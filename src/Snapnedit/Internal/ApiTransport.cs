using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Snapnedit.Internal;

/// <summary>One api response: its status and its parsed body (when there was one).</summary>
internal sealed class ApiResponse : IDisposable
{
    private readonly JsonDocument? _document;

    internal ApiResponse(int status, JsonDocument? document)
    {
        Status = status;
        _document = document;
    }

    internal int Status { get; }

    internal JsonElement Root => _document is null ? default : _document.RootElement;

    public void Dispose() => _document?.Dispose();
}

/// <summary>
/// The HTTP pipeline: absolute-url resolution, bearer auth, the uniform error
/// envelope, and backoff retries for idempotent requests.
/// </summary>
internal sealed class ApiTransport : IDisposable
{
    private static readonly string PackageVersion =
        typeof(ApiTransport).GetTypeInfo().Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SnapneditClientOptions _options;
    private readonly string _apiKey;
    private readonly string _userAgent;

    internal ApiTransport(string apiKey, SnapneditClientOptions options)
    {
        _apiKey = apiKey;
        _options = options;
        _ownsHttp = options.HttpClient is null;
        _http = options.HttpClient ?? new HttpClient();
        _userAgent = options.UserAgent ?? ("snapnedit-dotnet/" + PackageVersion);
        BaseUrl = options.BaseUrl.EndsWith("/", StringComparison.Ordinal)
            ? options.BaseUrl.Substring(0, options.BaseUrl.Length - 1)
            : options.BaseUrl;
    }

    /// <summary>The api origin, with any trailing slash removed.</summary>
    internal string BaseUrl { get; }

    /// <summary>Absolute url for an api path such as <c>/jobs</c>.</summary>
    internal string ApiUrl(string path) => BaseUrl + path;

    /// <summary>
    /// Resolves a presigned url against the base url: an absolute url passes
    /// through, a same-origin path (local storage signs <c>/_local/…</c>) is
    /// resolved against the api origin.
    /// </summary>
    internal string ResolvePresigned(string maybeRelative) =>
        new Uri(new Uri(BaseUrl + "/", UriKind.Absolute), maybeRelative).ToString();

    /// <summary>Sends a request and parses a JSON body. Non-2xx throws a typed <see cref="SnapneditException"/>.</summary>
    internal async Task<ApiResponse> SendJsonAsync(
        HttpMethod method,
        string url,
        Func<HttpContent>? content,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(method, url, content, authenticated, IsIdempotent(method), cancellationToken)
            .ConfigureAwait(false);
        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw await ToExceptionAsync(response, url).ConfigureAwait(false);
            }

            if (status == 204 || response.Content is null)
            {
                return new ApiResponse(status, null);
            }

            var body = await ReadStringAsync(response).ConfigureAwait(false);
            if (body.Length == 0)
            {
                return new ApiResponse(status, null);
            }

            try
            {
                return new ApiResponse(status, JsonDocument.Parse(body, Json.DocumentOptions));
            }
            catch (JsonException cause)
            {
                throw new SnapneditException(
                    ErrorCode.Internal,
                    url + " returned a non-JSON " + status.ToString(CultureInfo.InvariantCulture) + " response",
                    status,
                    cause);
            }
        }
    }

    /// <summary>Sends a request that answers <c>204 No Content</c>.</summary>
    internal async Task SendVoidAsync(
        HttpMethod method,
        string url,
        Func<HttpContent>? content,
        CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(method, url, content, true, IsIdempotent(method), cancellationToken)
            .ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ToExceptionAsync(response, url).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Follows a signed url and returns the bytes plus their content type.</summary>
    internal async Task<(byte[] Bytes, string Mime)> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(HttpMethod.Get, url, null, false, true, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new SnapneditException(
                    ErrorCode.Internal,
                    "download from " + url + " failed with status " +
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    (int)response.StatusCode);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var mime = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            return (bytes, mime);
        }
    }

    /// <summary>
    /// PUTs bytes to a presigned url. Deliberately unauthenticated: a presigned
    /// url is self-authenticating, and some schemes (S3 SigV4 among them)
    /// reject a second <c>Authorization</c> header outright.
    /// </summary>
    internal async Task PutPresignedAsync(
        string url,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(
            HttpMethod.Put,
            url,
            () => BinaryContent(bytes, contentType),
            false,
            true,
            cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new SnapneditException(
                    ErrorCode.Internal,
                    "PUT to " + url + " failed with status " +
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    (int)response.StatusCode);
            }
        }
    }

    /// <summary>POSTs a JSON body and returns the raw response bytes (a rendered image or PDF).</summary>
    internal async Task<byte[]> PostForBytesAsync(
        string url,
        Func<HttpContent> content,
        CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(HttpMethod.Post, url, content, true, false, cancellationToken)
            .ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ToExceptionAsync(response, url).ConfigureAwait(false);
            }

            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
    }

    internal static HttpContent BinaryContent(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    internal static HttpContent JsonContent(Action<Utf8JsonWriter> write)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Json.WriterOptions))
        {
            write(writer);
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    internal static HttpContent EmptyJsonContent() => JsonContent(writer =>
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    });

    private static bool IsIdempotent(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Put || method == HttpMethod.Delete || method == HttpMethod.Head;

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string url,
        Func<HttpContent>? content,
        bool authenticated,
        bool idempotent,
        CancellationToken cancellationToken)
    {
        var attempts = idempotent ? Math.Max(0, _options.MaxRetries) + 1 : 1;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.RequestTimeout > TimeSpan.Zero)
            {
                timeout.CancelAfter(_options.RequestTimeout);
            }

            var request = new HttpRequestMessage(method, url);
            if (content is not null)
            {
                request.Content = content();
            }

            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (_options.DefaultHeaders is not null)
            {
                foreach (var header in _options.DefaultHeaders)
                {
                    if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception cause) when (cause is HttpRequestException ||
                                          (cause is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                request.Dispose();
                if (attempt >= attempts)
                {
                    throw new SnapneditException(
                        ErrorCode.Internal,
                        "network error calling " + url + ": " + cause.Message,
                        0,
                        cause);
                }

                await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // The request message owns the content stream; the response is
            // fully buffered by ResponseContentRead, so the request can go now.
            request.Dispose();

            if (attempt < attempts && ShouldRetryStatus((int)response.StatusCode))
            {
                var retryAfter = response.Headers.RetryAfter;
                response.Dispose();
                await DelayAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private static bool ShouldRetryStatus(int status) => status == 429 || status >= 500;

    private async Task DelayAsync(int attempt, RetryConditionHeaderValue? retryAfter, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(
            _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        if (delay > _options.RetryMaxDelay)
        {
            delay = _options.RetryMaxDelay;
        }

        if (retryAfter is not null)
        {
            var hinted = retryAfter.Delta ??
                         (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : (TimeSpan?)null);
            if (hinted.HasValue && hinted.Value > TimeSpan.Zero && hinted.Value <= _options.RetryMaxDelay)
            {
                delay = hinted.Value;
            }
        }

        // Half-to-full jitter, so a fleet of clients does not retry in lockstep.
        var jittered = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.5 + (NextJitter() * 0.5)));
        await Task.Delay(jittered, cancellationToken).ConfigureAwait(false);
    }

    [ThreadStatic]
    private static Random? _jitter;

    private static double NextJitter()
    {
        _jitter ??= new Random(unchecked(Environment.TickCount * 31) ^ Thread.CurrentThread.ManagedThreadId);
        return _jitter.NextDouble();
    }

    private static async Task<string> ReadStringAsync(HttpResponseMessage response) =>
        response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

    /// <summary>Turns a non-2xx response into the typed exception its <c>{ error: { code, message } }</c> envelope describes.</summary>
    private static async Task<SnapneditException> ToExceptionAsync(HttpResponseMessage response, string url)
    {
        var status = (int)response.StatusCode;
        var fallback = "request to " + url + " failed with status " + status.ToString(CultureInfo.InvariantCulture);

        string body;
        try
        {
            body = await ReadStringAsync(response).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new SnapneditException(ErrorCode.Internal, fallback, status);
        }

        if (body.Length == 0)
        {
            return new SnapneditException(StatusFallbackCode(status), fallback, status);
        }

        try
        {
            using var document = JsonDocument.Parse(body, Json.DocumentOptions);
            var error = Json.ObjectProperty(document.RootElement, "error");
            if (error is null)
            {
                return new SnapneditException(StatusFallbackCode(status), fallback, status);
            }

            var code = ErrorCodes.Parse(Json.OptionalString(error.Value, "code"));
            var message = Json.OptionalString(error.Value, "message");
            return new SnapneditException(code, string.IsNullOrEmpty(message) ? fallback : message!, status);
        }
        catch (JsonException)
        {
            return new SnapneditException(StatusFallbackCode(status), fallback, status);
        }
    }

    /// <summary>The best code for a non-2xx that did not carry a parseable envelope.</summary>
    private static ErrorCode StatusFallbackCode(int status) => status switch
    {
        400 => ErrorCode.InvalidInput,
        401 => ErrorCode.Unauthorized,
        402 => ErrorCode.PaymentRequired,
        403 => ErrorCode.Forbidden,
        404 => ErrorCode.NotFound,
        413 => ErrorCode.TooLarge,
        429 => ErrorCode.RateLimited,
        _ => ErrorCode.Internal,
    };

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

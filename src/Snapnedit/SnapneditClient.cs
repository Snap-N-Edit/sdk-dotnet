using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Snapnedit.Internal;

namespace Snapnedit;

/// <summary>
/// The snapnedit api client: upload an image, run an operation, poll the job,
/// download the result — or hand the api a url and a destination and never
/// touch the bytes at all.
/// </summary>
/// <remarks>
/// <para>
/// The client is thread-safe and meant to be long-lived: construct one per
/// process (or register it as a singleton) rather than one per request. Inject
/// an <see cref="System.Net.Http.HttpClient"/> through
/// <see cref="SnapneditClientOptions.HttpClient"/> to reuse your own handler
/// pipeline; the client then never disposes it.
/// </para>
/// <example>
/// <code>
/// using var client = new SnapneditClient(Environment.GetEnvironmentVariable("SNAPNEDIT_API_KEY")!);
/// var result = await client.RunAsync(Operations.RemoveBackground, JobInput.FromFile("cat.png"));
/// await File.WriteAllBytesAsync("cat-cutout.png", result.Output!);
/// </code>
/// </example>
/// </remarks>
public sealed class SnapneditClient : IDisposable
{
    private readonly ApiTransport _transport;

    /// <summary>Creates a client.</summary>
    /// <param name="apiKey">A secret <c>sk_</c> API key, or an embed token. Sent as <c>Authorization: Bearer</c> on every api request.</param>
    /// <param name="options">Base url, injected <see cref="System.Net.Http.HttpClient"/>, timeouts and retry policy. Every field has a working default.</param>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is null or blank.</exception>
    public SnapneditClient(string apiKey, SnapneditClientOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("an api key is required", nameof(apiKey));
        }

        Options = options ?? new SnapneditClientOptions();
        _transport = new ApiTransport(apiKey, Options);
        Destinations = new DestinationsClient(_transport);
        Embed = new EmbedClient(_transport);
        Designs = new DesignsClient(_transport);
        WebhookEndpoints = new WebhookEndpointsClient(_transport);
    }

    /// <summary>The options this client was built with.</summary>
    public SnapneditClientOptions Options { get; }

    /// <summary>Your account's saved storage destinations.</summary>
    public DestinationsClient Destinations { get; }

    /// <summary>Minting embed-editor session tokens.</summary>
    public EmbedClient Embed { get; }

    /// <summary>Server-side design rendering.</summary>
    public DesignsClient Designs { get; }

    /// <summary>Your account's webhook endpoints.</summary>
    public WebhookEndpointsClient WebhookEndpoints { get; }

    /// <summary>
    /// <c>GET /operations</c> — the operation catalog: accepted mime types,
    /// maximum input dimension, params JSON Schema and whether a mask is
    /// required. Public; no credential needed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every operation this deployment serves.</returns>
    /// <exception cref="SnapneditException">The api answered non-2xx, or with a body this client could not read.</exception>
    public async Task<IReadOnlyList<OperationMetadata>> ListOperationsAsync(CancellationToken cancellationToken = default)
    {
        var url = _transport.ApiUrl("/operations");
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        if (response.Root.ValueKind != JsonValueKind.Array)
        {
            throw Json.Malformed(url, "expected an array of operations");
        }

        var operations = new List<OperationMetadata>();
        foreach (var entry in response.Root.EnumerateArray())
        {
            operations.Add(ApiParse.OperationMetadata(entry, url));
        }

        return operations;
    }

    /// <summary>
    /// Uploads one asset: <c>POST /uploads</c> → <c>PUT &lt;presigned url&gt;</c>
    /// → <c>POST /uploads/{id}/confirm</c>. The confirm step is not optional:
    /// until it runs the asset carries a placeholder hash and cannot take part
    /// in content-based caching.
    /// </summary>
    /// <param name="bytes">The encoded image (png, jpeg or webp).</param>
    /// <param name="mime">The content type to declare. The api accepts only <c>image/png</c>, <c>image/jpeg</c> and <c>image/webp</c>.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The asset id, its content hash and its byte length.</returns>
    /// <exception cref="SnapneditException">The mime is unsupported, the bytes are too large, or a step failed.</exception>
    public Task<UploadResult> UploadAsync(byte[] bytes, string? mime = null, CancellationToken cancellationToken = default) =>
        UploadAsync(JobInput.FromBytes(bytes, mime), cancellationToken);

    /// <summary>Uploads one asset from any byte-bearing <see cref="JobInput"/>.</summary>
    /// <param name="input">Bytes, a stream or a file. A url or asset-id input has nothing to upload.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The asset id, its content hash and its byte length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="input"/> carries no bytes.</exception>
    /// <exception cref="SnapneditException">The api refused the upload.</exception>
    public async Task<UploadResult> UploadAsync(JobInput input, CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var bytes = await input.ReadBytesAsync(cancellationToken).ConfigureAwait(false);
        var mime = input.Mime ?? "application/octet-stream";

        var createUrl = _transport.ApiUrl("/uploads");
        string assetId;
        string uploadUrl;
        using (var created = await _transport.SendJsonAsync(
                   HttpMethod.Post,
                   createUrl,
                   () => ApiTransport.JsonContent(writer =>
                   {
                       writer.WriteStartObject();
                       writer.WriteString("mime", mime);
                       writer.WriteNumber("bytes", bytes.Length);
                       writer.WriteEndObject();
                   }),
                   true,
                   cancellationToken).ConfigureAwait(false))
        {
            assetId = Json.RequireString(created.Root, "assetId", createUrl);
            var signed = Json.ObjectProperty(created.Root, "upload");
            if (signed is null)
            {
                throw Json.Malformed(createUrl, "missing object field \"upload\"");
            }

            uploadUrl = _transport.ResolvePresigned(ApiParse.SignedUrl(signed.Value, createUrl).Url);
        }

        await _transport.PutPresignedAsync(uploadUrl, bytes, mime, cancellationToken).ConfigureAwait(false);

        var confirmUrl = _transport.ApiUrl("/uploads/" + Uri.EscapeDataString(assetId) + "/confirm");
        using var confirmed = await _transport
            .SendJsonAsync(HttpMethod.Post, confirmUrl, null, true, cancellationToken)
            .ConfigureAwait(false);
        return new UploadResult(
            Json.RequireString(confirmed.Root, "assetId", confirmUrl),
            Json.OptionalString(confirmed.Root, "contentHash") ?? string.Empty,
            Json.OptionalLong(confirmed.Root, "bytes") ?? bytes.Length);
    }

    /// <summary>
    /// <c>POST /jobs</c>. A byte-bearing <paramref name="input"/> is uploaded
    /// first; a url input is handed to the server to fetch, and an asset id is
    /// used as-is.
    /// </summary>
    /// <param name="operation">One of <see cref="Operations.All"/>.</param>
    /// <param name="input">Where the input image comes from.</param>
    /// <param name="options">Params and destination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The job id and its status. <c>202</c> for a fresh job; <c>200</c> with a
    /// status already <see cref="JobState.Succeeded"/> when the result cache
    /// answered it — a cache hit is never billed
    /// (<see cref="CreateJobResult.WasCacheHit"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> or <paramref name="input"/> is null.</exception>
    /// <exception cref="SnapneditException">Validation failed (400), credits ran out (402), or a named destination is not on this account (404).</exception>
    public async Task<CreateJobResult> CreateJobAsync(
        string operation,
        JobInput input,
        CreateJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var resolved = await ResolveInputAsync(input, cancellationToken).ConfigureAwait(false);
        var parameters = MergeParams(options, null);
        return await PostJobAsync(operation, resolved, parameters, options?.Destination, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary><c>GET /jobs/{id}</c>. Returns any state, including <c>failed</c> — it never throws for a failed job.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The job's status and its input/destination/delivery envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="jobId"/> is null.</exception>
    /// <exception cref="SnapneditException">
    /// <see cref="ErrorCode.NotFound"/> for a job id that does not exist — and
    /// equally for one that belongs to another account: an id must never become
    /// an existence oracle.
    /// </exception>
    public async Task<JobView> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (jobId is null)
        {
            throw new ArgumentNullException(nameof(jobId));
        }

        var url = _transport.ApiUrl("/jobs/" + Uri.EscapeDataString(jobId));
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        return ApiParse.JobView(jobId, response.Root, url);
    }

    /// <summary>
    /// Polls <c>GET /jobs/{id}</c> until the job has SETTLED: terminal, and — if
    /// it has a destination — with its delivery no longer pending.
    /// </summary>
    /// <remarks>
    /// That second half matters for exactly one case: a cache hit WITH a
    /// destination comes back already <c>succeeded</c> but with
    /// <c>delivery: pending</c>, because pushing the bytes to your bucket is
    /// still the worker's work. Stopping at "terminal" there would report
    /// pending as if it were the outcome.
    /// </remarks>
    /// <param name="jobId">The job to wait for.</param>
    /// <param name="options">Poll interval and total budget.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The settled job.</returns>
    /// <exception cref="SnapneditTimeoutException">The budget ran out first.</exception>
    public async Task<JobView> WaitForJobAsync(
        string jobId,
        WaitForJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var wait = options ?? new WaitForJobOptions();
        var deadline = DateTime.UtcNow + wait.Timeout;

        while (true)
        {
            var view = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (view.IsSettled)
            {
                return view;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new SnapneditTimeoutException(
                    "polling job " + jobId + " exceeded " +
                    wait.Timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    "ms without reaching a settled state");
            }

            var delay = wait.PollInterval < remaining ? wait.PollInterval : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The whole flow: upload the input (and the mask, if any) → <c>POST /jobs</c>
    /// → poll until settled → download the result.
    /// </summary>
    /// <param name="operation">One of <see cref="Operations.All"/>.</param>
    /// <param name="input">Bytes, a stream, a file, an asset id, or a url for the SERVER to fetch.</param>
    /// <param name="options">Params, mask, destination, poll timings, and whether to download.</param>
    /// <param name="cancellationToken">Cancels the whole flow.</param>
    /// <returns>
    /// The result. <see cref="RunResult.Output"/> holds the bytes unless the
    /// download was skipped — see <see cref="RunOptions.Download"/>.
    /// </returns>
    /// <exception cref="SnapneditException">The api refused the request, or the job failed (the job's own <c>errorCode</c> becomes <see cref="SnapneditException.Code"/>).</exception>
    /// <exception cref="SnapneditTimeoutException">Polling ran out of time.</exception>
    public async Task<RunResult> RunAsync(
        string operation,
        JobInput input,
        RunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var run = options ?? new RunOptions();
        var resolved = await ResolveInputAsync(input, cancellationToken).ConfigureAwait(false);

        string? maskAssetId = null;
        if (run.Mask is not null)
        {
            maskAssetId = (await UploadAsync(run.Mask, cancellationToken).ConfigureAwait(false)).AssetId;
        }

        var parameters = MergeParams(run, maskAssetId);
        var created = await PostJobAsync(operation, resolved, parameters, run.Destination, cancellationToken)
            .ConfigureAwait(false);

        // A cache hit that ALSO has to be delivered comes back terminal with a
        // still-pending delivery — poll that through, or the caller would be
        // told "pending" forever and never learn the outcome.
        var settled = created.Status.IsTerminal &&
                      !(created.Delivery is not null && created.Delivery.Status == JobDeliveryStatus.Pending)
            ? created.ToJobView()
            : await WaitForJobAsync(created.JobId, run.ToWaitOptions(), cancellationToken).ConfigureAwait(false);

        switch (settled.State)
        {
            case JobState.Succeeded:
            {
                var delivered = settled.Delivery is not null && settled.Delivery.Status == JobDeliveryStatus.Delivered;
                // Only an EXPLICIT destination flips the default. An account
                // default applied server-side does not: a caller who wrote
                // RunAsync(...) and nothing else still expects bytes back, and
                // their bucket copy is a bonus rather than a replacement.
                var askedForDelivery = run.Destination is not null && run.Destination is not JobDestination.NullDestination;
                var shouldDownload = run.Download ?? !(askedForDelivery && delivered);
                var download = settled.Status.Download;

                if (!shouldDownload || download is null)
                {
                    return new RunResult(
                        settled.JobId,
                        false,
                        null,
                        null,
                        settled.Status.OutputAssetId,
                        download,
                        settled.InputKind,
                        settled.Destination,
                        settled.Delivery,
                        settled.CreditCost,
                        settled.Cached,
                        settled.DeliveryOnly,
                        settled.Test);
                }

                var fetched = await _transport
                    .GetBytesAsync(_transport.ResolvePresigned(download.Url), cancellationToken)
                    .ConfigureAwait(false);
                return new RunResult(
                    settled.JobId,
                    true,
                    fetched.Bytes,
                    fetched.Mime,
                    settled.Status.OutputAssetId,
                    download,
                    settled.InputKind,
                    settled.Destination,
                    settled.Delivery,
                    settled.CreditCost,
                    settled.Cached,
                    settled.DeliveryOnly,
                    settled.Test);
            }

            case JobState.Failed:
                throw new SnapneditException(
                    settled.Status.ErrorCode ?? ErrorCode.Internal,
                    settled.Status.Message ?? "job failed",
                    200);
            case JobState.Canceled:
                throw new SnapneditException(ErrorCode.Internal, "job " + settled.JobId + " was canceled", 200);
            default:
                throw new SnapneditException(
                    ErrorCode.Internal,
                    "job " + settled.JobId + " resolved to non-terminal state " + settled.State + " outside of polling",
                    0);
        }
    }

    /// <summary>
    /// <c>GET /usage</c> — what this account has run, what it cost and where it
    /// came from, bucketed along ONE dimension at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is optional: <c>GetUsageAsync()</c> is the last 30 days
    /// grouped by day. Every <c>POST /jobs</c> writes one row — a cache hit
    /// included — so <see cref="UsageFacts.Jobs"/> counts REQUESTS and
    /// <see cref="UsageFacts.CacheHits"/> says how many of them ran no model.
    /// </para>
    /// <para>
    /// An EMBED token is scoped to its own key: <see cref="UsageQuery.KeyId"/>
    /// is forced to it and <see cref="UsageReport.Keys"/> comes back empty. A
    /// session or <c>sk_</c> caller sees the whole account.
    /// </para>
    /// </remarks>
    /// <param name="query">Window, grouping and filters. <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The account's usage over the resolved window.</returns>
    /// <exception cref="SnapneditException">
    /// <see cref="ErrorCode.Unauthorized"/> without an account credential;
    /// <see cref="ErrorCode.InvalidInput"/> when <c>from</c> is after <c>to</c>
    /// or the range exceeds 366 days.
    /// </exception>
    public async Task<UsageReport> GetUsageAsync(
        UsageQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var url = _transport.ApiUrl("/usage" + UsageQueryString(query));
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        return ApiParse.UsageReport(response.Root, url);
    }

    /// <summary>
    /// <c>GET /account</c> — who this secret key belongs to, the account's
    /// credit balance, and whether the key is live or <c>sk_test_</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The account, its balance and the key's mode.</returns>
    /// <exception cref="SnapneditException">
    /// <see cref="ErrorCode.Unauthorized"/> for anything but a secret key — an
    /// embed token included.
    /// </exception>
    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var url = _transport.ApiUrl("/account");
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        return ApiParse.Account(response.Root, url);
    }

    /// <summary><c>GET /jobs?limit=N</c> — the account's most recent jobs, newest first. Secret key only.</summary>
    /// <param name="query">Limit (1–100, default 20), state and lane filters. <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The jobs, each read exactly as <see cref="GetJobAsync"/> would, plus its creation time.</returns>
    /// <exception cref="SnapneditException">
    /// <see cref="ErrorCode.InvalidInput"/> for a limit outside 1–100;
    /// <see cref="ErrorCode.Unauthorized"/> without a secret key.
    /// </exception>
    public async Task<IReadOnlyList<JobListItem>> ListJobsAsync(
        JobListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        // `limit` is always sent: without it GET /jobs is the older count endpoint.
        var parts = new List<string> { "limit=" + (query?.Limit ?? 20).ToString(CultureInfo.InvariantCulture) };
        if (query?.Status is { } status)
        {
            parts.Add("status=" + JobStateWire(status));
        }

        if (query?.Priority is { } priority)
        {
            parts.Add("priority=" + (priority == JobPriority.Background ? "background" : "interactive"));
        }

        var url = _transport.ApiUrl("/jobs?" + string.Join("&", parts));
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        var jobs = Json.Property(response.Root, "jobs");
        if (jobs is null || jobs.Value.ValueKind != JsonValueKind.Array)
        {
            throw Json.Malformed(url, "missing array field \"jobs\"");
        }

        var items = new List<JobListItem>();
        foreach (var entry in jobs.Value.EnumerateArray())
        {
            items.Add(ApiParse.JobListItem(entry, url));
        }

        return items;
    }

    /// <summary>Releases the internally created <see cref="System.Net.Http.HttpClient"/>. An injected one is left alone.</summary>
    public void Dispose() => _transport.Dispose();

    /// <summary>
    /// <c>?from=…&amp;groupBy=…</c> for a query. An unset field is DROPPED
    /// rather than sent empty: the api distinguishes "no filter" (count
    /// everything) from a filter, and an empty one would match nothing.
    /// </summary>
    private static string UsageQueryString(UsageQuery? query)
    {
        if (query is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        AppendUsageFilter(parts, "from", FormatInstant(query.From));
        AppendUsageFilter(parts, "to", FormatInstant(query.To));
        AppendUsageFilter(parts, "groupBy", query.GroupBy.HasValue ? UsageGroupBys.ToWire(query.GroupBy.Value) : null);
        AppendUsageFilter(parts, "keyId", query.KeyId);
        AppendUsageFilter(parts, "origin", query.Origin);
        AppendUsageFilter(parts, "operation", query.Operation);
        AppendUsageFilter(parts, "source", query.Source.HasValue ? UsageSources.ToWire(query.Source.Value) : null);
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static void AppendUsageFilter(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parts.Add(name + "=" + Uri.EscapeDataString(value!));
        }
    }

    private static string JobStateWire(JobState state) => state switch
    {
        JobState.Queued => "queued",
        JobState.Processing => "processing",
        JobState.Succeeded => "succeeded",
        JobState.Failed => "failed",
        JobState.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>
    /// An instant in the exact shape the api parses: UTC, milliseconds, <c>Z</c>.
    /// Round-tripping <c>"o"</c> instead would emit seven fractional digits and
    /// a numeric offset, which is a wider format than the contract promises.
    /// </summary>
    private static string? FormatInstant(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private async Task<ResolvedInput> ResolveInputAsync(JobInput input, CancellationToken cancellationToken)
    {
        if (input.Source == JobInputSource.Url)
        {
            return new ResolvedInput(null, input.Url);
        }

        if (input.Source == JobInputSource.AssetId)
        {
            return new ResolvedInput(input.AssetId, null);
        }

        var uploaded = await UploadAsync(input, cancellationToken).ConfigureAwait(false);
        return new ResolvedInput(uploaded.AssetId, null);
    }

    private static IReadOnlyDictionary<string, object?> MergeParams(CreateJobOptions? options, string? maskAssetId)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options?.TypedParams is not null)
        {
            foreach (var pair in options.TypedParams.ToParams())
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (options?.Params is not null)
        {
            foreach (var pair in options.Params)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (maskAssetId is not null)
        {
            merged["maskAssetId"] = maskAssetId;
        }

        return merged;
    }

    private async Task<CreateJobResult> PostJobAsync(
        string operation,
        ResolvedInput input,
        IReadOnlyDictionary<string, object?> parameters,
        JobDestination? destination,
        CancellationToken cancellationToken)
    {
        var url = _transport.ApiUrl("/jobs");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(writer => WriteJobBody(writer, operation, input, parameters, destination)),
            true,
            cancellationToken).ConfigureAwait(false);
        return ApiParse.CreateJobResult(response.Root, response.Status, url);
    }

    private static void WriteJobBody(
        Utf8JsonWriter writer,
        string operation,
        ResolvedInput input,
        IReadOnlyDictionary<string, object?> parameters,
        JobDestination? destination)
    {
        writer.WriteStartObject();
        writer.WriteString("operation", operation);

        // Exactly one of inputAssetId / inputUrl — the api rejects a body
        // carrying both or neither.
        if (input.Url is not null)
        {
            writer.WriteString("inputUrl", input.Url);
        }
        else
        {
            writer.WriteString("inputAssetId", input.AssetId);
        }

        writer.WritePropertyName("params");
        writer.WriteStartObject();
        foreach (var pair in parameters)
        {
            writer.WritePropertyName(pair.Key);
            Json.WriteValue(writer, "params." + pair.Key, pair.Value);
        }

        writer.WriteEndObject();

        // Omitted means "apply my account default, if I have one"; an explicit
        // null opts out. They are NOT the same thing.
        switch (destination)
        {
            case null:
                break;
            case JobDestination.NullDestination:
                writer.WriteNull("destination");
                break;
            case JobDestination.PresignedPutDestination presigned:
                writer.WritePropertyName("destination");
                writer.WriteStartObject();
                writer.WriteString("type", "presigned-put");
                writer.WriteString("url", presigned.Url);
                if (presigned.Headers is not null && presigned.Headers.Count > 0)
                {
                    writer.WritePropertyName("headers");
                    writer.WriteStartObject();
                    foreach (var header in presigned.Headers)
                    {
                        writer.WriteString(header.Key, header.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                break;
            case JobDestination.SavedDestination saved:
                writer.WritePropertyName("destination");
                writer.WriteStartObject();
                writer.WriteString("type", "saved");
                writer.WriteString("id", saved.Id);
                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(destination), "unknown destination kind");
        }

        writer.WriteEndObject();
    }

    private readonly struct ResolvedInput
    {
        internal ResolvedInput(string? assetId, string? url)
        {
            AssetId = assetId;
            Url = url;
        }

        internal string? AssetId { get; }

        internal string? Url { get; }
    }
}

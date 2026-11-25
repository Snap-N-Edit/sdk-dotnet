using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Snapnedit.Tests.Conformance;

/// <summary>One raw HTTP response, kept as text AND as parsed JSON.</summary>
public sealed class RestResponse
{
    internal RestResponse(int status, string text, JsonElement json, string? contentType)
    {
        Status = status;
        Text = text;
        Json = json;
        ContentType = contentType;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The body, verbatim.</summary>
    public string Text { get; }

    /// <summary>The body parsed as JSON, or <c>default</c> when it was not JSON.</summary>
    public JsonElement Json { get; }

    /// <summary>The response content type.</summary>
    public string? ContentType { get; }

    /// <summary>The <c>error.code</c> of an error envelope.</summary>
    public string? ErrorCode =>
        Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error) &&
        error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;

    /// <summary>The <c>error.message</c> of an error envelope.</summary>
    public string? ErrorMessage =>
        Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error) &&
        error.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
}

/// <summary>
/// Plain HTTP against the conformance server, for the scenario steps that are
/// about the WIRE (exact status codes, requests with no credential, the helper
/// endpoints) rather than about the SDK.
/// </summary>
public sealed class Rest
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string? _token;

    internal Rest(HttpClient http, string baseUrl, string? token)
    {
        _http = http;
        _base = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _token = token;
    }

    /// <summary>Sends a request with an optional JSON body.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">A path (resolved against the server) or an absolute url.</param>
    /// <param name="json">A JSON body, or <see langword="null"/> for none.</param>
    /// <param name="authorization">An <c>Authorization</c> value overriding this client's own.</param>
    /// <returns>The response.</returns>
    public async Task<RestResponse> SendAsync(string method, string path, string? json = null, string? authorization = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Resolve(path));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var auth = authorization ?? (_token is null ? null : "Bearer " + _token);
        if (auth is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new RestResponse((int)response.StatusCode, text, TryParse(text), response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>Sends a request with a binary body and explicit headers.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">A path or an absolute url.</param>
    /// <param name="bytes">The body.</param>
    /// <param name="headers">Headers to send verbatim (a presigned PUT's signature covers them).</param>
    /// <returns>The response.</returns>
    public async Task<RestResponse> SendBytesAsync(
        string method,
        string path,
        byte[] bytes,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Resolve(path))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = null;
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                }
                else
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new RestResponse((int)response.StatusCode, text, TryParse(text), response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>Fetches raw bytes.</summary>
    /// <param name="path">A path or an absolute url.</param>
    /// <returns>The status, the bytes and the content type.</returns>
    public async Task<(int Status, byte[] Bytes, string? ContentType)> GetBytesAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(path));
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return ((int)response.StatusCode, bytes, response.Content.Headers.ContentType?.ToString());
    }

    private Uri Resolve(string path) => new Uri(_base, path.TrimStart('/'));

    private static JsonElement TryParse(string text)
    {
        if (text.Length == 0)
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>
/// Boots <c>node scripts/conformance-server.mjs</c> from the monorepo root,
/// waits for its one line of JSON on stdout, and terminates it on teardown.
/// </summary>
/// <remarks>
/// This is exactly the harness <c>docs/sdk-conformance.md</c> describes for a
/// non-TypeScript SDK: a real OS process on a real (ephemeral) port, running
/// the production Fastify app and the production worker over an in-memory
/// store, with the stub provider. Assert on job lifecycle and metadata — never
/// on pixels.
/// </remarks>
public sealed class ConformanceServerFixture : IAsyncLifetime
{
    private readonly StringBuilder _stderr = new StringBuilder();
    private Process? _process;
    private HttpClient? _http;

    /// <summary>The monorepo root, or <see langword="null"/> in a standalone (mirrored) checkout.</summary>
    public static string? RepoRoot { get; } = FindRepoRoot();

    /// <summary>The server's base url, e.g. <c>http://127.0.0.1:53211</c>.</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>A secret <c>sk_live_</c> key for the seeded account.</summary>
    public string ApiKey { get; private set; } = string.Empty;

    /// <summary>A secret <c>sk_test_</c> key on the same account: free jobs, placeholder results.</summary>
    public string TestApiKey { get; private set; } = string.Empty;

    /// <summary>A <c>pk_live_</c> publishable key on the same account.</summary>
    public string PublishableKey { get; private set; } = string.Empty;

    /// <summary>The fixed webhook signing secret.</summary>
    public string WebhookSecret { get; private set; } = string.Empty;

    /// <summary>The seeded account id.</summary>
    public string AccountId { get; private set; } = string.Empty;

    /// <summary>The stand-in bucket name.</summary>
    public string Bucket { get; private set; } = "conformance-bucket";

    /// <summary>The account's starting credit balance (1000).</summary>
    public int StartingCredits { get; private set; }

    /// <summary>The SDK, authenticated with the seeded account's secret key.</summary>
    public SnapneditClient Api { get; private set; } = null!;

    /// <summary>Raw HTTP with the secret key.</summary>
    public Rest Authenticated { get; private set; } = null!;

    /// <summary>Raw HTTP with no credential at all.</summary>
    public Rest Anonymous { get; private set; } = null!;

    /// <summary><c>test/conformance/scenarios.json</c>, parsed.</summary>
    public JsonElement Scenarios { get; private set; }

    /// <summary>Everything the server printed on stderr, for a failure message.</summary>
    public string Diagnostics => _stderr.ToString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var root = RepoRoot ?? throw new InvalidOperationException(
            "the snapnedit monorepo was not found above " + AppContext.BaseDirectory +
            ". The conformance suite drives `node scripts/conformance-server.mjs` from the repo root; " +
            "in a standalone checkout run `dotnet test --filter Category!=Conformance`.");

        Scenarios = JsonDocument
            .Parse(File.ReadAllText(Path.Combine(root, "test", "conformance", "scenarios.json")))
            .RootElement.Clone();

        var startInfo = new ProcessStartInfo("node", "scripts/conformance-server.mjs")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Port 0: an ephemeral port, so this suite never collides with a
        // manually started server on 8790 or with a sibling SDK's suite.
        startInfo.Environment["CONFORMANCE_PORT"] = "0";

        _process = Process.Start(startInfo) ??
                   throw new InvalidOperationException("could not start `node scripts/conformance-server.mjs`");

        // Drain stderr continuously: the banner and any warning go there, and a
        // full pipe would deadlock the server before it ever prints its ready line.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(line);
                }
            }
        });

        var readyLine = _process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(readyLine, Task.Delay(TimeSpan.FromMinutes(3))).ConfigureAwait(false);
        if (completed != readyLine || readyLine.Result is null)
        {
            throw new InvalidOperationException(
                "the conformance server never printed its ready line. Did you run `npm ci && npm run build`?\n" +
                Diagnostics);
        }

        using var ready = JsonDocument.Parse(readyLine.Result);
        var root0 = ready.RootElement;
        Url = root0.GetProperty("url").GetString()!;
        ApiKey = root0.GetProperty("apiKey").GetString()!;
        TestApiKey = root0.GetProperty("testApiKey").GetString()!;
        PublishableKey = root0.GetProperty("publishableKey").GetString()!;
        WebhookSecret = root0.GetProperty("webhookSecret").GetString()!;
        AccountId = root0.GetProperty("accountId").GetString()!;
        Bucket = root0.TryGetProperty("bucket", out var bucket) ? bucket.GetString()! : "conformance-bucket";
        StartingCredits = root0.TryGetProperty("startingCredits", out var credits) ? credits.GetInt32() : 1000;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        Authenticated = new Rest(_http, Url, ApiKey);
        Anonymous = new Rest(_http, Url, null);
        Api = NewClient(ApiKey);
    }

    /// <summary>A second SDK client against the same server, holding a different credential.</summary>
    /// <param name="credential">An API key or an embed token.</param>
    /// <returns>The client. Dispose it when done.</returns>
    public SnapneditClient NewClient(string credential) =>
        new SnapneditClient(credential, new SnapneditClientOptions
        {
            BaseUrl = Url,
            HttpClient = _http,
            // The stub provider is instant; a 1s default poll would add a second
            // of wall clock to every job scenario.
            RequestTimeout = TimeSpan.FromSeconds(60),
        });

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        Api?.Dispose();

        if (_process is not null && !_process.HasExited)
        {
            // SIGTERM is the documented shutdown: it closes the listener, stops
            // the worker loop and removes the temp storage directory. .NET's
            // Process.Kill() is SIGKILL on Unix, which would leave that behind,
            // so ask politely first and only then insist.
            TrySigterm(_process.Id);
            var exited = await Task.Run(() => _process.WaitForExit(10_000)).ConfigureAwait(false);
            if (!exited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone between the check and the kill.
                }
            }
        }

        _process?.Dispose();
        _http?.Dispose();
    }

    private static void TrySigterm(int pid)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("/bin/kill", "-TERM " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            kill?.WaitForExit(5_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No /bin/kill (Windows): the Kill(true) fallback handles it.
        }
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "conformance-server.mjs")) &&
                File.Exists(Path.Combine(directory.FullName, "test", "conformance", "scenarios.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

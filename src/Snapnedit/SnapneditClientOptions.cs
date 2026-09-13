using System;
using System.Net.Http;

namespace Snapnedit;

/// <summary>Construction options for <see cref="SnapneditClient"/>. Every one has a working default.</summary>
public sealed record SnapneditClientOptions
{
    /// <summary>The default api origin, used when <see cref="BaseUrl"/> is not set.</summary>
    public const string DefaultBaseUrl = "https://snapnedit.com/api";

    /// <summary>
    /// Origin of the snapnedit api. Defaults to <see cref="DefaultBaseUrl"/>;
    /// point it at <c>http://localhost:8787</c> for a local stack. A trailing
    /// slash is fine.
    /// </summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>
    /// An <see cref="System.Net.Http.HttpClient"/> to use for every request —
    /// inject one from <c>IHttpClientFactory</c>, or a test double. When set,
    /// the client never disposes it; when unset, it creates and owns one.
    /// </summary>
    /// <remarks>
    /// Only the message pipeline is borrowed: this client always sends absolute
    /// urls, so an injected client's <c>BaseAddress</c> is ignored and
    /// <see cref="BaseUrl"/> decides where requests go.
    /// </remarks>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Per-request timeout, applied on top of whatever the injected <see cref="HttpClient"/> enforces. Default 100 seconds.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// How many times to RETRY a failed idempotent request (so the default of
    /// 2 means up to 3 attempts). Retries cover network failures, 429 and 5xx,
    /// and apply only to GET, PUT and DELETE — never to a POST, which may have
    /// created a job and debited credits before the response was lost.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Base delay for the exponential, jittered retry backoff. Default 200ms.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Ceiling for one retry backoff. Default 5 seconds.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra headers sent on every api request (a proxy token, a trace id). <c>Authorization</c> is set by the client and cannot be overridden here.</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }

    /// <summary>The <c>User-Agent</c> to send. Defaults to <c>snapnedit-dotnet/&lt;version&gt;</c>.</summary>
    public string? UserAgent { get; init; }
}

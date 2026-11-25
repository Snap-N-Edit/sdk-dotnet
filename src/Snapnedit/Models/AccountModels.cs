using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>Whether a secret key is a live key or an <c>sk_test_</c> key.</summary>
public enum ApiKeyMode
{
    /// <summary>A live key: real model runs, billed. Wire value <c>"live"</c>.</summary>
    Live = 0,

    /// <summary>An <c>sk_test_</c> key: free, never cached, placeholder results. Wire value <c>"test"</c>.</summary>
    Test = 1,
}

/// <summary>The key a <c>GET /account</c> call was made with.</summary>
public sealed record AccountKey
{
    /// <summary>Creates an account key.</summary>
    /// <param name="id">The key's id.</param>
    /// <param name="mode">Live or test.</param>
    public AccountKey(string id, ApiKeyMode mode)
    {
        Id = id;
        Mode = mode;
    }

    /// <summary>The key's id — the one <see cref="UsageQuery.KeyId"/> filters on.</summary>
    public string Id { get; }

    /// <summary>Live or test.</summary>
    public ApiKeyMode Mode { get; }
}

/// <summary>What <c>GET /account</c> returns: who a secret key belongs to, its balance and its mode.</summary>
public sealed record AccountInfo
{
    /// <summary>Creates account info.</summary>
    /// <param name="accountId">The account id.</param>
    /// <param name="email">The account email, or <see langword="null"/>.</param>
    /// <param name="plan">The account's plan.</param>
    /// <param name="credits">The credit balance.</param>
    /// <param name="key">The key the request was made with.</param>
    public AccountInfo(string accountId, string? email, string plan, int credits, AccountKey key)
    {
        AccountId = accountId;
        Email = email;
        Plan = plan;
        Credits = credits;
        Key = key;
    }

    /// <summary>The account id.</summary>
    public string AccountId { get; }

    /// <summary>The account email, or <see langword="null"/> when there is none.</summary>
    public string? Email { get; }

    /// <summary>The account's plan, as an opaque string.</summary>
    public string Plan { get; }

    /// <summary>The credit balance.</summary>
    public int Credits { get; }

    /// <summary>The key the request was made with.</summary>
    public AccountKey Key { get; }
}

/// <summary>Which lane a job ran on.</summary>
public enum JobPriority
{
    /// <summary>The default lane. Wire value <c>"interactive"</c>.</summary>
    Interactive = 0,

    /// <summary>The bulk lane. Wire value <c>"background"</c>.</summary>
    Background = 1,
}

/// <summary>Filters for <see cref="SnapneditClient.ListJobsAsync"/>.</summary>
public sealed record JobListQuery
{
    /// <summary>How many jobs to return, 1–100. Defaults to 20.</summary>
    public int? Limit { get; init; }

    /// <summary>Only jobs in this state.</summary>
    public JobState? Status { get; init; }

    /// <summary>Only jobs on this lane.</summary>
    public JobPriority? Priority { get; init; }
}

/// <summary>One row of <c>GET /jobs?limit=</c>: the same body as <c>GET /jobs/{id}</c>, plus when the job was made.</summary>
public sealed record JobListItem
{
    /// <summary>Creates a list item.</summary>
    /// <param name="job">The job, exactly as <see cref="SnapneditClient.GetJobAsync"/> would read it.</param>
    /// <param name="createdAt">ISO-8601 timestamp the job was created.</param>
    public JobListItem(JobView job, string createdAt)
    {
        Job = job;
        CreatedAt = createdAt;
    }

    /// <summary>The job's status and envelope.</summary>
    public JobView Job { get; }

    /// <summary>Shorthand for <c>Job.JobId</c>.</summary>
    public string JobId => Job.JobId;

    /// <summary>ISO-8601 timestamp the job was created. The list is newest first.</summary>
    public string CreatedAt { get; }
}

/// <summary>The event types a webhook endpoint can subscribe to, as their wire strings.</summary>
public static class WebhookEndpointEvents
{
    /// <summary><c>job.succeeded</c>.</summary>
    public const string JobSucceeded = "job.succeeded";

    /// <summary><c>job.failed</c>.</summary>
    public const string JobFailed = "job.failed";

    /// <summary><c>session.ended</c> — an embed session's last token expired.</summary>
    public const string SessionEnded = "session.ended";

    /// <summary>Every event type, which is also what an endpoint created without <c>events</c> subscribes to.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { JobSucceeded, JobFailed, SessionEnded };
}

/// <summary>A webhook endpoint, as every read returns it — never its signing secret.</summary>
public sealed record WebhookEndpoint
{
    /// <summary>Creates a webhook endpoint.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="url">The https url deliveries are POSTed to.</param>
    /// <param name="events">The event types it receives.</param>
    /// <param name="active">Whether deliveries are being sent.</param>
    /// <param name="createdAt">ISO-8601 creation timestamp.</param>
    /// <param name="updatedAt">ISO-8601 last-update timestamp.</param>
    public WebhookEndpoint(
        string id,
        string accountId,
        string url,
        IReadOnlyList<string> events,
        bool active,
        string createdAt,
        string updatedAt)
    {
        Id = id;
        AccountId = accountId;
        Url = url;
        Events = events ?? throw new ArgumentNullException(nameof(events));
        Active = active;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>The endpoint id.</summary>
    public string Id { get; }

    /// <summary>The owning account.</summary>
    public string AccountId { get; }

    /// <summary>The https url deliveries are POSTed to.</summary>
    public string Url { get; }

    /// <summary>The event types it receives — see <see cref="WebhookEndpointEvents"/>. Kept as strings so a newer type never breaks a read.</summary>
    public IReadOnlyList<string> Events { get; }

    /// <summary>Whether deliveries are being sent. A paused endpoint keeps its secret.</summary>
    public bool Active { get; }

    /// <summary>ISO-8601 creation timestamp.</summary>
    public string CreatedAt { get; }

    /// <summary>ISO-8601 last-update timestamp.</summary>
    public string UpdatedAt { get; }
}

/// <summary>What <c>POST /webhook-endpoints</c> returns: the endpoint and its signing secret, shown this once.</summary>
public sealed record WebhookEndpointCreated
{
    /// <summary>Creates the result.</summary>
    /// <param name="endpoint">The new endpoint.</param>
    /// <param name="secret">Its plaintext signing secret.</param>
    public WebhookEndpointCreated(WebhookEndpoint endpoint, string secret)
    {
        Endpoint = endpoint;
        Secret = secret;
    }

    /// <summary>The new endpoint.</summary>
    public WebhookEndpoint Endpoint { get; }

    /// <summary>The plaintext signing secret. No later read returns it — store it now.</summary>
    public string Secret { get; }
}

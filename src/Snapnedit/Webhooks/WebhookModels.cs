using System;

namespace Snapnedit;

/// <summary>Which kind of event an outbound webhook carries.</summary>
public enum WebhookEventType
{
    /// <summary><c>job.succeeded</c>.</summary>
    JobSucceeded = 0,

    /// <summary><c>job.failed</c>.</summary>
    JobFailed = 1,

    /// <summary>An event type this package does not know. Treat it as a forward-compatible no-op, never as an error.</summary>
    Unknown = 2,

    /// <summary><c>session.ended</c> — an embed session's last token expired. Read <see cref="WebhookEvent.Session"/>.</summary>
    SessionEnded = 3,
}

/// <summary>The <c>data</c> object of a <c>session.ended</c> delivery: one per embed session, sent shortly after its last token expires.</summary>
public sealed record WebhookSessionEndedData
{
    /// <summary>Creates session-ended data.</summary>
    /// <param name="sessionId">The embed session id.</param>
    /// <param name="apiKeyId">The publishable key the session was opened with, if any.</param>
    /// <param name="origin">The host origin the editor was embedded on, if known.</param>
    /// <param name="endUserId">The end-user id the host minted the token for, if any.</param>
    /// <param name="startedAt">ISO-8601 timestamp the session opened.</param>
    /// <param name="endedAt">ISO-8601 timestamp the session ended.</param>
    /// <param name="refreshes">How many times the token was re-minted within the session.</param>
    /// <param name="jobs">Jobs created in the session.</param>
    /// <param name="credits">Credits those jobs debited.</param>
    public WebhookSessionEndedData(
        string sessionId,
        string? apiKeyId,
        string? origin,
        string? endUserId,
        string startedAt,
        string endedAt,
        int refreshes,
        int jobs,
        int credits)
    {
        SessionId = sessionId;
        ApiKeyId = apiKeyId;
        Origin = origin;
        EndUserId = endUserId;
        StartedAt = startedAt;
        EndedAt = endedAt;
        Refreshes = refreshes;
        Jobs = jobs;
        Credits = credits;
    }

    /// <summary>The embed session id.</summary>
    public string SessionId { get; }

    /// <summary>The publishable key the session was opened with, if any.</summary>
    public string? ApiKeyId { get; }

    /// <summary>The host origin the editor was embedded on, if known.</summary>
    public string? Origin { get; }

    /// <summary>The end-user id the host minted the token for, if any.</summary>
    public string? EndUserId { get; }

    /// <summary>ISO-8601 timestamp the session opened.</summary>
    public string StartedAt { get; }

    /// <summary>ISO-8601 timestamp the session ended.</summary>
    public string EndedAt { get; }

    /// <summary>How many times the token was re-minted within the session.</summary>
    public int Refreshes { get; }

    /// <summary>Jobs created in the session.</summary>
    public int Jobs { get; }

    /// <summary>Credits those jobs debited.</summary>
    public int Credits { get; }
}

/// <summary>The <c>data</c> object inside a webhook delivery.</summary>
public sealed record WebhookEventData
{
    /// <summary>Creates webhook event data.</summary>
    /// <param name="jobId">The job the event is about.</param>
    /// <param name="operation">The operation id.</param>
    /// <param name="state">Succeeded or failed.</param>
    /// <param name="outputAssetId">The result asset (succeeded only).</param>
    /// <param name="download">A signed url for the result (succeeded only, optional).</param>
    /// <param name="errorCode">Why it failed (failed only).</param>
    /// <param name="message">Human text for the failure (failed only).</param>
    /// <param name="inputKind">Whether the input was an asset or a url.</param>
    /// <param name="destination">The destination summary, or <see langword="null"/>.</param>
    /// <param name="delivery">The delivery record, or <see langword="null"/>.</param>
    /// <param name="test">Whether the job was made with an <c>sk_test_</c> key.</param>
    public WebhookEventData(
        string jobId,
        string operation,
        JobState state,
        string? outputAssetId,
        string? download,
        ErrorCode? errorCode,
        string? message,
        JobInputKind inputKind,
        JobDestinationSummary? destination,
        JobDelivery? delivery,
        bool test = false)
    {
        JobId = jobId;
        Operation = operation;
        State = state;
        OutputAssetId = outputAssetId;
        Download = download;
        ErrorCode = errorCode;
        Message = message;
        InputKind = inputKind;
        Destination = destination;
        Delivery = delivery;
        Test = test;
    }

    /// <summary>The job this event is about.</summary>
    public string JobId { get; }

    /// <summary>The operation id, as an opaque string.</summary>
    public string Operation { get; }

    /// <summary><see cref="JobState.Succeeded"/> or <see cref="JobState.Failed"/>.</summary>
    public JobState State { get; }

    /// <summary>The result asset id, for a succeeded job.</summary>
    public string? OutputAssetId { get; }

    /// <summary>A signed url for the result, for a succeeded job that still has one.</summary>
    public string? Download { get; }

    /// <summary>Why the job failed.</summary>
    public ErrorCode? ErrorCode { get; }

    /// <summary>Human text for the failure. Branch on <see cref="ErrorCode"/>, not on this.</summary>
    public string? Message { get; }

    /// <summary>Whether the job read its input from an asset or a url.</summary>
    public JobInputKind InputKind { get; }

    /// <summary>The destination summary — never the url or headers, which are credentials in motion.</summary>
    public JobDestinationSummary? Destination { get; }

    /// <summary>
    /// How delivery went. A <c>succeeded</c> event with a FAILED delivery is a
    /// real combination, not a contradiction: the job ran fine, only the PUT
    /// into your bucket did not, and the result is still at
    /// <see cref="Download"/>.
    /// </summary>
    public JobDelivery? Delivery { get; }

    /// <summary>Whether the job was made with an <c>sk_test_</c> key (a placeholder result). False when not reported.</summary>
    public bool Test { get; }
}

/// <summary>The full JSON body POSTed to a developer endpoint.</summary>
public sealed record WebhookEvent
{
    /// <summary>Creates a webhook event.</summary>
    /// <param name="id">The delivery id, echoed in <c>X-Snapnedit-Delivery</c>.</param>
    /// <param name="type">The event type, echoed in <c>X-Snapnedit-Event</c>.</param>
    /// <param name="rawType">The event type exactly as sent, including one this package does not know.</param>
    /// <param name="created">Unix seconds the event was created.</param>
    /// <param name="data">The job payload, or <see langword="null"/> for an event that is not about a job.</param>
    /// <param name="session">The <c>session.ended</c> payload, or <see langword="null"/>.</param>
    public WebhookEvent(
        string id,
        WebhookEventType type,
        string rawType,
        long created,
        WebhookEventData? data,
        WebhookSessionEndedData? session = null)
    {
        Id = id;
        Type = type;
        RawType = rawType;
        Created = created;
        Data = data;
        Session = session;
    }

    /// <summary>The delivery id, also sent as <c>X-Snapnedit-Delivery</c>.</summary>
    public string Id { get; }

    /// <summary>The event type. <see cref="WebhookEventType.Unknown"/> for anything newer than this package.</summary>
    public WebhookEventType Type { get; }

    /// <summary>The event type exactly as it arrived on the wire.</summary>
    public string RawType { get; }

    /// <summary>Unix seconds the event was created.</summary>
    public long Created { get; }

    /// <summary>
    /// The job payload, for <see cref="WebhookEventType.JobSucceeded"/> and
    /// <see cref="WebhookEventType.JobFailed"/>. <see langword="null"/> for an
    /// event that is not about a job (<c>session.ended</c>, or a newer type
    /// without a <c>jobId</c>).
    /// </summary>
    public WebhookEventData? Data { get; }

    /// <summary>The payload of a <see cref="WebhookEventType.SessionEnded"/> event; otherwise <see langword="null"/>.</summary>
    public WebhookSessionEndedData? Session { get; }
}

/// <summary>Options for <see cref="SnapneditWebhooks.Verify(string, string, string, WebhookVerifyOptions?)"/>.</summary>
public sealed record WebhookVerifyOptions
{
    /// <summary>
    /// When set, also reject a delivery whose signed timestamp is more than
    /// this many seconds away from <see cref="NowUnixSeconds"/> — replay
    /// protection. Off by default; 300 is the conventional choice.
    /// </summary>
    public int? ToleranceSeconds { get; init; }

    /// <summary>Current time in unix SECONDS, used only for the tolerance check. Defaults to now; inject a fixed value in tests.</summary>
    public long? NowUnixSeconds { get; init; }
}

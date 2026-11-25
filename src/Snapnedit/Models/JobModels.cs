using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>Lifecycle state of a job.</summary>
public enum JobState
{
    /// <summary>Accepted and waiting for a worker.</summary>
    Queued = 0,

    /// <summary>A worker has claimed it.</summary>
    Processing = 1,

    /// <summary>Finished; there is an output asset (and usually a signed download).</summary>
    Succeeded = 2,

    /// <summary>Terminally failed. Credits are refunded.</summary>
    Failed = 3,

    /// <summary>Canceled before completion.</summary>
    Canceled = 4,
}

/// <summary>Where a job's input bytes came from.</summary>
public enum JobInputKind
{
    /// <summary>An asset uploaded through <c>POST /uploads</c>.</summary>
    Asset = 0,

    /// <summary>An external url the server fetched itself (bring your own storage).</summary>
    Url = 1,
}

/// <summary>How a delivery to the caller's own bucket went.</summary>
public enum JobDeliveryStatus
{
    /// <summary>Not attempted yet.</summary>
    Pending = 0,

    /// <summary>The bucket accepted the PUT.</summary>
    Delivered = 1,

    /// <summary>Every attempt failed. The job itself still succeeded — the result is downloadable.</summary>
    Failed = 2,
}

/// <summary>
/// A job's status, as one flattened object. Only the fields relevant to
/// <see cref="State"/> are populated.
/// </summary>
public sealed record JobStatus
{
    /// <summary>Creates a job status.</summary>
    /// <param name="state">Which lifecycle state the job is in.</param>
    /// <param name="startedAt">When a worker claimed it (processing only).</param>
    /// <param name="outputAssetId">The result asset (succeeded only).</param>
    /// <param name="download">A signed url for the result, or <see langword="null"/> when our copy was deleted after delivery (succeeded only).</param>
    /// <param name="errorCode">Why it failed (failed only).</param>
    /// <param name="message">Human text for the failure (failed only).</param>
    public JobStatus(
        JobState state,
        string? startedAt = null,
        string? outputAssetId = null,
        SignedUrl? download = null,
        ErrorCode? errorCode = null,
        string? message = null)
    {
        State = state;
        StartedAt = startedAt;
        OutputAssetId = outputAssetId;
        Download = download;
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>The lifecycle state.</summary>
    public JobState State { get; }

    /// <summary>ISO-8601 timestamp a worker claimed the job. Only for <see cref="JobState.Processing"/>.</summary>
    public string? StartedAt { get; }

    /// <summary>The result asset id. Only for <see cref="JobState.Succeeded"/>.</summary>
    public string? OutputAssetId { get; }

    /// <summary>
    /// A signed url for the result. Only for <see cref="JobState.Succeeded"/> —
    /// and <see langword="null"/> even then when the job was delivered to a
    /// saved destination with <c>deleteAfterDelivery</c>: the only copy is the
    /// one in your bucket.
    /// </summary>
    public SignedUrl? Download { get; }

    /// <summary>Why the job failed. Only for <see cref="JobState.Failed"/>.</summary>
    public ErrorCode? ErrorCode { get; }

    /// <summary>Human text for the failure. Only for <see cref="JobState.Failed"/>. Branch on <see cref="ErrorCode"/>, not on this.</summary>
    public string? Message { get; }

    /// <summary>True once the job can no longer change state.</summary>
    public bool IsTerminal =>
        State == JobState.Succeeded || State == JobState.Failed || State == JobState.Canceled;
}

/// <summary>
/// What the api reports about a job's destination — never the presigned url or
/// its headers, which are bearer credentials for the caller's own bucket.
/// </summary>
public sealed record JobDestinationSummary
{
    /// <summary>Creates a destination summary.</summary>
    /// <param name="type"><c>presigned-put</c> or <c>saved</c>.</param>
    /// <param name="id">The saved destination's id (saved only).</param>
    /// <param name="name">The saved destination's label, absent if it has since been deleted.</param>
    public JobDestinationSummary(string type, string? id = null, string? name = null)
    {
        Type = type;
        Id = id;
        Name = name;
    }

    /// <summary><c>"presigned-put"</c> or <c>"saved"</c>.</summary>
    public string Type { get; }

    /// <summary>The saved destination's id, for <c>saved</c>.</summary>
    public string? Id { get; }

    /// <summary>The saved destination's label, for <c>saved</c>. Absent when the destination has since been deleted.</summary>
    public string? Name { get; }
}

/// <summary>The outcome of PUTting a job's result into the caller's bucket.</summary>
/// <remarks>
/// A failed delivery does NOT fail the job: <c>succeeded</c> with
/// <see cref="JobDeliveryStatus.Failed"/> is a real, correct combination and
/// the result is still downloadable.
/// </remarks>
public sealed record JobDelivery
{
    /// <summary>Creates a delivery record.</summary>
    /// <param name="status">Pending, delivered or failed.</param>
    /// <param name="attempts">How many PUTs were made.</param>
    /// <param name="deliveredAt">When the bucket confirmed the write.</param>
    /// <param name="statusCode">The bucket's HTTP status on the last attempt.</param>
    /// <param name="error">Why the last attempt failed.</param>
    /// <param name="bucket">The bucket written to (saved destinations only).</param>
    /// <param name="key">The object key written (saved destinations only).</param>
    /// <param name="localCopyDeleted">Whether our own copy of the result was dropped after delivery.</param>
    public JobDelivery(
        JobDeliveryStatus status,
        int attempts,
        string? deliveredAt = null,
        int? statusCode = null,
        string? error = null,
        string? bucket = null,
        string? key = null,
        bool? localCopyDeleted = null)
    {
        Status = status;
        Attempts = attempts;
        DeliveredAt = deliveredAt;
        StatusCode = statusCode;
        Error = error;
        Bucket = bucket;
        Key = key;
        LocalCopyDeleted = localCopyDeleted;
    }

    /// <summary>Pending, delivered or failed.</summary>
    public JobDeliveryStatus Status { get; }

    /// <summary>How many PUT attempts were made.</summary>
    public int Attempts { get; }

    /// <summary>ISO-8601 timestamp the bucket confirmed the write.</summary>
    public string? DeliveredAt { get; }

    /// <summary>The bucket's HTTP status on the last attempt.</summary>
    public int? StatusCode { get; }

    /// <summary>Why the last attempt failed.</summary>
    public string? Error { get; }

    /// <summary>Which bucket the bytes landed in. Saved destinations only.</summary>
    public string? Bucket { get; }

    /// <summary>The object key the server generated. Saved destinations only.</summary>
    public string? Key { get; }

    /// <summary>Whether our copy of the result was deleted once the bucket confirmed (<c>deleteAfterDelivery</c>).</summary>
    public bool? LocalCopyDeleted { get; }
}

/// <summary>The envelope every job response carries: where the bytes came from, where they go, and what the job cost.</summary>
public record JobEnvelope
{
    /// <summary>Creates an envelope.</summary>
    /// <param name="inputKind">Whether the input was an asset or a url.</param>
    /// <param name="destination">The destination summary, or <see langword="null"/>.</param>
    /// <param name="delivery">The delivery record, or <see langword="null"/>.</param>
    /// <param name="creditCost">Credits actually debited for this job.</param>
    /// <param name="cached">Whether the result came out of the result cache.</param>
    /// <param name="deliveryOnly">Whether this row only delivers an already-cached result.</param>
    /// <param name="test">Whether the job was made with an <c>sk_test_</c> key.</param>
    public JobEnvelope(
        JobInputKind inputKind,
        JobDestinationSummary? destination,
        JobDelivery? delivery,
        int creditCost = 0,
        bool cached = false,
        bool deliveryOnly = false,
        bool test = false)
    {
        InputKind = inputKind;
        Destination = destination;
        Delivery = delivery;
        CreditCost = creditCost;
        Cached = cached;
        DeliveryOnly = deliveryOnly;
        Test = test;
    }

    /// <summary>Whether the job read its input from an uploaded asset or from a url.</summary>
    public JobInputKind InputKind { get; }

    /// <summary>Which destination the result is delivered to, if any. Never carries the url or headers.</summary>
    public JobDestinationSummary? Destination { get; }

    /// <summary>How delivery went, if there is a destination.</summary>
    public JobDelivery? Delivery { get; }

    /// <summary>
    /// Credits actually debited for THIS job: 0 for a free operation, a cache
    /// hit, a delivery-only clone or an unmetered website session. Debited at
    /// creation time and refunded in full if the job ends
    /// <see cref="JobState.Failed"/>.
    /// </summary>
    public int CreditCost { get; }

    /// <summary>
    /// Whether the result came out of the result cache: no model ran and
    /// nothing was billed. A cache hit is still a job — one row per REQUEST —
    /// so it carries a NEW job id pointing at the SAME output asset as the job
    /// whose result it reuses.
    /// </summary>
    public bool Cached { get; }

    /// <summary>
    /// Whether this row exists only to deliver an already-cached result to a
    /// destination: cached and free, but still worth polling, because writing
    /// to your bucket is work the worker has yet to do.
    /// </summary>
    public bool DeliveryOnly { get; }

    /// <summary>
    /// Whether the job was created with an <c>sk_test_</c> key: free, never
    /// cached, and its result is a placeholder image rather than a model run.
    /// False when the api does not report it.
    /// </summary>
    public bool Test { get; }
}

/// <summary>What <c>GET /jobs/{id}</c> returns: the status, flattened, plus the envelope.</summary>
public sealed record JobView : JobEnvelope
{
    /// <summary>Creates a job view.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="status">The job's status.</param>
    /// <param name="inputKind">Whether the input was an asset or a url.</param>
    /// <param name="destination">The destination summary, or <see langword="null"/>.</param>
    /// <param name="delivery">The delivery record, or <see langword="null"/>.</param>
    /// <param name="creditCost">Credits actually debited for this job.</param>
    /// <param name="cached">Whether the result came out of the result cache.</param>
    /// <param name="deliveryOnly">Whether this row only delivers an already-cached result.</param>
    /// <param name="test">Whether the job was made with an <c>sk_test_</c> key.</param>
    public JobView(
        string jobId,
        JobStatus status,
        JobInputKind inputKind,
        JobDestinationSummary? destination,
        JobDelivery? delivery,
        int creditCost = 0,
        bool cached = false,
        bool deliveryOnly = false,
        bool test = false)
        : base(inputKind, destination, delivery, creditCost, cached, deliveryOnly, test)
    {
        JobId = jobId;
        Status = status;
    }

    /// <summary>The job id this view describes.</summary>
    public string JobId { get; }

    /// <summary>The job's status.</summary>
    public JobStatus Status { get; }

    /// <summary>Shorthand for <c>Status.State</c>.</summary>
    public JobState State => Status.State;

    /// <summary>
    /// True when nothing is left to wait for: the job is terminal AND, if it
    /// has a destination, the delivery has settled. A cache hit with a
    /// destination comes back <c>succeeded</c> with a still-pending delivery,
    /// which is exactly the case this guards.
    /// </summary>
    public bool IsSettled =>
        Status.IsTerminal &&
        !(Status.State == JobState.Succeeded && Delivery is not null && Delivery.Status == JobDeliveryStatus.Pending);
}

/// <summary>What <c>POST /jobs</c> returns. <see cref="Status"/> may already be terminal on a cache hit.</summary>
public sealed record CreateJobResult : JobEnvelope
{
    /// <summary>Creates a job-create result.</summary>
    /// <param name="jobId">The new (or cached) job id.</param>
    /// <param name="status">The job's status at creation time.</param>
    /// <param name="httpStatus">The HTTP status: <c>202</c> for a fresh job, <c>200</c> for a cache hit.</param>
    /// <param name="inputKind">Whether the input was an asset or a url.</param>
    /// <param name="destination">The destination summary, or <see langword="null"/>.</param>
    /// <param name="delivery">The delivery record, or <see langword="null"/>.</param>
    /// <param name="creditCost">Credits actually debited for this job.</param>
    /// <param name="cached">Whether the result came out of the result cache.</param>
    /// <param name="deliveryOnly">Whether this row only delivers an already-cached result.</param>
    /// <param name="test">Whether the job was made with an <c>sk_test_</c> key.</param>
    public CreateJobResult(
        string jobId,
        JobStatus status,
        int httpStatus,
        JobInputKind inputKind,
        JobDestinationSummary? destination,
        JobDelivery? delivery,
        int creditCost = 0,
        bool cached = false,
        bool deliveryOnly = false,
        bool test = false)
        : base(inputKind, destination, delivery, creditCost, cached, deliveryOnly, test)
    {
        JobId = jobId;
        Status = status;
        HttpStatus = httpStatus;
    }

    /// <summary>The job id.</summary>
    public string JobId { get; }

    /// <summary>The status the api reported at creation time.</summary>
    public JobStatus Status { get; }

    /// <summary>The HTTP status: <c>202</c> for a newly queued job, <c>200</c> when the result cache already had it.</summary>
    public int HttpStatus { get; }

    /// <summary>
    /// True when this was answered from the result cache — already succeeded,
    /// and never billed. Reads the api's own <see cref="JobEnvelope.Cached"/>
    /// flag, falling back to the <c>200</c> (rather than <c>202</c>) such a
    /// response is sent with.
    /// </summary>
    public bool WasCacheHit => Cached || (HttpStatus == 200 && Status.State == JobState.Succeeded);

    /// <summary>A <see cref="JobView"/> over the same data, for code that wants one shape.</summary>
    /// <returns>The equivalent job view.</returns>
    public JobView ToJobView() =>
        new JobView(JobId, Status, InputKind, Destination, Delivery, CreditCost, Cached, DeliveryOnly, Test);
}

/// <summary>What <see cref="SnapneditClient.RunAsync"/> resolves to.</summary>
public sealed record RunResult : JobEnvelope
{
    /// <summary>Creates a run result.</summary>
    /// <param name="jobId">The job that produced it.</param>
    /// <param name="downloaded">Whether the bytes were fetched into <paramref name="output"/>.</param>
    /// <param name="output">The result bytes, when downloaded.</param>
    /// <param name="mime">The result's content type, when downloaded.</param>
    /// <param name="outputAssetId">The result asset id.</param>
    /// <param name="download">The signed url for the result, or <see langword="null"/> when our copy was deleted after delivery.</param>
    /// <param name="inputKind">Whether the input was an asset or a url.</param>
    /// <param name="destination">The destination summary, or <see langword="null"/>.</param>
    /// <param name="delivery">The delivery record, or <see langword="null"/>.</param>
    /// <param name="creditCost">Credits actually debited for the job.</param>
    /// <param name="cached">Whether the result came out of the result cache.</param>
    /// <param name="deliveryOnly">Whether the job only delivered an already-cached result.</param>
    /// <param name="test">Whether the job was made with an <c>sk_test_</c> key.</param>
    public RunResult(
        string jobId,
        bool downloaded,
        byte[]? output,
        string? mime,
        string? outputAssetId,
        SignedUrl? download,
        JobInputKind inputKind,
        JobDestinationSummary? destination,
        JobDelivery? delivery,
        int creditCost = 0,
        bool cached = false,
        bool deliveryOnly = false,
        bool test = false)
        : base(inputKind, destination, delivery, creditCost, cached, deliveryOnly, test)
    {
        JobId = jobId;
        Downloaded = downloaded;
        Output = output;
        Mime = mime;
        OutputAssetId = outputAssetId;
        Download = download;
    }

    /// <summary>The job that produced this result.</summary>
    public string JobId { get; }

    /// <summary>
    /// Whether <see cref="Output"/> holds the bytes. False when the result went
    /// straight to your bucket (see <see cref="RunOptions.Download"/>), or when
    /// a <c>deleteAfterDelivery</c> destination left no copy to fetch.
    /// </summary>
    public bool Downloaded { get; }

    /// <summary>The result bytes, or <see langword="null"/> when <see cref="Downloaded"/> is false.</summary>
    public byte[]? Output { get; }

    /// <summary>The result's content type, or <see langword="null"/> when <see cref="Downloaded"/> is false.</summary>
    public string? Mime { get; }

    /// <summary>The result asset id.</summary>
    public string? OutputAssetId { get; }

    /// <summary>
    /// A signed url for the result, whether or not it was downloaded —
    /// <see langword="null"/> when there is no copy on our side to sign one for
    /// (a saved destination with <c>deleteAfterDelivery</c>; the bytes are at
    /// <c>Delivery.Bucket</c>/<c>Delivery.Key</c>).
    /// </summary>
    public SignedUrl? Download { get; }
}

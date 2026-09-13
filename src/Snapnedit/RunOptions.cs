using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>Options shared by <see cref="SnapneditClient.CreateJobAsync"/> and <see cref="SnapneditClient.RunAsync"/>.</summary>
public record CreateJobOptions
{
    /// <summary>
    /// Free-form operation params. Merged over
    /// <see cref="TypedParams"/> when both are given, so this always wins.
    /// The api validates them strictly: an unknown key is a 400.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Params { get; init; }

    /// <summary>A strongly-typed params record, e.g. <see cref="ResizeImageParams"/>. Prefer it over <see cref="Params"/>.</summary>
    public IOperationParams? TypedParams { get; init; }

    /// <summary>
    /// Where the result is delivered. Leave <see langword="null"/> to apply your
    /// account default (if any); pass <see cref="JobDestination.OptOut"/> to
    /// explicitly send <c>destination: null</c> and decline it.
    /// </summary>
    public JobDestination? Destination { get; init; }
}

/// <summary>How long to keep polling a job, and how often.</summary>
public record WaitForJobOptions
{
    /// <summary>Delay between <c>GET /jobs/{id}</c> polls. Default 1 second.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Total budget before <see cref="SnapneditTimeoutException"/> is thrown. Default 2 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>Options for <see cref="SnapneditClient.RunAsync"/> — the whole upload, run, poll, download flow.</summary>
public sealed record RunOptions : CreateJobOptions
{
    /// <summary>
    /// A second image for mask-guided operations
    /// (<see cref="Operations.MagicEraser"/>, <see cref="Operations.GenerativeFill"/>,
    /// <see cref="Operations.RemoveWatermark"/>). The client uploads it and sets
    /// <c>params.maskAssetId</c> — do not set that yourself. Masks are
    /// asset-only; there is no url form.
    /// </summary>
    public JobInput? Mask { get; init; }

    /// <summary>Delay between <c>GET /jobs/{id}</c> polls. Default 1 second.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Total polling budget before <see cref="SnapneditTimeoutException"/> is thrown. Default 2 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether to download the result bytes.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/> for an ordinary run, and to
    /// <see langword="false"/> when you named a <see cref="CreateJobOptions.Destination"/>
    /// AND the server reports the delivery succeeded — the whole point of a
    /// destination is that the bytes go straight to your bucket. A delivery that
    /// FAILED still downloads, so you are never left empty-handed. An account
    /// default applied server-side does not flip it: a caller who wrote
    /// <c>RunAsync(...)</c> and nothing else still expects bytes back.
    /// Set it explicitly to override either way. A saved destination with
    /// <c>deleteAfterDelivery</c> leaves no copy to fetch, so the result is
    /// <c>Downloaded == false</c> even with <see langword="true"/> here, rather
    /// than an error.
    /// </remarks>
    public bool? Download { get; init; }

    internal WaitForJobOptions ToWaitOptions() => new WaitForJobOptions { PollInterval = PollInterval, Timeout = Timeout };
}

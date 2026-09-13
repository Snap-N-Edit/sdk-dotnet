using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>A short-lived signed url minted by the api, plus its expiry.</summary>
/// <remarks>
/// <c>Url</c> may be a same-origin PATH (local/dev storage signs
/// <c>/_local/…?exp&amp;sig</c>) rather than an absolute url. This client
/// resolves it against the configured base url before following it.
/// </remarks>
public sealed record SignedUrl
{
    /// <summary>Creates a signed url.</summary>
    /// <param name="url">The url or same-origin path to follow.</param>
    /// <param name="expiresAt">ISO-8601 timestamp after which the signature is no longer valid.</param>
    public SignedUrl(string url, string expiresAt)
    {
        Url = url;
        ExpiresAt = expiresAt;
    }

    /// <summary>The url, or a same-origin path to resolve against the api base url.</summary>
    public string Url { get; }

    /// <summary>ISO-8601 timestamp after which the signature stops working.</summary>
    public string ExpiresAt { get; }
}

/// <summary>One entry of <c>GET /operations</c> — everything a caller needs to drive an operation without hard-coding it.</summary>
public sealed record OperationMetadata
{
    /// <summary>Creates operation metadata.</summary>
    /// <param name="id">The operation id.</param>
    /// <param name="label">Human label.</param>
    /// <param name="accept">Mime types the operation accepts.</param>
    /// <param name="maxInputDimension">Longest input edge, in pixels, before the api downscales.</param>
    /// <param name="requiresMask">Whether <c>params.maskAssetId</c> is mandatory.</param>
    /// <param name="description">Human description.</param>
    /// <param name="seoTitle">Marketing title used by the website.</param>
    /// <param name="paramsJsonSchema">The operation's params, as JSON Schema.</param>
    public OperationMetadata(
        string id,
        string label,
        IReadOnlyList<string> accept,
        int maxInputDimension,
        bool requiresMask,
        string description,
        string seoTitle,
        System.Text.Json.JsonElement paramsJsonSchema)
    {
        Id = id;
        Label = label;
        Accept = accept;
        MaxInputDimension = maxInputDimension;
        RequiresMask = requiresMask;
        Description = description;
        SeoTitle = seoTitle;
        ParamsJsonSchema = paramsJsonSchema;
    }

    /// <summary>The operation id, one of <see cref="Operations.All"/>.</summary>
    public string Id { get; }

    /// <summary>Human-readable label.</summary>
    public string Label { get; }

    /// <summary>Mime types this operation accepts as input.</summary>
    public IReadOnlyList<string> Accept { get; }

    /// <summary>Longest input edge in pixels; larger inputs are downscaled before inference.</summary>
    public int MaxInputDimension { get; }

    /// <summary>Whether the api refuses the job without <c>params.maskAssetId</c>.</summary>
    public bool RequiresMask { get; }

    /// <summary>Human-readable description.</summary>
    public string Description { get; }

    /// <summary>The website's marketing title for this operation.</summary>
    public string SeoTitle { get; }

    /// <summary>The operation's params as a JSON Schema document — the same one the api validates against.</summary>
    public System.Text.Json.JsonElement ParamsJsonSchema { get; }
}

/// <summary>What <see cref="SnapneditClient.UploadAsync(byte[], string?, System.Threading.CancellationToken)"/> produced.</summary>
public sealed record UploadResult
{
    /// <summary>Creates an upload result.</summary>
    /// <param name="assetId">The asset id to name as a job input.</param>
    /// <param name="contentHash">The sha-256 of the confirmed bytes, lower-case hex.</param>
    /// <param name="bytes">The confirmed byte length.</param>
    public UploadResult(string assetId, string contentHash, long bytes)
    {
        AssetId = assetId;
        ContentHash = contentHash;
        Bytes = bytes;
    }

    /// <summary>The asset id — pass it to <see cref="JobInput.FromAssetId"/>.</summary>
    public string AssetId { get; }

    /// <summary>Sha-256 of the stored bytes, lower-case hex. Set by <c>POST /uploads/{id}/confirm</c>; content-based caching keys on it.</summary>
    public string ContentHash { get; }

    /// <summary>Byte length the api recorded.</summary>
    public long Bytes { get; }
}

using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>Which S3-compatible service a saved destination points at.</summary>
public enum StorageProvider
{
    /// <summary>Amazon S3. Wire value <c>"aws-s3"</c>.</summary>
    AwsS3 = 0,

    /// <summary>Cloudflare R2. Wire value <c>"cloudflare-r2"</c>.</summary>
    CloudflareR2 = 1,

    /// <summary>Backblaze B2. Wire value <c>"backblaze-b2"</c>.</summary>
    BackblazeB2 = 2,

    /// <summary>Any other S3-compatible endpoint. Wire value <c>"s3-compatible"</c>.</summary>
    S3Compatible = 3,
}

/// <summary>Wire spellings for <see cref="StorageProvider"/>.</summary>
public static class StorageProviders
{
    /// <summary>The wire spelling of <paramref name="provider"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>Its wire string.</returns>
    public static string ToWire(StorageProvider provider) => provider switch
    {
        StorageProvider.AwsS3 => "aws-s3",
        StorageProvider.CloudflareR2 => "cloudflare-r2",
        StorageProvider.BackblazeB2 => "backblaze-b2",
        StorageProvider.S3Compatible => "s3-compatible",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Parses a wire provider string.</summary>
    /// <param name="value">The wire string.</param>
    /// <returns>The matching provider.</returns>
    /// <exception cref="SnapneditException">The value is not a provider this package knows.</exception>
    public static StorageProvider Parse(string? value) => value switch
    {
        "aws-s3" => StorageProvider.AwsS3,
        "cloudflare-r2" => StorageProvider.CloudflareR2,
        "backblaze-b2" => StorageProvider.BackblazeB2,
        "s3-compatible" => StorageProvider.S3Compatible,
        _ => throw new SnapneditException(
            ErrorCode.Internal,
            "the api returned an unknown storage provider: " + (value ?? "null"),
            0),
    };
}

/// <summary>The result of the last <c>POST /destinations/{id}/test</c> the api recorded.</summary>
public sealed record StorageDestinationTest
{
    /// <summary>Creates a test record.</summary>
    /// <param name="ok">Whether the probe write succeeded.</param>
    /// <param name="at">ISO-8601 timestamp of the probe.</param>
    /// <param name="error">Why it failed, when it did.</param>
    public StorageDestinationTest(bool ok, string at, string? error = null)
    {
        Ok = ok;
        At = at;
        Error = error;
    }

    /// <summary>Whether the probe write succeeded.</summary>
    public bool Ok { get; }

    /// <summary>ISO-8601 timestamp of the probe.</summary>
    public string At { get; }

    /// <summary>Why the probe failed, when it did.</summary>
    public string? Error { get; }
}

/// <summary>A saved storage destination, as the api reports it. Credentials are never echoed.</summary>
public sealed record StorageDestination
{
    /// <summary>Creates a destination view.</summary>
    /// <param name="id">The destination id.</param>
    /// <param name="name">Its label.</param>
    /// <param name="provider">Which S3-compatible service it points at.</param>
    /// <param name="bucket">The bucket name.</param>
    /// <param name="region">The region, if any.</param>
    /// <param name="endpoint">The endpoint url, if any.</param>
    /// <param name="forcePathStyle">Whether the signer uses path-style addressing.</param>
    /// <param name="keyPrefix">Prefix prepended to every generated object key.</param>
    /// <param name="accessKeyIdLast4">The last 4 characters of the access key id — the only part ever returned.</param>
    /// <param name="isDefault">Whether jobs with no <c>destination</c> are delivered here.</param>
    /// <param name="deleteAfterDelivery">Whether our copy of the result is dropped once the bucket confirms.</param>
    /// <param name="lastTest">The last recorded probe, if any.</param>
    /// <param name="createdAt">ISO-8601 creation timestamp.</param>
    /// <param name="updatedAt">ISO-8601 last-update timestamp.</param>
    public StorageDestination(
        string id,
        string name,
        StorageProvider provider,
        string bucket,
        string? region,
        string? endpoint,
        bool forcePathStyle,
        string keyPrefix,
        string accessKeyIdLast4,
        bool isDefault,
        bool deleteAfterDelivery,
        StorageDestinationTest? lastTest,
        string createdAt,
        string updatedAt)
    {
        Id = id;
        Name = name;
        Provider = provider;
        Bucket = bucket;
        Region = region;
        Endpoint = endpoint;
        ForcePathStyle = forcePathStyle;
        KeyPrefix = keyPrefix;
        AccessKeyIdLast4 = accessKeyIdLast4;
        IsDefault = isDefault;
        DeleteAfterDelivery = deleteAfterDelivery;
        LastTest = lastTest;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>The destination id — name it on a job as <see cref="JobDestination.Saved"/>.</summary>
    public string Id { get; }

    /// <summary>Human label.</summary>
    public string Name { get; }

    /// <summary>Which S3-compatible service this points at.</summary>
    public StorageProvider Provider { get; }

    /// <summary>The bucket name.</summary>
    public string Bucket { get; }

    /// <summary>The region, if the provider needs one.</summary>
    public string? Region { get; }

    /// <summary>The endpoint url, if the provider needs one.</summary>
    public string? Endpoint { get; }

    /// <summary>Whether the signer uses path-style addressing.</summary>
    public bool ForcePathStyle { get; }

    /// <summary>Prefix prepended to every generated object key.</summary>
    public string KeyPrefix { get; }

    /// <summary>The last 4 characters of the access key id — the only part any endpoint returns.</summary>
    public string AccessKeyIdLast4 { get; }

    /// <summary>Whether API-key and embed jobs that name no destination are delivered here.</summary>
    public bool IsDefault { get; }

    /// <summary>Whether our copy of the result is dropped once the bucket confirms the write.</summary>
    public bool DeleteAfterDelivery { get; }

    /// <summary>The last recorded credential probe, if the destination has ever been tested.</summary>
    public StorageDestinationTest? LastTest { get; }

    /// <summary>ISO-8601 creation timestamp.</summary>
    public string CreatedAt { get; }

    /// <summary>ISO-8601 last-update timestamp.</summary>
    public string UpdatedAt { get; }
}

/// <summary>The body of <c>POST /destinations</c>. Max 10 destinations per account.</summary>
public sealed record StorageDestinationInput
{
    /// <summary>Creates a destination input.</summary>
    /// <param name="name">A label, 1..80 characters.</param>
    /// <param name="provider">Which S3-compatible service to write to.</param>
    /// <param name="bucket">The bucket name, 3..63 characters.</param>
    /// <param name="accessKeyId">The access key id. Only its last 4 characters are ever returned.</param>
    /// <param name="secretAccessKey">The secret access key. Sealed at rest and never returned by any endpoint.</param>
    public StorageDestinationInput(string name, StorageProvider provider, string bucket, string accessKeyId, string secretAccessKey)
    {
        Name = name;
        Provider = provider;
        Bucket = bucket;
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
    }

    /// <summary>A label, 1..80 characters.</summary>
    public string Name { get; }

    /// <summary>Which S3-compatible service to write to.</summary>
    public StorageProvider Provider { get; }

    /// <summary>The bucket name, 3..63 characters.</summary>
    public string Bucket { get; }

    /// <summary>The access key id.</summary>
    public string AccessKeyId { get; }

    /// <summary>The secret access key. Encrypted at rest; no endpoint ever echoes it.</summary>
    public string SecretAccessKey { get; }

    /// <summary>The region, lower-case alphanumerics and dashes. Required by some providers.</summary>
    public string? Region { get; init; }

    /// <summary>The endpoint url. Required for <see cref="StorageProvider.S3Compatible"/>.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The Cloudflare account id, for <see cref="StorageProvider.CloudflareR2"/>.</summary>
    public string? AccountId { get; init; }

    /// <summary>Whether to sign path-style requests.</summary>
    public bool? ForcePathStyle { get; init; }

    /// <summary>Prefix prepended to every generated object key, e.g. <c>"results/"</c>.</summary>
    public string? KeyPrefix { get; init; }

    /// <summary>Make this the account default: every API-key or embed job that names no destination is delivered here.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Drop our copy of the result once the bucket confirms the write. <c>GET /jobs/{id}</c> then reports <c>download: null</c>.</summary>
    public bool? DeleteAfterDelivery { get; init; }
}

/// <summary>
/// The body of <c>PATCH /destinations/{id}</c>. Every field is optional, but
/// <see cref="AccessKeyId"/> and <see cref="SecretAccessKey"/> must move
/// together, and <c>provider</c> is not patchable — change it with a delete
/// plus a create.
/// </summary>
public sealed record StorageDestinationPatch
{
    /// <summary>A new label.</summary>
    public string? Name { get; init; }

    /// <summary>A new bucket.</summary>
    public string? Bucket { get; init; }

    /// <summary>A new region. Pass <see cref="NullSentinel"/> semantics via <see cref="ClearRegion"/> to unset it.</summary>
    public string? Region { get; init; }

    /// <summary>Send <c>region: null</c> to unset the region.</summary>
    public bool ClearRegion { get; init; }

    /// <summary>A new endpoint.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Send <c>endpoint: null</c> to unset the endpoint.</summary>
    public bool ClearEndpoint { get; init; }

    /// <summary>Whether to sign path-style requests.</summary>
    public bool? ForcePathStyle { get; init; }

    /// <summary>A new key prefix.</summary>
    public string? KeyPrefix { get; init; }

    /// <summary>A new access key id. Must be sent together with <see cref="SecretAccessKey"/>.</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>A new secret access key. Must be sent together with <see cref="AccessKeyId"/>.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>Make (or stop making) this the account default.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Turn <c>deleteAfterDelivery</c> on or off.</summary>
    public bool? DeleteAfterDelivery { get; init; }

    /// <summary>Documentation anchor for the <c>Clear*</c> flags: the api distinguishes an omitted field from an explicit <c>null</c>.</summary>
    public const string NullSentinel = "null";
}

/// <summary>
/// The outcome of <c>POST /destinations/{id}/test</c> — a real round trip
/// (a probe object written under the destination's own prefix, then deleted),
/// not a credential format check. The call always succeeds; <see cref="Ok"/>
/// says whether the bucket did.
/// </summary>
public sealed record DestinationTestResult
{
    /// <summary>Creates a test result.</summary>
    /// <param name="ok">Whether the bucket accepted the probe.</param>
    /// <param name="latencyMs">How long the round trip took.</param>
    /// <param name="error">Why it failed, when it did.</param>
    public DestinationTestResult(bool ok, double latencyMs, string? error = null)
    {
        Ok = ok;
        LatencyMs = latencyMs;
        Error = error;
    }

    /// <summary>Whether the bucket accepted the probe write.</summary>
    public bool Ok { get; }

    /// <summary>Round-trip time in milliseconds.</summary>
    public double LatencyMs { get; }

    /// <summary>Why the probe failed, when it did.</summary>
    public string? Error { get; }
}

/// <summary>File extensions <c>POST /destinations/{id}/presign</c> will sign an upload for.</summary>
public enum DestinationExportExt
{
    /// <summary><c>png</c>, canonical content type <c>image/png</c>.</summary>
    Png = 0,

    /// <summary><c>jpg</c>, canonical content type <c>image/jpeg</c>.</summary>
    Jpg = 1,

    /// <summary><c>webp</c>, canonical content type <c>image/webp</c>.</summary>
    Webp = 2,

    /// <summary><c>avif</c>, canonical content type <c>image/avif</c>.</summary>
    Avif = 3,

    /// <summary><c>svg</c>, canonical content type <c>image/svg+xml</c>.</summary>
    Svg = 4,

    /// <summary><c>pdf</c>, canonical content type <c>application/pdf</c>.</summary>
    Pdf = 5,

    /// <summary><c>gif</c>, canonical content type <c>image/gif</c>.</summary>
    Gif = 6,
}

/// <summary>Wire spellings and canonical content types for <see cref="DestinationExportExt"/>.</summary>
public static class DestinationExportExtensions
{
    /// <summary>The wire spelling of <paramref name="ext"/>.</summary>
    /// <param name="ext">The extension.</param>
    /// <returns>Its wire string, e.g. <c>"png"</c>.</returns>
    public static string ToWire(DestinationExportExt ext) => ext switch
    {
        DestinationExportExt.Png => "png",
        DestinationExportExt.Jpg => "jpg",
        DestinationExportExt.Webp => "webp",
        DestinationExportExt.Avif => "avif",
        DestinationExportExt.Svg => "svg",
        DestinationExportExt.Pdf => "pdf",
        DestinationExportExt.Gif => "gif",
        _ => throw new ArgumentOutOfRangeException(nameof(ext)),
    };

    /// <summary>
    /// The one content type the api accepts for <paramref name="ext"/>. A
    /// mismatched pair is a <see cref="ErrorCode.InvalidInput"/> 400.
    /// </summary>
    /// <param name="ext">The extension.</param>
    /// <returns>Its canonical content type.</returns>
    public static string CanonicalContentType(DestinationExportExt ext) => ext switch
    {
        DestinationExportExt.Png => "image/png",
        DestinationExportExt.Jpg => "image/jpeg",
        DestinationExportExt.Webp => "image/webp",
        DestinationExportExt.Avif => "image/avif",
        DestinationExportExt.Svg => "image/svg+xml",
        DestinationExportExt.Pdf => "application/pdf",
        DestinationExportExt.Gif => "image/gif",
        _ => throw new ArgumentOutOfRangeException(nameof(ext)),
    };
}

/// <summary>A short-lived, server-signed PUT for one object in your own bucket.</summary>
public sealed record DestinationPresignResponse
{
    /// <summary>Creates a presign response.</summary>
    /// <param name="url">The absolute url to PUT to.</param>
    /// <param name="method">Always <c>PUT</c>.</param>
    /// <param name="headers">Headers the signature covers — send them verbatim.</param>
    /// <param name="key">The object key the server generated.</param>
    /// <param name="bucket">The bucket the key lives in.</param>
    /// <param name="expiresAt">ISO-8601 expiry, 15 minutes out.</param>
    public DestinationPresignResponse(
        string url,
        string method,
        IReadOnlyDictionary<string, string> headers,
        string key,
        string bucket,
        string expiresAt)
    {
        Url = url;
        Method = method;
        Headers = headers;
        Key = key;
        Bucket = bucket;
        ExpiresAt = expiresAt;
    }

    /// <summary>The absolute url to PUT to.</summary>
    public string Url { get; }

    /// <summary>Always <c>"PUT"</c>.</summary>
    public string Method { get; }

    /// <summary>Headers the signature covers. Send them verbatim or the bucket rejects the PUT.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The object key, of the form <c>&lt;keyPrefix&gt;YYYY/MM/DD/export-&lt;timestamp&gt;-&lt;6 chars&gt;.&lt;ext&gt;</c>.</summary>
    public string Key { get; }

    /// <summary>The bucket the key lives in.</summary>
    public string Bucket { get; }

    /// <summary>ISO-8601 expiry — the signature is good for 15 minutes.</summary>
    public string ExpiresAt { get; }
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Snapnedit.Internal;

namespace Snapnedit;

/// <summary>
/// Your account's saved storage destinations: S3-compatible buckets saved once
/// and then named on a job by id, or marked as the account default.
/// </summary>
/// <remarks>
/// Reached through <see cref="SnapneditClient.Destinations"/>. The secret
/// access key is sealed at rest and is never returned by any endpoint — only
/// the last 4 characters of the access key id are. A destination id belonging
/// to another account is <see cref="ErrorCode.NotFound"/>, never
/// <see cref="ErrorCode.Forbidden"/>.
/// </remarks>
public sealed class DestinationsClient
{
    private readonly ApiTransport _transport;

    internal DestinationsClient(ApiTransport transport) => _transport = transport;

    /// <summary><c>GET /destinations</c> — every saved destination on the account, credential-free.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The account's destinations.</returns>
    /// <exception cref="SnapneditException">The credential is missing or not an account key (401).</exception>
    public async Task<IReadOnlyList<StorageDestination>> ListAsync(CancellationToken cancellationToken = default)
    {
        var url = _transport.ApiUrl("/destinations");
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        var list = Json.Property(response.Root, "destinations");
        if (list is null || list.Value.ValueKind != JsonValueKind.Array)
        {
            throw Json.Malformed(url, "missing array field \"destinations\"");
        }

        var destinations = new List<StorageDestination>();
        foreach (var entry in list.Value.EnumerateArray())
        {
            destinations.Add(ApiParse.Destination(entry, url));
        }

        return destinations;
    }

    /// <summary><c>POST /destinations</c> — saves a bucket and its credentials. Max 10 per account.</summary>
    /// <param name="input">The bucket, its credentials and its options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The saved destination, credential-free.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="SnapneditException">Validation failed (400), or the account is already at its limit.</exception>
    public async Task<StorageDestination> CreateAsync(
        StorageDestinationInput input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var url = _transport.ApiUrl("/destinations");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(writer => WriteCreate(writer, input)),
            true,
            cancellationToken).ConfigureAwait(false);
        return Envelope(response.Root, url);
    }

    /// <summary>
    /// <c>PATCH /destinations/{id}</c>. Every field is optional, but
    /// <see cref="StorageDestinationPatch.AccessKeyId"/> and
    /// <see cref="StorageDestinationPatch.SecretAccessKey"/> must move together;
    /// <c>provider</c> is not patchable.
    /// </summary>
    /// <param name="id">The destination id.</param>
    /// <param name="patch">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The updated destination.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="patch"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.NotFound"/> for an id that is not on this account.</exception>
    public async Task<StorageDestination> UpdateAsync(
        string id,
        StorageDestinationPatch patch,
        CancellationToken cancellationToken = default)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        if (patch is null)
        {
            throw new ArgumentNullException(nameof(patch));
        }

        var url = DestinationUrl(id);
        using var response = await _transport.SendJsonAsync(
            new HttpMethod("PATCH"),
            url,
            () => ApiTransport.JsonContent(writer => WritePatch(writer, patch)),
            true,
            cancellationToken).ConfigureAwait(false);
        return Envelope(response.Root, url);
    }

    /// <summary>
    /// <c>DELETE /destinations/{id}</c> — answers <c>204</c>. Deleting your
    /// default simply leaves the account without one; jobs already queued
    /// against it stop being delivered.
    /// </summary>
    /// <param name="id">The destination id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the destination is gone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.NotFound"/> for an id that is not on this account.</exception>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        return _transport.SendVoidAsync(HttpMethod.Delete, DestinationUrl(id), null, cancellationToken);
    }

    /// <summary>
    /// <c>POST /destinations/{id}/test</c> — proves the credentials can WRITE,
    /// by writing (and deleting) a probe object under the destination's own
    /// prefix. Always answers <c>200</c>;
    /// <see cref="DestinationTestResult.Ok"/> says whether the bucket did.
    /// </summary>
    /// <param name="id">The destination id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The probe outcome and its latency.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.NotFound"/> for an id that is not on this account.</exception>
    public async Task<DestinationTestResult> TestAsync(string id, CancellationToken cancellationToken = default)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        var url = DestinationUrl(id, "/test");
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Post, url, ApiTransport.EmptyJsonContent, true, cancellationToken)
            .ConfigureAwait(false);
        return ApiParse.DestinationTestResult(response.Root, url);
    }

    /// <summary>
    /// <c>POST /destinations/{id}/presign</c> — a short-lived (15 minute),
    /// server-signed PUT for ONE object in your own bucket, so something you
    /// produced yourself (an editor export, say) can be uploaded without your
    /// S3 credentials ever being in the browser.
    /// </summary>
    /// <param name="id">The destination id.</param>
    /// <param name="ext">The file extension to sign for.</param>
    /// <param name="contentType">
    /// The content type. Must be the canonical type for <paramref name="ext"/>
    /// (see <see cref="DestinationExportExtensions.CanonicalContentType"/>) —
    /// a mismatched pair is <see cref="ErrorCode.InvalidInput"/>. Defaults to
    /// the canonical one.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The url, the headers to send verbatim, the generated key and its expiry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="SnapneditException">The pair is mismatched (400), or the id is not on this account (404).</exception>
    public async Task<DestinationPresignResponse> PresignUploadAsync(
        string id,
        DestinationExportExt ext,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        var resolvedType = contentType ?? DestinationExportExtensions.CanonicalContentType(ext);
        var url = DestinationUrl(id, "/presign");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("ext", DestinationExportExtensions.ToWire(ext));
                writer.WriteString("contentType", resolvedType);
                writer.WriteEndObject();
            }),
            true,
            cancellationToken).ConfigureAwait(false);
        return ApiParse.PresignResponse(response.Root, url);
    }

    private string DestinationUrl(string id, string suffix = "") =>
        _transport.ApiUrl("/destinations/" + Uri.EscapeDataString(id) + suffix);

    private static StorageDestination Envelope(JsonElement root, string url)
    {
        var destination = Json.ObjectProperty(root, "destination");
        if (destination is null)
        {
            throw Json.Malformed(url, "missing object field \"destination\"");
        }

        return ApiParse.Destination(destination.Value, url);
    }

    private static void WriteCreate(Utf8JsonWriter writer, StorageDestinationInput input)
    {
        writer.WriteStartObject();
        writer.WriteString("name", input.Name);
        writer.WriteString("provider", StorageProviders.ToWire(input.Provider));
        writer.WriteString("bucket", input.Bucket);
        if (input.Region is not null)
        {
            writer.WriteString("region", input.Region);
        }

        if (input.Endpoint is not null)
        {
            writer.WriteString("endpoint", input.Endpoint);
        }

        if (input.AccountId is not null)
        {
            writer.WriteString("accountId", input.AccountId);
        }

        if (input.ForcePathStyle.HasValue)
        {
            writer.WriteBoolean("forcePathStyle", input.ForcePathStyle.Value);
        }

        if (input.KeyPrefix is not null)
        {
            writer.WriteString("keyPrefix", input.KeyPrefix);
        }

        writer.WriteString("accessKeyId", input.AccessKeyId);
        writer.WriteString("secretAccessKey", input.SecretAccessKey);
        if (input.IsDefault.HasValue)
        {
            writer.WriteBoolean("isDefault", input.IsDefault.Value);
        }

        if (input.DeleteAfterDelivery.HasValue)
        {
            writer.WriteBoolean("deleteAfterDelivery", input.DeleteAfterDelivery.Value);
        }

        writer.WriteEndObject();
    }

    private static void WritePatch(Utf8JsonWriter writer, StorageDestinationPatch patch)
    {
        writer.WriteStartObject();
        if (patch.Name is not null)
        {
            writer.WriteString("name", patch.Name);
        }

        if (patch.Bucket is not null)
        {
            writer.WriteString("bucket", patch.Bucket);
        }

        if (patch.ClearRegion)
        {
            writer.WriteNull("region");
        }
        else if (patch.Region is not null)
        {
            writer.WriteString("region", patch.Region);
        }

        if (patch.ClearEndpoint)
        {
            writer.WriteNull("endpoint");
        }
        else if (patch.Endpoint is not null)
        {
            writer.WriteString("endpoint", patch.Endpoint);
        }

        if (patch.ForcePathStyle.HasValue)
        {
            writer.WriteBoolean("forcePathStyle", patch.ForcePathStyle.Value);
        }

        if (patch.KeyPrefix is not null)
        {
            writer.WriteString("keyPrefix", patch.KeyPrefix);
        }

        if (patch.AccessKeyId is not null)
        {
            writer.WriteString("accessKeyId", patch.AccessKeyId);
        }

        if (patch.SecretAccessKey is not null)
        {
            writer.WriteString("secretAccessKey", patch.SecretAccessKey);
        }

        if (patch.IsDefault.HasValue)
        {
            writer.WriteBoolean("isDefault", patch.IsDefault.Value);
        }

        if (patch.DeleteAfterDelivery.HasValue)
        {
            writer.WriteBoolean("deleteAfterDelivery", patch.DeleteAfterDelivery.Value);
        }

        writer.WriteEndObject();
    }
}

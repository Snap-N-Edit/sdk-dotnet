using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Snapnedit.Internal;

namespace Snapnedit;

/// <summary>
/// Your account's webhook endpoints: the https urls a signed POST is sent to
/// when a job finishes or an embed session ends.
/// </summary>
/// <remarks>
/// Reached through <see cref="SnapneditClient.WebhookEndpoints"/>; secret key
/// only. The signing secret is returned exactly twice — by
/// <see cref="CreateAsync"/> and by <see cref="RotateSecretAsync"/> — and never
/// by a read. An endpoint id belonging to another account is
/// <see cref="ErrorCode.NotFound"/>.
/// </remarks>
public sealed class WebhookEndpointsClient
{
    private readonly ApiTransport _transport;

    internal WebhookEndpointsClient(ApiTransport transport) => _transport = transport;

    /// <summary><c>GET /webhook-endpoints</c> — every endpoint on the account, secret-free.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The account's endpoints.</returns>
    /// <exception cref="SnapneditException">The credential is not a secret key (401).</exception>
    public async Task<IReadOnlyList<WebhookEndpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        var url = EndpointUrl(null);
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Get, url, null, true, cancellationToken)
            .ConfigureAwait(false);
        var list = Json.Property(response.Root, "endpoints");
        if (list is null || list.Value.ValueKind != JsonValueKind.Array)
        {
            throw Json.Malformed(url, "missing array field \"endpoints\"");
        }

        var endpoints = new List<WebhookEndpoint>();
        foreach (var entry in list.Value.EnumerateArray())
        {
            endpoints.Add(ApiParse.WebhookEndpoint(entry, url));
        }

        return endpoints;
    }

    /// <summary><c>POST /webhook-endpoints</c> — registers an https url on a public host.</summary>
    /// <param name="url">Where deliveries are POSTed. Must be https and must not resolve to a private address.</param>
    /// <param name="events">
    /// The event types to receive (see <see cref="WebhookEndpointEvents"/>).
    /// <see langword="null"/> subscribes to all of them.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The endpoint and its signing secret — the only read that returns it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.InvalidInput"/> for a non-https or private url, or an unknown event type.</exception>
    public async Task<WebhookEndpointCreated> CreateAsync(
        string url,
        IEnumerable<string>? events = null,
        CancellationToken cancellationToken = default)
    {
        if (url is null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        var requestUrl = EndpointUrl(null);
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            requestUrl,
            () => ApiTransport.JsonContent(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("url", url);
                if (events is not null)
                {
                    writer.WritePropertyName("events");
                    writer.WriteStartArray();
                    foreach (var name in events)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }),
            true,
            cancellationToken).ConfigureAwait(false);
        return new WebhookEndpointCreated(
            Envelope(response.Root, requestUrl),
            Json.RequireString(response.Root, "secret", requestUrl));
    }

    /// <summary><c>PATCH /webhook-endpoints/{id}</c> — pauses or resumes deliveries. The secret is unchanged.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="active">False to pause, true to resume.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The updated endpoint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.NotFound"/> for an id that is not on this account.</exception>
    public async Task<WebhookEndpoint> SetActiveAsync(
        string id,
        bool active,
        CancellationToken cancellationToken = default)
    {
        var url = EndpointUrl(id ?? throw new ArgumentNullException(nameof(id)));
        using var response = await _transport.SendJsonAsync(
            new HttpMethod("PATCH"),
            url,
            () => ApiTransport.JsonContent(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("active", active);
                writer.WriteEndObject();
            }),
            true,
            cancellationToken).ConfigureAwait(false);
        return Envelope(response.Root, url);
    }

    /// <summary><c>DELETE /webhook-endpoints/{id}</c> — answers <c>204</c>.</summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the endpoint is gone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.NotFound"/> for an id that is not on this account, or already deleted.</exception>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        _transport.SendVoidAsync(
            HttpMethod.Delete,
            EndpointUrl(id ?? throw new ArgumentNullException(nameof(id))),
            null,
            cancellationToken);

    /// <summary>
    /// <c>POST /webhook-endpoints/{id}/rotate</c> — issues a new signing
    /// secret. During the rotation window a delivery may carry a <c>v1</c> for
    /// each secret; <see cref="SnapneditWebhooks.Verify(string, string, string, WebhookVerifyOptions?)"/> accepts any match.
    /// </summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The new plaintext secret.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.NotFound"/> for an id that is not on this account.</exception>
    public async Task<string> RotateSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        var url = EndpointUrl(id ?? throw new ArgumentNullException(nameof(id)), "/rotate");
        using var response = await _transport
            .SendJsonAsync(HttpMethod.Post, url, ApiTransport.EmptyJsonContent, true, cancellationToken)
            .ConfigureAwait(false);
        return Json.RequireString(response.Root, "secret", url);
    }

    private string EndpointUrl(string? id, string suffix = "") =>
        _transport.ApiUrl("/webhook-endpoints" + (id is null ? string.Empty : "/" + Uri.EscapeDataString(id)) + suffix);

    private static WebhookEndpoint Envelope(JsonElement root, string url)
    {
        var endpoint = Json.ObjectProperty(root, "endpoint");
        if (endpoint is null)
        {
            throw Json.Malformed(url, "missing object field \"endpoint\"");
        }

        return ApiParse.WebhookEndpoint(endpoint.Value, url);
    }
}

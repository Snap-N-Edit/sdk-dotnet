using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Snapnedit.Internal;

namespace Snapnedit;

/// <summary>
/// Minting the short-lived tokens the embedded editor authenticates with.
/// </summary>
/// <remarks>
/// Two doors onto the same token. <see cref="CreateSessionAsync"/> is the one
/// a FRAME uses: it exchanges a publishable <c>pk_</c> key plus the host
/// origin, and is deliberately sent with no <c>Authorization</c> header — a
/// <c>pk_</c> key presented as a bearer resolves to anonymous, never to an
/// account. <see cref="CreateTokenAsync"/> is the one YOUR SERVER uses: it
/// authenticates with your secret <c>sk_</c> key and can scope the token to an
/// end user, a credit cap and an operation allowlist. Either token is billed
/// like an API key.
/// </remarks>
public sealed class EmbedClient
{
    private readonly ApiTransport _transport;

    internal EmbedClient(ApiTransport transport) => _transport = transport;

    /// <summary>
    /// <c>POST /embed/sessions</c> — exchanges a publishable key for a
    /// short-lived embed token. Sent unauthenticated: the publishable key in
    /// the body is the credential.
    /// </summary>
    /// <param name="publishableKey">The account's <c>pk_</c> key.</param>
    /// <param name="hostOrigin">
    /// The surface embedding the editor, matched against the key's allowlist: a
    /// browser origin (<c>https://app.example.com</c>), or a native shell id
    /// (<c>native:com.acme.photos</c>) for a WebView host that has no origin.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The token and its expiry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publishableKey"/> or <paramref name="hostOrigin"/> is null.</exception>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.Forbidden"/> when the origin is not on the key's allowlist.</exception>
    public async Task<EmbedToken> CreateSessionAsync(
        string publishableKey,
        string hostOrigin,
        CancellationToken cancellationToken = default)
    {
        if (publishableKey is null)
        {
            throw new ArgumentNullException(nameof(publishableKey));
        }

        if (hostOrigin is null)
        {
            throw new ArgumentNullException(nameof(hostOrigin));
        }

        var url = _transport.ApiUrl("/embed/sessions");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("publishableKey", publishableKey);
                writer.WriteString("hostOrigin", hostOrigin);
                writer.WriteEndObject();
            }),
            // A pk_ key is only ever exchanged here; presenting the client's own
            // sk_ key alongside it would be a second, contradictory credential.
            false,
            cancellationToken).ConfigureAwait(false);
        return ApiParse.EmbedToken(response.Root, url);
    }

    /// <summary>
    /// <c>POST /embed/tokens</c> — mints an embed token from your server with
    /// your secret key, scoped as tightly as you like. Hand the token to the
    /// browser; never the key.
    /// </summary>
    /// <param name="options">TTL, end-user id, credit cap, operation allowlist, origin. All optional.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The token and its expiry.</returns>
    /// <exception cref="SnapneditException"><see cref="ErrorCode.Unauthorized"/> when the client is not holding an <c>sk_</c> key.</exception>
    public async Task<EmbedToken> CreateTokenAsync(
        EmbedTokenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = options ?? new EmbedTokenOptions();
        var url = _transport.ApiUrl("/embed/tokens");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(writer =>
            {
                writer.WriteStartObject();
                if (request.TtlSeconds.HasValue)
                {
                    writer.WriteNumber("ttlSeconds", request.TtlSeconds.Value);
                }

                if (request.EndUserId is not null)
                {
                    writer.WriteString("endUserId", request.EndUserId);
                }

                if (request.MaxCredits.HasValue)
                {
                    writer.WriteNumber("maxCredits", request.MaxCredits.Value);
                }

                if (request.AllowedOperations is not null)
                {
                    writer.WritePropertyName("allowedOperations");
                    writer.WriteStartArray();
                    foreach (var operation in request.AllowedOperations)
                    {
                        writer.WriteStringValue(operation);
                    }

                    writer.WriteEndArray();
                }

                if (request.Origin is not null)
                {
                    writer.WriteString("origin", request.Origin);
                }

                writer.WriteEndObject();
            }),
            true,
            cancellationToken).ConfigureAwait(false);
        return ApiParse.EmbedToken(response.Root, url);
    }
}

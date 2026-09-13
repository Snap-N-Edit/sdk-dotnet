using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snapnedit.Internal;

namespace Snapnedit;

/// <summary>
/// Verifying and parsing the signed webhooks snapnedit POSTs when a job
/// finishes.
/// </summary>
/// <remarks>
/// <para>
/// The header is <c>X-Snapnedit-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>
/// — comma-separated <c>k=v</c> pairs, unknown keys ignored, and <c>v1</c> may
/// appear more than once during a secret rotation (any match accepts the
/// delivery). The signed string is <c>{t}.{rawBody}</c>, MAC'd with
/// HMAC-SHA256 under the endpoint's plaintext signing secret and hex-encoded.
/// </para>
/// <para>
/// Four rules that are part of the contract, not implementation detail:
/// <list type="number">
/// <item><description><c>rawBody</c> is the bytes you RECEIVED, verified BEFORE any JSON parse — a re-serialized body will not match.</description></item>
/// <item><description>Compare in constant time.</description></item>
/// <item><description>Compare case-insensitively against lower-case hex.</description></item>
/// <item><description>Return <see langword="false"/>, never throw, for a malformed header, a bad signature or a stale timestamp.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SnapneditWebhooks
{
    /// <summary>The header carrying the signature.</summary>
    public const string SignatureHeader = "X-Snapnedit-Signature";

    /// <summary>The header carrying the event type.</summary>
    public const string EventHeader = "X-Snapnedit-Event";

    /// <summary>The header carrying the delivery id.</summary>
    public const string DeliveryHeader = "X-Snapnedit-Delivery";

    /// <summary>
    /// Verifies a signature header against the raw request body and the
    /// endpoint's signing secret.
    /// </summary>
    /// <param name="payload">The raw body as received, before any JSON parse. Read as UTF-8.</param>
    /// <param name="signature">The <c>X-Snapnedit-Signature</c> header value.</param>
    /// <param name="secret">The endpoint's plaintext signing secret (<c>whsec_…</c>).</param>
    /// <param name="options">Optional freshness window.</param>
    /// <returns><see langword="true"/> only when a signature matches and, if requested, the timestamp is fresh. Never throws for untrusted input.</returns>
    public static bool Verify(string payload, string signature, string secret, WebhookVerifyOptions? options = null)
    {
        if (payload is null || signature is null || secret is null)
        {
            return false;
        }

        return Verify(Encoding.UTF8.GetBytes(payload), signature, secret, options);
    }

    /// <summary>Verifies a signature header against the raw body BYTES — the most faithful form, with no encoding round trip at all.</summary>
    /// <param name="payload">The raw body bytes as received.</param>
    /// <param name="signature">The <c>X-Snapnedit-Signature</c> header value.</param>
    /// <param name="secret">The endpoint's plaintext signing secret.</param>
    /// <param name="options">Optional freshness window.</param>
    /// <returns><see langword="true"/> only when a signature matches and, if requested, the timestamp is fresh.</returns>
    public static bool Verify(byte[] payload, string signature, string secret, WebhookVerifyOptions? options = null)
    {
        if (payload is null || signature is null || secret is null)
        {
            return false;
        }

        var parsed = ParseHeader(signature);
        if (parsed is null)
        {
            return false;
        }

        var tolerance = options?.ToleranceSeconds;
        if (tolerance.HasValue)
        {
            var now = options?.NowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - parsed.Value.Timestamp) > tolerance.Value)
            {
                return false;
            }
        }

        var expected = ComputeHex(parsed.Value.Timestamp, payload, secret);
        foreach (var candidate in parsed.Value.Signatures)
        {
            if (FixedTimeEquals(expected, candidate.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies and then parses a delivery, the way a webhook endpoint wants
    /// it: one call that either hands back a typed event or refuses.
    /// </summary>
    /// <param name="payload">The raw body as received, before any JSON parse.</param>
    /// <param name="signature">The <c>X-Snapnedit-Signature</c> header value.</param>
    /// <param name="secret">The endpoint's plaintext signing secret.</param>
    /// <param name="options">Optional freshness window.</param>
    /// <returns>The parsed event.</returns>
    /// <exception cref="SnapneditException">
    /// <see cref="ErrorCode.Unauthorized"/> when the signature does not verify,
    /// <see cref="ErrorCode.InvalidInput"/> when the body is not a webhook
    /// delivery. Reject the request either way.
    /// </exception>
    public static WebhookEvent ConstructEvent(
        string payload,
        string signature,
        string secret,
        WebhookVerifyOptions? options = null)
    {
        if (!Verify(payload, signature, secret, options))
        {
            throw new SnapneditException(ErrorCode.Unauthorized, "webhook signature verification failed", 0);
        }

        return Parse(payload);
    }

    /// <summary>
    /// Parses a delivery body WITHOUT verifying it. Only for a payload you have
    /// already verified (or one you produced yourself in a test) — everything
    /// else should go through <see cref="ConstructEvent"/>.
    /// </summary>
    /// <param name="payload">The delivery body.</param>
    /// <returns>The parsed event.</returns>
    /// <exception cref="SnapneditException">The body is not a webhook delivery.</exception>
    public static WebhookEvent Parse(string payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, Json.DocumentOptions);
        }
        catch (JsonException cause)
        {
            throw new SnapneditException(ErrorCode.InvalidInput, "webhook body is not valid JSON", 0, cause);
        }

        using (document)
        {
            var root = document.RootElement;
            const string Source = "webhook delivery";
            var data = Json.ObjectProperty(root, "data");
            if (data is null)
            {
                throw Json.Malformed(Source, "missing object field \"data\"");
            }

            var rawType = Json.RequireString(root, "type", Source);
            var status = Json.OptionalString(data.Value, "status");
            var envelope = ApiParse.JobEnvelope(data.Value);

            var eventData = new WebhookEventData(
                Json.RequireString(data.Value, "jobId", Source),
                Json.RequireString(data.Value, "operation", Source),
                status == "failed" ? JobState.Failed : JobState.Succeeded,
                Json.OptionalString(data.Value, "outputAssetId"),
                Json.OptionalString(data.Value, "download"),
                status == "failed" ? ErrorCodes.Parse(Json.OptionalString(data.Value, "errorCode")) : (ErrorCode?)null,
                Json.OptionalString(data.Value, "message"),
                envelope.InputKind,
                envelope.Destination,
                envelope.Delivery);

            return new WebhookEvent(
                Json.RequireString(root, "id", Source),
                rawType switch
                {
                    "job.succeeded" => WebhookEventType.JobSucceeded,
                    "job.failed" => WebhookEventType.JobFailed,
                    _ => WebhookEventType.Unknown,
                },
                rawType,
                Json.OptionalLong(root, "created") ?? 0L,
                eventData);
        }
    }

    /// <summary>
    /// Computes the hex MAC snapnedit would send for a body and timestamp.
    /// Exposed for tests and for anyone building a fixture; verification should
    /// go through <see cref="Verify(string, string, string, WebhookVerifyOptions?)"/>.
    /// </summary>
    /// <param name="timestamp">Unix seconds to sign with.</param>
    /// <param name="payload">The raw body.</param>
    /// <param name="secret">The endpoint's plaintext signing secret.</param>
    /// <returns>Lower-case hex HMAC-SHA256 of <c>{timestamp}.{payload}</c>.</returns>
    public static string ComputeSignature(long timestamp, string payload, string secret) =>
        ComputeHex(timestamp, Encoding.UTF8.GetBytes(payload), secret);

    private static string ComputeHex(long timestamp, byte[] payload, string secret)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + payload.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(payload, 0, signed, prefix.Length, payload.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return ToHex(hmac.ComputeHash(signed));
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i * 2] = HexDigit(b >> 4);
            chars[(i * 2) + 1] = HexDigit(b & 0xF);
        }

        return new string(chars);
    }

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));

    /// <summary>
    /// Constant-time comparison of two ASCII hex strings. A length mismatch may
    /// short-circuit (a length is not secret); a value mismatch must not.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var mismatch = 0;
        for (var i = 0; i < a.Length; i++)
        {
            mismatch |= a[i] ^ b[i];
        }

        return mismatch == 0;
    }

    /// <summary>Parses <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>; <see langword="null"/> for any header lacking a numeric <c>t</c> or at least one <c>v1</c>.</summary>
    private static (long Timestamp, List<string> Signatures)? ParseHeader(string header)
    {
        long? timestamp = null;
        var signatures = new List<string>();

        foreach (var part in header.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = part.Substring(0, eq).Trim();
            var value = part.Substring(eq + 1).Trim();
            if (key == "t")
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    timestamp = parsed;
                }
            }
            else if (key == "v1" && value.Length > 0)
            {
                signatures.Add(value);
            }
        }

        if (!timestamp.HasValue || signatures.Count == 0)
        {
            return null;
        }

        return (timestamp.Value, signatures);
    }
}

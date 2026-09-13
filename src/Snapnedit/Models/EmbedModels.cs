using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>A short-lived embed token and when it expires.</summary>
public sealed record EmbedToken
{
    /// <summary>Creates an embed token.</summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="expiresAt">ISO-8601 expiry.</param>
    public EmbedToken(string token, string expiresAt)
    {
        Token = token;
        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// The bearer token. It authenticates as the account and is billed like an
    /// API key — hand it to a browser, never your <c>sk_</c> key.
    /// </summary>
    public string Token { get; }

    /// <summary>ISO-8601 expiry.</summary>
    public string ExpiresAt { get; }
}

/// <summary>Options for <see cref="EmbedClient.CreateTokenAsync"/> — the server-side mint, scoped as tightly as you like.</summary>
public sealed record EmbedTokenOptions
{
    /// <summary>Lifetime in seconds, 60..86400. Omit for the api default.</summary>
    public int? TtlSeconds { get; init; }

    /// <summary>Your own id for the end user this session belongs to, 1..200 characters. Shows up on usage records.</summary>
    public string? EndUserId { get; init; }

    /// <summary>A hard credit ceiling for everything done with this token.</summary>
    public int? MaxCredits { get; init; }

    /// <summary>Restrict the token to these operation ids. At least one when set.</summary>
    public IReadOnlyList<string>? AllowedOperations { get; init; }

    /// <summary>The host origin to stamp into the token. Informational after minting.</summary>
    public string? Origin { get; init; }
}

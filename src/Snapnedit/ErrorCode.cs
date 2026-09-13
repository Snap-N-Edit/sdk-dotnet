using System;

namespace Snapnedit;

/// <summary>
/// The closed set of machine-readable error codes the snapnedit api sends in
/// its <c>{ "error": { "code", "message" } }</c> envelope, and that a failed
/// job reports as <c>errorCode</c>.
/// </summary>
/// <remarks>
/// Branch on the code, never on the message: messages are human text and
/// change without notice. A code this enum does not know about (an api newer
/// than this package) is surfaced as <see cref="Internal"/> rather than
/// throwing while parsing the error itself.
/// </remarks>
public enum ErrorCode
{
    /// <summary>The request body, its params, or a referenced id failed validation. HTTP 400.</summary>
    InvalidInput = 0,

    /// <summary>The declared upload mime is not one the api accepts (<c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>). HTTP 400.</summary>
    UnsupportedMime = 1,

    /// <summary>The payload exceeds the upload size ceiling. HTTP 400 or 413.</summary>
    TooLarge = 2,

    /// <summary>No such job, asset or destination — also what another account's id looks like, so an id can never become an existence oracle. HTTP 404.</summary>
    NotFound = 3,

    /// <summary>A job whose input was an external <c>inputUrl</c> could not be fetched (blocked address, redirect, timeout, non-2xx, oversized, or not an image). Terminal; credits are refunded.</summary>
    InputFetchFailed = 4,

    /// <summary>A model provider failed on this job. Retryable.</summary>
    ProviderFailed = 5,

    /// <summary>Every provider that can serve the operation failed.</summary>
    ProviderExhausted = 6,

    /// <summary>The caller is being rate limited. HTTP 429; retry after a backoff.</summary>
    RateLimited = 7,

    /// <summary>A bot-protection challenge was not satisfied.</summary>
    BotCheckFailed = 8,

    /// <summary>No credential, or a credential the api does not recognize. HTTP 401.</summary>
    Unauthorized = 9,

    /// <summary>Authenticated, but this caller may not do this (for example an anonymous caller supplying <c>inputUrl</c>). HTTP 403.</summary>
    Forbidden = 10,

    /// <summary>Out of credits, or over a free-tier limit. HTTP 402.</summary>
    PaymentRequired = 11,

    /// <summary>An unexpected server-side failure — and the fallback for any code this package does not recognize.</summary>
    Internal = 12,
}

/// <summary>Conversions between <see cref="ErrorCode"/> and its wire spelling.</summary>
public static class ErrorCodes
{
    /// <summary>Every code, in the order the api publishes them.</summary>
    public static readonly ErrorCode[] All =
    {
        ErrorCode.InvalidInput,
        ErrorCode.UnsupportedMime,
        ErrorCode.TooLarge,
        ErrorCode.NotFound,
        ErrorCode.InputFetchFailed,
        ErrorCode.ProviderFailed,
        ErrorCode.ProviderExhausted,
        ErrorCode.RateLimited,
        ErrorCode.BotCheckFailed,
        ErrorCode.Unauthorized,
        ErrorCode.Forbidden,
        ErrorCode.PaymentRequired,
        ErrorCode.Internal,
    };

    /// <summary>The wire spelling of <paramref name="code"/>, e.g. <c>input_fetch_failed</c>.</summary>
    /// <param name="code">The code to render.</param>
    /// <returns>The snake_case string the api uses.</returns>
    public static string ToWire(ErrorCode code) => code switch
    {
        ErrorCode.InvalidInput => "invalid_input",
        ErrorCode.UnsupportedMime => "unsupported_mime",
        ErrorCode.TooLarge => "too_large",
        ErrorCode.NotFound => "not_found",
        ErrorCode.InputFetchFailed => "input_fetch_failed",
        ErrorCode.ProviderFailed => "provider_failed",
        ErrorCode.ProviderExhausted => "provider_exhausted",
        ErrorCode.RateLimited => "rate_limited",
        ErrorCode.BotCheckFailed => "bot_check_failed",
        ErrorCode.Unauthorized => "unauthorized",
        ErrorCode.Forbidden => "forbidden",
        ErrorCode.PaymentRequired => "payment_required",
        ErrorCode.Internal => "internal",
        _ => "internal",
    };

    /// <summary>
    /// Parses a wire code. Anything unrecognized (including <see langword="null"/>)
    /// degrades to <see cref="ErrorCode.Internal"/>, so an api that grows a new
    /// code still produces a usable typed error instead of a parse failure.
    /// </summary>
    /// <param name="value">The wire string, or <see langword="null"/>.</param>
    /// <returns>The matching code, or <see cref="ErrorCode.Internal"/>.</returns>
    public static ErrorCode Parse(string? value) => value switch
    {
        "invalid_input" => ErrorCode.InvalidInput,
        "unsupported_mime" => ErrorCode.UnsupportedMime,
        "too_large" => ErrorCode.TooLarge,
        "not_found" => ErrorCode.NotFound,
        "input_fetch_failed" => ErrorCode.InputFetchFailed,
        "provider_failed" => ErrorCode.ProviderFailed,
        "provider_exhausted" => ErrorCode.ProviderExhausted,
        "rate_limited" => ErrorCode.RateLimited,
        "bot_check_failed" => ErrorCode.BotCheckFailed,
        "unauthorized" => ErrorCode.Unauthorized,
        "forbidden" => ErrorCode.Forbidden,
        "payment_required" => ErrorCode.PaymentRequired,
        "internal" => ErrorCode.Internal,
        _ => ErrorCode.Internal,
    };
}

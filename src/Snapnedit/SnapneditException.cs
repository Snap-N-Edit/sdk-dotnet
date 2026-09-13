using System;

namespace Snapnedit;

/// <summary>
/// Every failure this client reports: a non-2xx api response, a job that
/// reached <c>failed</c>, or a malformed response body.
/// </summary>
/// <remarks>
/// A 402 (insufficient credits / free-tier limit) is this same exception with
/// <see cref="Code"/> set to <see cref="ErrorCode.PaymentRequired"/> — there
/// is no separate class, because the shape a caller needs is identical.
/// </remarks>
public class SnapneditException : Exception
{
    /// <summary>Creates an exception carrying the api's typed error code and HTTP status.</summary>
    /// <param name="code">The machine-readable error code to branch on.</param>
    /// <param name="message">The human-readable message. Never parse it.</param>
    /// <param name="status">The HTTP status that produced it, or <c>0</c> for a client-side/network failure.</param>
    public SnapneditException(ErrorCode code, string message, int status)
        : base(message)
    {
        Code = code;
        Status = status;
    }

    /// <summary>Creates an exception carrying the api's typed error code, HTTP status and an inner cause.</summary>
    /// <param name="code">The machine-readable error code to branch on.</param>
    /// <param name="message">The human-readable message. Never parse it.</param>
    /// <param name="status">The HTTP status that produced it, or <c>0</c> for a client-side/network failure.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public SnapneditException(ErrorCode code, string message, int status, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
        Status = status;
    }

    /// <summary>The machine-readable error code. Branch on this, never on <see cref="Exception.Message"/>.</summary>
    public ErrorCode Code { get; }

    /// <summary>The HTTP status that produced the error, or <c>0</c> when there was no response (network failure, malformed body).</summary>
    public int Status { get; }
}

/// <summary>
/// Thrown when polling a job never reached a settled state within the
/// caller's budget. A client-side timeout is not an api response, so
/// <see cref="SnapneditException.Status"/> is <c>0</c> and
/// <see cref="SnapneditException.Code"/> is <see cref="ErrorCode.Internal"/>.
/// </summary>
public sealed class SnapneditTimeoutException : SnapneditException
{
    /// <summary>Creates a poll-timeout exception.</summary>
    /// <param name="message">What timed out, and after how long.</param>
    public SnapneditTimeoutException(string message)
        : base(ErrorCode.Internal, message, 0)
    {
    }
}

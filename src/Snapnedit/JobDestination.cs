using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>
/// BRING YOUR OWN STORAGE, output half: where the server delivers the result
/// bytes, so they never round-trip through this process.
/// </summary>
/// <remarks>
/// Three forms, and the difference between the last two matters:
/// <list type="bullet">
/// <item><description><see cref="PresignedPut"/> — a PUT you signed yourself.</description></item>
/// <item><description><see cref="Saved"/> — one of your account's saved destinations; the SERVER signs the upload.</description></item>
/// <item><description><see cref="OptOut"/> — send <c>destination: null</c>, explicitly declining your account default. OMITTING the destination (leaving <see cref="CreateJobOptions.Destination"/> null) means "apply my default if I have one".</description></item>
/// </list>
/// A destination requires an API key or embed token; an anonymous caller gets
/// <see cref="ErrorCode.Forbidden"/>. <see cref="OptOut"/> is exempt.
/// </remarks>
public abstract class JobDestination
{
    private JobDestination()
    {
    }

    /// <summary>
    /// Explicitly send <c>destination: null</c> — decline the account default
    /// for this one job. Not the same as omitting the destination.
    /// </summary>
    public static JobDestination OptOut { get; } = new NullDestination();

    /// <summary>A presigned PUT you signed against your own bucket.</summary>
    /// <param name="url">The presigned url. Never echoed back by any endpoint or webhook.</param>
    /// <param name="headers">
    /// Headers the signature requires. Only <c>content-type</c>,
    /// <c>cache-control</c>, <c>content-disposition</c> and <c>x-amz-*</c> /
    /// <c>x-goog-*</c> / <c>x-ms-*</c> are accepted, at most 16 — anything else
    /// is a 400.
    /// </param>
    /// <returns>The destination.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    public static JobDestination PresignedPut(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (url is null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        return new PresignedPutDestination(url, headers);
    }

    /// <summary>One of your account's saved storage destinations, by id.</summary>
    /// <param name="id">The destination id from <see cref="DestinationsClient.ListAsync"/>.</param>
    /// <returns>The destination.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public static JobDestination Saved(string id)
    {
        if (id is null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        return new SavedDestination(id);
    }

    internal sealed class NullDestination : JobDestination
    {
    }

    internal sealed class PresignedPutDestination : JobDestination
    {
        internal PresignedPutDestination(string url, IReadOnlyDictionary<string, string>? headers)
        {
            Url = url;
            Headers = headers;
        }

        internal string Url { get; }

        internal IReadOnlyDictionary<string, string>? Headers { get; }
    }

    internal sealed class SavedDestination : JobDestination
    {
        internal SavedDestination(string id) => Id = id;

        internal string Id { get; }
    }
}

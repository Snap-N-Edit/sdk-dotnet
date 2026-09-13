using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Snapnedit;

/// <summary>Which form a <see cref="JobInput"/> takes.</summary>
public enum JobInputSource
{
    /// <summary>Bytes this process holds; the client uploads them first.</summary>
    Bytes = 0,

    /// <summary>A stream this process reads; the client buffers and uploads it.</summary>
    Stream = 1,

    /// <summary>A file on disk; the client reads and uploads it.</summary>
    File = 2,

    /// <summary>An https url the SERVER fetches — the bytes never pass through this process.</summary>
    Url = 3,

    /// <summary>An asset id already uploaded through <see cref="SnapneditClient.UploadAsync(byte[], string?, CancellationToken)"/>.</summary>
    AssetId = 4,
}

/// <summary>
/// A job's input image, in any of the forms the api and this client accept.
/// Build one with <see cref="FromBytes"/>, <see cref="FromStream"/>,
/// <see cref="FromFile"/>, <see cref="FromUrl"/> or <see cref="FromAssetId"/>.
/// </summary>
public sealed class JobInput
{
    private readonly byte[]? _bytes;
    private readonly Stream? _stream;
    private readonly string? _value;

    private JobInput(JobInputSource source, byte[]? bytes, Stream? stream, string? value, string? mime)
    {
        Source = source;
        _bytes = bytes;
        _stream = stream;
        _value = value;
        Mime = mime;
    }

    /// <summary>Which form this input takes.</summary>
    public JobInputSource Source { get; }

    /// <summary>
    /// The declared content type for an uploaded input. Ignored for
    /// <see cref="JobInputSource.Url"/> (the server sniffs the fetched bytes)
    /// and for <see cref="JobInputSource.AssetId"/>.
    /// </summary>
    public string? Mime { get; }

    /// <summary>The url, for <see cref="JobInputSource.Url"/>.</summary>
    public string? Url => Source == JobInputSource.Url ? _value : null;

    /// <summary>The asset id, for <see cref="JobInputSource.AssetId"/>.</summary>
    public string? AssetId => Source == JobInputSource.AssetId ? _value : null;

    /// <summary>True when the client has to upload the bytes before creating the job.</summary>
    public bool RequiresUpload =>
        Source == JobInputSource.Bytes || Source == JobInputSource.Stream || Source == JobInputSource.File;

    /// <summary>An image already in memory.</summary>
    /// <param name="bytes">The encoded image bytes (png, jpeg or webp).</param>
    /// <param name="mime">Content type to declare. Defaults to <c>application/octet-stream</c>, which the api refuses — pass the real one.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    public static JobInput FromBytes(byte[] bytes, string? mime = null)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        return new JobInput(JobInputSource.Bytes, bytes, null, null, mime);
    }

    /// <summary>
    /// An image from a stream. The stream is read to the end exactly once (the
    /// api needs the byte length up front), and is NOT disposed by this client.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the start of the image.</param>
    /// <param name="mime">Content type to declare.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static JobInput FromStream(Stream stream, string? mime = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return new JobInput(JobInputSource.Stream, null, stream, null, mime);
    }

    /// <summary>An image on disk. The content type is inferred from the extension unless <paramref name="mime"/> says otherwise.</summary>
    /// <param name="path">Path to a png, jpeg or webp file.</param>
    /// <param name="mime">Content type to declare, overriding the extension.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static JobInput FromFile(string path, string? mime = null)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return new JobInput(JobInputSource.File, null, null, path, mime ?? MimeFromPath(path));
    }

    /// <summary>
    /// BRING YOUR OWN STORAGE, input half: an image already in your bucket,
    /// named by a url the SERVER fetches — no upload happens at all.
    /// </summary>
    /// <param name="url">
    /// An https url (a short-lived presigned GET is the usual choice). The api
    /// refuses private, loopback, link-local and metadata addresses, follows no
    /// redirects, times out at 30s and stops at the upload size ceiling. Any of
    /// those is a terminal <see cref="ErrorCode.InputFetchFailed"/> with the
    /// credits refunded. Requires an API key or embed token — an anonymous
    /// caller gets <see cref="ErrorCode.Forbidden"/>.
    /// </param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is null.</exception>
    public static JobInput FromUrl(string url)
    {
        if (url is null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        return new JobInput(JobInputSource.Url, null, null, url, null);
    }

    /// <summary>An asset already uploaded and confirmed — reuse it across jobs without re-uploading.</summary>
    /// <param name="assetId">The asset id from <see cref="UploadResult.AssetId"/>.</param>
    /// <returns>The input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assetId"/> is null.</exception>
    public static JobInput FromAssetId(string assetId)
    {
        if (assetId is null)
        {
            throw new ArgumentNullException(nameof(assetId));
        }

        return new JobInput(JobInputSource.AssetId, null, null, assetId, null);
    }

    /// <summary>Materializes the bytes this input represents, buffering a stream or reading a file if need be.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The encoded image bytes.</returns>
    /// <exception cref="InvalidOperationException">This input has no bytes (it is a url or an asset id).</exception>
    public async Task<byte[]> ReadBytesAsync(CancellationToken cancellationToken = default)
    {
        switch (Source)
        {
            case JobInputSource.Bytes:
                return _bytes!;
            case JobInputSource.File:
                return await ReadFileAsync(_value!, cancellationToken).ConfigureAwait(false);
            case JobInputSource.Stream:
                return await ReadStreamAsync(_stream!, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException(
                    "this JobInput carries no bytes (source: " + Source + ") — it names a url or an asset id");
        }
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await ReadStreamAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string? MimeFromPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        switch (ext.ToLowerInvariant())
        {
            case ".png":
                return "image/png";
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".webp":
                return "image/webp";
            default:
                return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Snapnedit.Internal;

namespace Snapnedit;

/// <summary>Output encoding for <see cref="DesignsClient.RenderAsync"/>.</summary>
public enum RenderFormat
{
    /// <summary>Wire value <c>"png"</c>.</summary>
    Png = 0,

    /// <summary>Wire value <c>"jpeg"</c>.</summary>
    Jpeg = 1,

    /// <summary>Wire value <c>"webp"</c>.</summary>
    Webp = 2,

    /// <summary>Wire value <c>"avif"</c>.</summary>
    Avif = 3,

    /// <summary>Wire value <c>"pdf"</c> — the only format for a multi-page render.</summary>
    Pdf = 4,
}

/// <summary>
/// The body of <c>POST /designs/render</c>. Give it exactly one source: a
/// <see cref="Spec"/> or a compiled <see cref="Document"/> for a single-page
/// image, or <see cref="Pages"/> / <see cref="Documents"/> (or
/// <see cref="RenderFormat.Pdf"/>) for a PDF.
/// </summary>
public sealed record DesignRenderRequest
{
    /// <summary>A declarative design spec, as JSON.</summary>
    public JsonElement? Spec { get; init; }

    /// <summary>An already-compiled editor document, as returned by <see cref="DesignsClient.CreateAsync"/>.</summary>
    public JsonElement? Document { get; init; }

    /// <summary>One spec per page, in order.</summary>
    public IReadOnlyList<JsonElement>? Pages { get; init; }

    /// <summary>One compiled document per page, in order.</summary>
    public IReadOnlyList<JsonElement>? Documents { get; init; }

    /// <summary>Output encoding. Omit for the api default.</summary>
    public RenderFormat? Format { get; init; }
}

/// <summary>
/// Server-side design composition and rendering — how an agent composes a
/// design rather than just running an image operation.
/// </summary>
/// <remarks>
/// The design spec is a large, evolving structure owned by the editor
/// (<c>packages/editor-core</c>), so this client takes and returns it as raw
/// <see cref="JsonElement"/> rather than pinning a C# mirror of it that would
/// drift. Build a spec with <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>
/// or <c>System.Text.Json.Nodes</c>; see
/// <see href="https://snapnedit.com/docs/api-reference">the API reference</see>
/// for the schema.
/// </remarks>
public sealed class DesignsClient
{
    private readonly ApiTransport _transport;

    internal DesignsClient(ApiTransport transport) => _transport = transport;

    /// <summary><c>POST /designs</c> — compiles a design spec into an editor document.</summary>
    /// <param name="spec">The design spec.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The compiled document. Feed it back to <see cref="RenderAsync"/> or load it in the editor.</returns>
    /// <exception cref="SnapneditException">The spec failed validation, or the response carried no document.</exception>
    public async Task<JsonElement> CreateAsync(JsonElement spec, CancellationToken cancellationToken = default)
    {
        var url = _transport.ApiUrl("/designs");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(spec.WriteTo),
            true,
            cancellationToken).ConfigureAwait(false);
        var document = Json.ObjectProperty(response.Root, "document");
        if (document is null)
        {
            throw Json.Malformed(url, "missing object field \"document\"");
        }

        return document.Value.Clone();
    }

    /// <summary><c>POST /designs</c> with <c>{ pages: [...] }</c> — compiles one document per page, in order.</summary>
    /// <param name="multiPageSpec">The multi-page spec.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One compiled document per page.</returns>
    /// <exception cref="SnapneditException">The spec failed validation, or the response carried no documents.</exception>
    public async Task<IReadOnlyList<JsonElement>> CreatePagesAsync(
        JsonElement multiPageSpec,
        CancellationToken cancellationToken = default)
    {
        var url = _transport.ApiUrl("/designs");
        using var response = await _transport.SendJsonAsync(
            HttpMethod.Post,
            url,
            () => ApiTransport.JsonContent(multiPageSpec.WriteTo),
            true,
            cancellationToken).ConfigureAwait(false);
        var documents = Json.Property(response.Root, "documents");
        if (documents is null || documents.Value.ValueKind != JsonValueKind.Array)
        {
            throw Json.Malformed(url, "missing array field \"documents\"");
        }

        var compiled = new List<JsonElement>();
        foreach (var entry in documents.Value.EnumerateArray())
        {
            compiled.Add(entry.Clone());
        }

        return compiled;
    }

    /// <summary><c>POST /designs/render</c> — renders a design to image or PDF bytes, server-side. No browser involved.</summary>
    /// <param name="request">Exactly one source, plus an optional format.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The rendered bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="SnapneditException">The api refused the render.</exception>
    public async Task<byte[]> RenderAsync(DesignRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var url = _transport.ApiUrl("/designs/render");
        return await _transport.PostForBytesAsync(
            url,
            () => ApiTransport.JsonContent(writer => WriteRender(writer, request)),
            cancellationToken).ConfigureAwait(false);
    }

    private static void WriteRender(Utf8JsonWriter writer, DesignRenderRequest request)
    {
        writer.WriteStartObject();
        if (request.Spec.HasValue)
        {
            writer.WritePropertyName("spec");
            request.Spec.Value.WriteTo(writer);
        }

        if (request.Document.HasValue)
        {
            writer.WritePropertyName("document");
            request.Document.Value.WriteTo(writer);
        }

        if (request.Pages is not null)
        {
            writer.WritePropertyName("pages");
            writer.WriteStartArray();
            foreach (var page in request.Pages)
            {
                page.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        if (request.Documents is not null)
        {
            writer.WritePropertyName("documents");
            writer.WriteStartArray();
            foreach (var document in request.Documents)
            {
                document.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        if (request.Format.HasValue)
        {
            writer.WriteString("format", request.Format.Value switch
            {
                RenderFormat.Jpeg => "jpeg",
                RenderFormat.Webp => "webp",
                RenderFormat.Avif => "avif",
                RenderFormat.Pdf => "pdf",
                _ => "png",
            });
        }

        writer.WriteEndObject();
    }
}

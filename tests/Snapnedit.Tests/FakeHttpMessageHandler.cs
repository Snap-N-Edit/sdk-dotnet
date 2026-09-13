using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Snapnedit.Tests;

/// <summary>One request the fake handler saw, captured before the response was produced.</summary>
public sealed record RecordedRequest(string Method, string Url, string Body, HttpRequestMessage Message)
{
    /// <summary>The <c>Authorization</c> header value, if any.</summary>
    public string? Authorization => Message.Headers.Authorization?.ToString();

    /// <summary>The request's declared content type, if any.</summary>
    public string? ContentType => Message.Content?.Headers.ContentType?.ToString();
}

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/>: every request is recorded, and
/// the response comes from a caller-supplied function. Lets the whole client be
/// exercised — retries and all — with no live network.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, int, HttpResponseMessage> _respond;
    private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();

    /// <summary>Scripts the handler with a function of (request, zero-based call index).</summary>
    /// <param name="respond">Produces the response for each call.</param>
    public FakeHttpMessageHandler(Func<RecordedRequest, int, HttpResponseMessage> respond) => _respond = respond;

    /// <summary>Scripts the handler with a fixed queue of responses, in order.</summary>
    /// <param name="responses">The responses to hand out.</param>
    public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
        : this((_, index) => index < responses.Length
            ? responses[index]
            : throw new InvalidOperationException("unexpected request #" + (index + 1)))
    {
    }

    /// <summary>Every request the handler saw, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>The urls of every request, in order.</summary>
    public IReadOnlyList<string> Urls => _requests.Select(r => r.Url).ToList();

    /// <summary>Builds a JSON response.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="json">The body.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>Builds a binary response.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="bytes">The body.</param>
    /// <param name="contentType">The content type.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Binary(HttpStatusCode status, byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    /// <summary>Builds an empty response.</summary>
    /// <param name="status">The status code.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Empty(HttpStatusCode status) =>
        new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
        var recorded = new RecordedRequest(
            request.Method.Method,
            request.RequestUri!.ToString(),
            body,
            request);
        var index = _requests.Count;
        _requests.Add(recorded);
        return _respond(recorded, index);
    }
}

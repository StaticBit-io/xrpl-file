using System.Text.Json;

namespace XrplFile;

/// <summary>
/// One answer from a node as the connection classes see it: the <c>result</c> member, and the
/// envelope's <c>warning</c> beside it, which <see cref="Xrpl.Client.XrplResponse{T}.Raw"/> does
/// not reach (spec 4.8).
/// </summary>
/// <param name="Result">The <c>result</c> member exactly as the node sent it. Valid until the answer is disposed when <paramref name="Owner"/> is set.</param>
/// <param name="Warning">The envelope's <c>warning</c> — <c>"load"</c> when the client nears its rate limit — or null.</param>
/// <param name="Owner">What backs <paramref name="Result"/>, released by <see cref="Dispose"/>; null when nothing does.</param>
internal readonly record struct NodeAnswer(JsonElement Result, string? Warning, IDisposable? Owner = null) : IDisposable
{
    /// <summary>
    /// Reads one <c>result</c> member out of the SDK's frame. <see cref="JsonDocument.ParseValue"/>
    /// copies it into <see cref="System.Buffers.ArrayPool{T}"/> buffers that go back on
    /// <see cref="Dispose"/>, so a page-sized answer leaves no page-sized garbage of its own;
    /// the SDK's frame stays the one large-object-heap array per page.
    /// <c>RawJson</c> exposes only a span, hence the one copy.
    /// </summary>
    internal static NodeAnswer Parse(ReadOnlySpan<byte> result, string? warning)
    {
        if (result.IsEmpty) return new NodeAnswer(default, warning);

        Utf8JsonReader reader = new(result);
        JsonDocument document = JsonDocument.ParseValue(ref reader);

        return new NodeAnswer(document.RootElement, warning, document);
    }

    /// <summary>Releases the buffers behind <see cref="Result"/>; the element is unusable afterwards.</summary>
    public void Dispose() => Owner?.Dispose();
}

using System.Text.Json;

namespace XrplFile;

/// <summary>
/// One answer from a node as the connection classes see it: the <c>result</c> member, and the
/// envelope's <c>warning</c> beside it, which <see cref="Xrpl.Client.XrplResponse{T}.Raw"/> does
/// not reach (spec 4.8).
/// </summary>
/// <param name="Result">The <c>result</c> member exactly as the node sent it.</param>
/// <param name="Warning">The envelope's <c>warning</c> — <c>"load"</c> when the client nears its rate limit — or null.</param>
internal readonly record struct NodeAnswer(JsonElement Result, string? Warning);

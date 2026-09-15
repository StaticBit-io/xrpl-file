namespace XrplFile.App.Core;

/// <summary>
/// Everything a run needs, settled before any node is contacted. The bytes travel with it because
/// <see cref="PublicationSession"/> takes the whole file as a <see cref="ReadOnlyMemory{T}"/> — there
/// is no streaming path into it, which is also why <see cref="PublishModel.MaxFileBytes"/> exists.
/// </summary>
public sealed record PublishRequest(
    ReadOnlyMemory<byte> File,
    string Type,
    string Name,
    string KeyReference,
    string Account,
    string Network);

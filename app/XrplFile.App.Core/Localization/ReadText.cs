using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Read screen: the link lookup, the progress of a read, its outcome, and the sentences for a read or lookup that failed.</summary>
public static class ReadText
{
    public static readonly TextId Title = Id();
    public static readonly TextId RunningTitle = Id();
    public static readonly TextId ProgressBlocks = Id();
    public static readonly TextId ProgressBytes = Id();

    public static readonly TextId Succeeded = Id();
    public static readonly TextId SucceededNamed = Id();
    public static readonly TextId SaveButton = Id();
    public static readonly TextId Saved = Id();
    public static readonly TextId CancelledNote = Id();

    public static readonly TextId LinkLabel = Id();
    public static readonly TextId LookUpButton = Id();
    public static readonly TextId ReadButton = Id();

    public static readonly TextId NoName = Id();
    public static readonly TextId ReadTime = Id();
    public static readonly TextId WrittenBy = Id();
    public static readonly TextId Blocks = Id();
    public static readonly TextId Node = Id();

    public static readonly TextId ErrorInvalidLink = Id();
    public static readonly TextId ErrorManifestNotFound = Id();
    public static readonly TextId ErrorNodeLacksHistory = Id();
    public static readonly TextId ErrorCorruptManifest = Id();
    public static readonly TextId ErrorListMismatch = Id();
    public static readonly TextId ErrorBlockMismatch = Id();
    public static readonly TextId ErrorChunkMissing = Id();
    public static readonly TextId ErrorTooLarge = Id();
    public static readonly TextId ErrorNodeUnreachable = Id();
    public static readonly TextId ErrorNotCausedByReading = Id();
    public static readonly TextId ErrorWithBlock = Id();
    public static readonly TextId ErrorTemporaryWriteFailed = Id();
    public static readonly TextId ErrorGeneric = Id();
    public static readonly TextId ErrorSaveFailed = Id();
    public static readonly TextId ErrorSaveFailedGeneric = Id();
    public static readonly TextId NoNodeUrl = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Read", name);
}

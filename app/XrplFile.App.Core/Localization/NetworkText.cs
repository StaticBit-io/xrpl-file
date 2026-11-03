using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>Choosing a network: the drop-down shared by the screens that file a key under one, and what is said of a name that is not one.</summary>
public static class NetworkText
{
    public static readonly TextId ChooseNetwork = Id();
    public static readonly TextId NotANetwork = Id();
    public static readonly TextId NetworkName = Id();
    public static readonly TextId NetworkById = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Network", name);
}

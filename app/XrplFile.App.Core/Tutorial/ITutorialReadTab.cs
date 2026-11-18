namespace XrplFile.App.Core.Tutorial;

/// <summary>What the Read topic does to the Read tab when it is done for real.</summary>
public interface ITutorialReadTab
{
    /// <summary>Puts <paramref name="link"/> in the link box.</summary>
    void PutLink(string link);

    /// <summary>Empties the link box and the lookup result.</summary>
    void Clear();

    /// <summary>Cancels the read in progress, which deletes its temporary copy as any cancel does.</summary>
    void CancelRead();
}

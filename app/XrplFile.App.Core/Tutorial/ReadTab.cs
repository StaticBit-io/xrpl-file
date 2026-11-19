namespace XrplFile.App.Core.Tutorial;

/// <summary>The real Read tab behind <see cref="ITutorialReadTab"/>.</summary>
public sealed class ReadTab(ReadModel read, ReadRun run) : ITutorialReadTab
{
    /// <inheritdoc />
    public void PutLink(string link) => read.Link = link;

    /// <inheritdoc />
    public void Clear() => read.Reset();

    /// <inheritdoc />
    public void CancelRead() => run.Cancel();
}

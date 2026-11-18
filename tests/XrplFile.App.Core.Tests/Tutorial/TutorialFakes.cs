using XrplFile.App.Core.Tutorial;

namespace XrplFile.App.Core.Tests.Tutorial;

internal sealed class FakeReadTab : ITutorialReadTab
{
    public List<string> Links { get; } = [];

    public int Clears { get; private set; }

    public int Cancels { get; private set; }

    public void PutLink(string link) => Links.Add(link);

    public void Clear() => Clears++;

    public void CancelRead() => Cancels++;
}

internal sealed class FakeTutorialSettings : ITutorialSettingsStore
{
    public bool Offered { get; set; }

    public bool MarkSucceeds { get; set; } = true;

    public int Marks { get; private set; }

    public Action? OnMark { get; set; }

    public bool WasOffered() => Offered;

    public bool TryMarkOffered()
    {
        Marks++;
        OnMark?.Invoke();
        return MarkSucceeds;
    }
}

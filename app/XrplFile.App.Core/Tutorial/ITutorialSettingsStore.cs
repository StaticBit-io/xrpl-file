namespace XrplFile.App.Core.Tutorial;

/// <summary>Remembers, across runs, that the first-run tutorial offer has been shown.</summary>
public interface ITutorialSettingsStore
{
    /// <summary>
    /// True when the offer was shown before. A missing flag means it was not; a flag that exists
    /// counts as shown even if it cannot be read.
    /// </summary>
    bool WasOffered();

    /// <summary>
    /// Records that the offer was shown. Returns false when the flag could not be written
    /// (I/O or permission failure); it never throws for those.
    /// </summary>
    bool TryMarkOffered();
}

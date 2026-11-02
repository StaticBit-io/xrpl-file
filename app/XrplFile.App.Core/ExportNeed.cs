using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The one rule for whether a key still needs exporting, shared by the two models that ask.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AccountsModel"/> asks it of every row, to decide which entries wear the "exists only
/// here" badge.
/// <see cref="AddKeyModel.LastStoredNeedsExporting"/> asks it of an entry that has just been
/// written, to decide whether the add-key screen offers the export step before moving on (design
/// section 6, "Adding a key": "immediately after generating, the screen offers to export — not a
/// menu item elsewhere").
/// </para>
/// <para>
/// One rule, in one place, because the two disagreed. The add-key screen decided from the tab it
/// was on rather than from the entry, so a container this application wrote for a generated key,
/// re-imported through the "Open a backup file" tab, produced a <see cref="VaultEntryKind.Generated"/>
/// entry that the list flagged and the import route never offered to export.
/// </para>
/// </remarks>
public static class ExportNeed
{
    /// <summary>
    /// Whether a key of this kind is one only this vault can hold. <see cref="VaultEntryKind.Generated"/>
    /// keys were born here and exist nowhere else until a container is saved;
    /// <see cref="VaultEntryKind.Imported"/> keys are "an account they had before and will have
    /// afterwards", so their owner already has them somewhere.
    /// </summary>
    public static bool ForKind(VaultEntryKind kind) => kind == VaultEntryKind.Generated;

    /// <summary>
    /// Whether <paramref name="info"/> is a key that exists only in this vault right now: one of a
    /// kind that started here, which has not been exported yet.
    /// </summary>
    /// <remarks>
    /// A freshly stored entry always has <see cref="VaultEntryInfo.Exported"/> unset, which is why
    /// <see cref="ForKind"/> alone is the right question there and this one is the right question
    /// for a row that may have been exported at any time since.
    /// </remarks>
    public static bool For(VaultEntryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return ForKind(info.Kind) && !info.Exported;
    }
}

using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>
/// The publications list (design 4.5). Forgetting a row and clearing the list change this file and
/// nothing else — the file is still in the ledger and the link still works for anyone who kept it,
/// which the screen says out loud.
/// </summary>
public sealed class HistoryModel(PublicationHistory history, IClipboardService clipboard)
{
    public IReadOnlyList<PublicationRecord> Records { get; private set; } = [];

    /// <summary>Why the list is empty, when it is empty for a reason worth showing.</summary>
    public UiText? Message { get; private set; }

    public void Refresh()
    {
        Records = history.Read();
        Message = (history.Failure, history.RecoveryNote) switch
        {
            (null, null) => null,
            ({ } failure, null) => failure,
            (null, { } note) => note,
            ({ } failure, { } note) => UiText.Of(HistoryText.FailureAndNote, ("failure", failure), ("note", note)),
        };
    }

    public Task CopyLinkAsync(PublicationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        return clipboard.CopyAsync(record.Link, cancellationToken);
    }

    public void Forget(PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        history.Forget(record.Link);
        Refresh();
    }

    public void Clear()
    {
        history.Clear();
        Refresh();
    }
}

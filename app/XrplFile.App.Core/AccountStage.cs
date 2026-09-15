namespace XrplFile.App.Core;

/// <summary>Where a throwaway account is in its life (design 4.1).</summary>
public enum AccountStage
{
    /// <summary>
    /// Nobody has asked a node about this account yet. Distinct from <see cref="KeyOnly"/>: that
    /// means a node confirmed there is nothing on the ledger, and this means no node has been asked
    /// at all — including when a row carries a <see cref="AccountRow.DeletableFromLedger"/> cached
    /// from an earlier session, which says only how long a previous read expected to wait, not
    /// what the account holds now. A cached countdown is not itself an answer.
    /// </summary>
    Unknown,

    /// <summary>
    /// A key, and no account. On the XRP Ledger an account exists once it holds its reserve, so
    /// until somebody funds the address there is nothing on the ledger at all. Reached only once a
    /// node has actually said so.
    /// </summary>
    KeyOnly,

    /// <summary>An account holding something, which cannot be closed yet.</summary>
    Funded,

    /// <summary>An account that can be closed now.</summary>
    Closable,

    /// <summary>An account this application deleted, and where its remainder went.</summary>
    Closed,
}

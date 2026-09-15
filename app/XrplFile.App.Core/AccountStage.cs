namespace XrplFile.App.Core;

/// <summary>Where a throwaway account is in its life (design 4.1).</summary>
public enum AccountStage
{
    /// <summary>
    /// A key, and no account. On the XRP Ledger an account exists once it holds its reserve, so
    /// until somebody funds the address there is nothing on the ledger at all.
    /// </summary>
    KeyOnly,

    /// <summary>An account holding something, which cannot be closed yet.</summary>
    Funded,

    /// <summary>An account that can be closed now.</summary>
    Closable,

    /// <summary>An account this application deleted, and where its remainder went.</summary>
    Closed,
}

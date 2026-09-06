using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xrpl.Wallet;

namespace XrplFile.Tests;

/// <summary>
/// A sink that validates everything on the spot into a FakeLedgerSource, one hundred
/// transactions per ledger, so the writer's read-back has something to read.
/// </summary>
public sealed class FakeLedgerSink(FakeLedgerSource ledger, long firstLedger = 5000) : ILedgerSink
{
    private readonly List<Dictionary<string, object>> _submitted = [];
    private uint _nextSequence = 10;
    private int _ledgersClosed;

    public IReadOnlyList<Dictionary<string, object>> Submitted => _submitted;

    /// <summary>Which submissions (by position) the node refuses, and with what.</summary>
    public Dictionary<int, string> Refusals { get; } = [];

    /// <summary>Applied to the stored MemoData of the given submission — corrupts the ledger copy without touching what was signed.</summary>
    public Dictionary<int, Func<string, string>> StoredAs { get; } = [];

    /// <summary>
    /// Every call this sink saw, in order: "query" for a validated-sequence call (SequenceAsync
    /// with validated: true), and "chunk" / "list" / "manifest" / "submit" for a submission,
    /// tagged by what its memo marker says it is. Lets a test see whether the writer interleaves
    /// batch validation waits with submissions, rather than only waiting once at the end.
    /// </summary>
    public List<string> Events { get; } = [];

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        // A wait for validation is where a real ledger closes; the fake closes one per query, so
        // the manifest lands in a later ledger than the chunks and lists it describes.
        if (validated)
        {
            _ledgersClosed++;
            Events.Add("query");
        }

        return Task.FromResult(_nextSequence);
    }

    public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, XrplWallet wallet, CancellationToken cancellationToken)
    {
        int position = _submitted.Count;
        _submitted.Add(transaction);

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"fake:{position}")));

        if (Refusals.TryGetValue(position, out string? refusal))
        {
            Events.Add("submit");
            return Task.FromResult(new SubmitOutcome(refusal, hash));
        }

        _nextSequence = Convert.ToUInt32(transaction["Sequence"]) + 1;

        string? memoType = null, memoData = null, memoFormat = null;
        if (transaction.TryGetValue("Memos", out object? memos))
        {
            JsonElement first = JsonSerializer.SerializeToElement(memos)[0].GetProperty("Memo");
            memoType = first.TryGetProperty("MemoType", out JsonElement t) ? t.GetString() : null;
            memoData = first.TryGetProperty("MemoData", out JsonElement d) ? d.GetString() : null;
            memoFormat = first.TryGetProperty("MemoFormat", out JsonElement f) ? f.GetString() : null;
        }

        if (memoData is not null && StoredAs.TryGetValue(position, out Func<string, string>? corrupt)) memoData = corrupt(memoData);

        Events.Add(
            MemoLayout.ChunkIndexOf(memoType) is not null ? "chunk"
            : MemoLayout.ListIndexOf(memoType) is not null ? "list"
            : MemoLayout.IsManifest(memoType) ? "manifest"
            : "submit");

        ledger.Add(new LedgerTransaction(
            hash, firstLedger + position / 100 + _ledgersClosed, (string)transaction["Account"], Succeeded: true, Validated: true,
            memoType, memoData, memoFormat));

        return Task.FromResult(new SubmitOutcome("tesSUCCESS", hash));
    }
}

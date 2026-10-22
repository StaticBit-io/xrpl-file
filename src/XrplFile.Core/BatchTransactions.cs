using System.Globalization;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;

namespace XrplFile;

/// <summary>
/// The transactions of the Batch path (spec 4.2): an inner <c>AccountSet</c> of the writer per memo,
/// the payer's outer <c>Batch</c> that carries them, and the writer's plain <c>AccountSet</c> for a
/// round's one memo.
/// </summary>
internal static class BatchTransactions
{
    /// <summary>rippled's <c>tfInnerBatchTxn</c>: every inner carries it.</summary>
    public const uint InnerBatchFlag = 0x40000000;

    /// <summary>rippled's <c>tfIndependent</c> (spec B2).</summary>
    public const uint IndependentFlag = 0x00080000;

    public static Dictionary<string, object> Inner(string writer, uint sequence, MemoSubmission memo) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = writer,
        ["Sequence"] = sequence,
        ["Fee"] = "0",
        ["SigningPubKey"] = string.Empty,
        ["Flags"] = InnerBatchFlag,
        ["Memos"] = Memos(memo),
    };

    public static Dictionary<string, object> Outer(
        string payer, string payerPublicKey, uint sequence, long feeDrops, uint lastLedger,
        IReadOnlyList<Dictionary<string, object>> inners) => new()
    {
        ["TransactionType"] = "Batch",
        ["Account"] = payer,
        ["Sequence"] = sequence,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["Flags"] = IndependentFlag,
        ["LastLedgerSequence"] = lastLedger,
        ["SigningPubKey"] = payerPublicKey,
        ["RawTransactions"] = inners.Select(inner => (object)new Dictionary<string, object> { ["RawTransaction"] = inner }).ToList(),
    };

    public static Dictionary<string, object> Solo(
        string writer, string writerPublicKey, uint sequence, long feeDrops, uint lastLedger, MemoSubmission memo) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = writer,
        ["Sequence"] = sequence,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["LastLedgerSequence"] = lastLedger,
        ["SigningPubKey"] = writerPublicKey,
        ["Memos"] = Memos(memo),
    };

    /// <summary>
    /// The writer signs first, then the payer (spec 4.2). The SDK routes a <c>Batch</c> to its
    /// batch-part signature when the signing account owns an inner and not the outer, and that
    /// signature adds the writer's <c>BatchSigner</c>. The payer then signs exactly what the writer
    /// returned, decoded from its blob, so the inners it commits to are the inners the writer's
    /// signature covers.
    /// </summary>
    public static async Task<SignedTransaction> SignAsync(
        Dictionary<string, object> outer, ITransactionSigner writer, ITransactionSigner payer, CancellationToken cancellationToken)
    {
        SignedTransaction part = await writer.SignAsync(outer, cancellationToken).ConfigureAwait(false);
        JsonObject decoded = XrplBinaryCodec.Decode(part.TxBlob).AsObject();

        if (!CarriesBatchSignature(decoded, writer.Account))
        {
            throw new InvalidOperationException(
                $"the writer's signer returned a Batch without a signed BatchSigner for {writer.Account}");
        }

        return await payer.SignAsync((Dictionary<string, object>)Plain(decoded), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A decoded blob as the plain objects the SDK's signer serializes: dictionaries, lists,
    /// strings, and numbers as <see cref="uint"/> where they fit and <see cref="long"/> otherwise.
    /// </summary>
    public static object Plain(JsonNode? node) => node switch
    {
        JsonObject json => json.ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.Ordinal),
        JsonArray array => array.Select(Plain).ToList(),
        JsonValue value when value.TryGetValue(out string? text) => text,
        JsonValue value => Number(value.ToJsonString()),
        null => throw new FormatException("a decoded transaction carried a null field"),
        _ => throw new FormatException($"a decoded transaction carried {node.GetType().Name}"),
    };

    private static object Number(string text)
    {
        long number = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return number is >= 0 and <= uint.MaxValue ? (object)(uint)number : number;
    }

    private static bool CarriesBatchSignature(JsonObject decoded, string writer) =>
        decoded["BatchSigners"] is JsonArray signers
        && signers.Any(entry => entry?["BatchSigner"] is JsonObject signer
            && string.Equals((string?)signer["Account"], writer, StringComparison.Ordinal)
            && !string.IsNullOrEmpty((string?)signer["SigningPubKey"])
            && !string.IsNullOrEmpty((string?)signer["TxnSignature"]));

    private static List<object> Memos(MemoSubmission memo)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        return [new Dictionary<string, object> { ["Memo"] = fields }];
    }
}

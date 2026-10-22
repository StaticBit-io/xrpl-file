using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class BatchTransactionsTests
{
    private static readonly XrplWallet WriterWallet = XrplWallet.Generate();
    private static readonly XrplWallet PayerWallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Writer = new WalletSigner(WriterWallet);
    private static readonly ITransactionSigner Payer = new WalletSigner(PayerWallet);

    private static MemoSubmission Memo(int index) =>
        new(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex(VectorStream.Bytes("batch", 1000)), null);

    private static Dictionary<string, object> Outer(int inners) =>
        BatchTransactions.Outer(
            Payer.Account, Payer.PublicKey, sequence: 7, feeDrops: (inners + 3) * 15L, lastLedger: 1020,
            [.. Enumerable.Range(0, inners).Select(i => BatchTransactions.Inner(Writer.Account, 5u + (uint)i, Memo(i)))]);

    [Fact]
    public void An_inner_is_an_AccountSet_of_the_writer_with_no_fee_no_key_and_the_inner_flag()
    {
        Dictionary<string, object> inner = BatchTransactions.Inner(Writer.Account, 5, Memo(0));

        Assert.Equal("AccountSet", inner["TransactionType"]);
        Assert.Equal(Writer.Account, inner["Account"]);
        Assert.Equal(5u, inner["Sequence"]);
        Assert.Equal("0", inner["Fee"]);
        Assert.Equal(string.Empty, inner["SigningPubKey"]);
        Assert.Equal(0x40000000u, inner["Flags"]);
        Assert.False(inner.ContainsKey("LastLedgerSequence"));
    }

    [Fact]
    public void An_outer_is_the_payers_independent_batch_with_its_deadline_and_no_batch_signers_yet()
    {
        Dictionary<string, object> outer = Outer(8);

        Assert.Equal("Batch", outer["TransactionType"]);
        Assert.Equal(Payer.Account, outer["Account"]);
        Assert.Equal(7u, outer["Sequence"]);
        Assert.Equal("165", outer["Fee"]);
        Assert.Equal(0x00080000u, outer["Flags"]);
        Assert.Equal(1020u, outer["LastLedgerSequence"]);
        Assert.Equal(Payer.PublicKey, outer["SigningPubKey"]);
        Assert.Equal(8, ((List<object>)outer["RawTransactions"]).Count);
        Assert.False(outer.ContainsKey("BatchSigners"));
    }

    [Fact]
    public async Task The_writer_signs_first_and_the_payer_signs_what_the_writer_returned()
    {
        SignedTransaction signed = await BatchTransactions.SignAsync(Outer(2), Writer, Payer, TestContext.Current.CancellationToken);

        JsonObject decoded = XrplBinaryCodec.Decode(signed.TxBlob).AsObject();
        JsonObject signer = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(decoded["BatchSigners"]))!["BatchSigner"]);

        Assert.Equal(Writer.Account, (string?)signer["Account"]);
        Assert.Equal(Writer.PublicKey, (string?)signer["SigningPubKey"], ignoreCase: true);
        Assert.False(string.IsNullOrEmpty((string?)signer["TxnSignature"]));
        Assert.Equal(Payer.PublicKey, (string?)decoded["SigningPubKey"], ignoreCase: true);
        Assert.False(string.IsNullOrEmpty((string?)decoded["TxnSignature"]));
        Assert.Equal(64, signed.Hash.Length);
    }

    [Fact]
    public void A_solo_memo_is_a_plain_AccountSet_of_the_writer_with_its_own_deadline()
    {
        Dictionary<string, object> solo = BatchTransactions.Solo(Writer.Account, Writer.PublicKey, 9, 15, 1020, Memo(0));

        Assert.Equal("AccountSet", solo["TransactionType"]);
        Assert.Equal(9u, solo["Sequence"]);
        Assert.Equal("15", solo["Fee"]);
        Assert.Equal(1020u, solo["LastLedgerSequence"]);
        Assert.Equal(Writer.PublicKey, solo["SigningPubKey"]);
        Assert.False(solo.ContainsKey("Flags"));
    }

    [Fact]
    public void Plain_turns_decoded_json_into_dictionaries_lists_strings_and_numbers()
    {
        JsonNode node = JsonNode.Parse("""{"A":"x","B":7,"C":[{"D":4294967295}],"E":5000000000}""")!;

        Dictionary<string, object> plain = Assert.IsType<Dictionary<string, object>>(BatchTransactions.Plain(node));

        Assert.Equal("x", plain["A"]);
        Assert.Equal(7u, plain["B"]);
        Assert.Equal(4294967295u, ((Dictionary<string, object>)((List<object>)plain["C"])[0])["D"]);
        Assert.Equal(5000000000L, plain["E"]);
    }
}

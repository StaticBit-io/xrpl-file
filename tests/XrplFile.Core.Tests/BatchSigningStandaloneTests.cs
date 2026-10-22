using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Spec 4.2, proven before anything else is built: two vault signers on one vault sign a Batch the
/// stand applies, and the inners land as AccountSets of the writer. Also measures what signing a
/// megabyte's batches costs (spec 4.2's last paragraph).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class BatchSigningStandaloneTests : IDisposable
{
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
    private static readonly ReadOnlyMemory<byte> Password = Encoding.UTF8.GetBytes("open sesame");

    /// <summary>One fifth of the 4.1 s the spike measured for 1 MB on this stand, over its 125 batches.</summary>
    private static readonly TimeSpan SigningBudgetPerBatch = TimeSpan.FromMilliseconds(6.5);

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-batch-signing-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    private static MemoSubmission Memo(int index) =>
        new(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex(VectorStream.Bytes($"signing-{index}", 1000)), null);

    private async Task<(KeyVault Vault, VaultSigner Writer, VaultSigner Payer)> TwoSignersAsync(
        IXrplClient client, XrplClientLedgerSink sink, CancellationToken token, bool cacheWallet = false)
    {
        XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
        XrplWallet payer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);

        KeyVault vault = new(Path.Combine(_directory, "vault.json"), new Argon2idKdf(), new NoDeviceSecretStore(), FastParameters);
        await vault.CreateAsync(Password, token);

        string writerReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "writer", "1", writer.ClassicAddress, writer.Seed), token);
        string payerReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "payer", "1", payer.ClassicAddress, payer.Seed), token);

        return (vault,
            await VaultSigner.CreateAsync(vault, writerReference, token, cacheWallet: cacheWallet),
            await VaultSigner.CreateAsync(vault, payerReference, token, cacheWallet: cacheWallet));
    }

    [Fact]
    public async Task The_writers_vault_signature_over_an_outer_carries_its_batch_signer()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            (KeyVault vault, VaultSigner writer, VaultSigner payer) = await TwoSignersAsync(client, new XrplClientLedgerSink(client), token);
            await using (vault)
            {
                Dictionary<string, object> outer = BatchTransactions.Outer(
                    payer.Account, payer.PublicKey, sequence: 7, feeDrops: 75, lastLedger: 1000,
                    [BatchTransactions.Inner(writer.Account, 5, Memo(0)), BatchTransactions.Inner(writer.Account, 6, Memo(1))]);

                SignedTransaction part = await writer.SignAsync(outer, token);

                JsonObject decoded = XrplBinaryCodec.Decode(part.TxBlob).AsObject();
                JsonObject signer = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(decoded["BatchSigners"]))!["BatchSigner"]);

                Assert.Equal(writer.Account, (string?)signer["Account"]);
                Assert.Equal(writer.PublicKey, (string?)signer["SigningPubKey"], ignoreCase: true);
                Assert.False(string.IsNullOrEmpty((string?)signer["TxnSignature"]));
                Assert.Null(decoded["TxnSignature"]);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task Two_vault_signers_on_one_vault_sign_a_batch_the_stand_applies()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            (KeyVault vault, VaultSigner writer, VaultSigner payer) = await TwoSignersAsync(client, sink, token);
            await using (vault)
            {
                uint writerSequence = await sink.SequenceAsync(writer.Account, validated: false, token);
                uint payerSequence = await sink.SequenceAsync(payer.Account, validated: false, token);
                long ledger = await StandaloneFixture.CurrentLedgerAsync(client, token);

                List<MemoSubmission> memos = [.. Enumerable.Range(0, 8).Select(Memo)];
                Dictionary<string, object> outer = BatchTransactions.Outer(
                    payer.Account, payer.PublicKey, payerSequence, feeDrops: 11 * 15, lastLedger: (uint)(ledger + 20),
                    [.. memos.Select((memo, i) => BatchTransactions.Inner(writer.Account, writerSequence + (uint)i, memo))]);

                SignedTransaction signed = await BatchTransactions.SignAsync(outer, writer, payer, token);

                XrplResponse<object> response = await client.GRequest<object, SubmitRequest>(new SubmitRequest { TxBlob = signed.TxBlob }, token);
                Assert.Equal("tesSUCCESS", response.Raw.ToJsonElement().GetProperty("engine_result").GetString());

                while (await sink.SequenceAsync(writer.Account, validated: true, token) < writerSequence + 8)
                {
                    await StandaloneFixture.CloseLedgersUntilAsync(client, await StandaloneFixture.CurrentLedgerAsync(client, token) + 1, token);
                }

                List<JsonElement> records = await AccountTransactionsAsync(client, writer.Account, token);

                foreach (MemoSubmission memo in memos)
                {
                    JsonElement record = Assert.Single(records, candidate => MemoTypeOf(candidate) == memo.MemoTypeHex);
                    JsonElement tx = TxOf(record);
                    JsonElement meta = record.GetProperty("meta");

                    Assert.Equal("AccountSet", tx.GetProperty("TransactionType").GetString());
                    Assert.Equal(writer.Account, tx.GetProperty("Account").GetString());
                    Assert.Equal("0", tx.GetProperty("Fee").GetString());
                    Assert.Equal("tesSUCCESS", meta.GetProperty("TransactionResult").GetString());
                    Assert.Equal(memo.MemoDataHex, MemoDataOf(record), ignoreCase: true);
                    Assert.Equal(signed.Hash, ParentBatchOf(record), ignoreCase: true);
                }
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task Signing_a_megabytes_batches_through_two_vault_signers_is_measured()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            (KeyVault vault, VaultSigner writer, VaultSigner payer) = await TwoSignersAsync(client, new XrplClientLedgerSink(client), token, cacheWallet: true);
            await using (vault)
            {
                const int Batches = 125;

                // The first pass pays the JIT for the codec and the signature code (about twice the
                // steady cost on this stand); the second is what a long run signs at.
                foreach (string pass in new[] { "cold", "warm" })
                {
                    Stopwatch watch = Stopwatch.StartNew();

                    for (int batch = 0; batch < Batches; batch++)
                    {
                        Dictionary<string, object> outer = BatchTransactions.Outer(
                            payer.Account, payer.PublicKey, (uint)(100 + batch), 11 * 15, 5000,
                            [.. Enumerable.Range(0, 8).Select(i => BatchTransactions.Inner(writer.Account, (uint)(1000 + batch * 8 + i), Memo(batch * 8 + i)))]);

                        await BatchTransactions.SignAsync(outer, writer, payer, token);
                    }

                    watch.Stop();
                    TimeSpan perBatch = watch.Elapsed / Batches;

                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"signing ({pass}): {Batches} batches in {watch.Elapsed.TotalMilliseconds:F0} ms, {perBatch.TotalMilliseconds:F2} ms per batch; "
                        + $"budget {SigningBudgetPerBatch.TotalMilliseconds} ms per batch");
                }
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    private static async Task<List<JsonElement>> AccountTransactionsAsync(IXrplClient client, string account, CancellationToken token)
    {
        XrplResponse<object> page = await client.GRequest<object, AccountTransactionsRequest>(
            new AccountTransactionsRequest(account) { Limit = 100 }, token);

        return [.. page.Raw.ToJsonElement().GetProperty("transactions").EnumerateArray().Select(record => record.Clone())];
    }

    private static JsonElement TxOf(JsonElement record) =>
        record.TryGetProperty("tx_json", out JsonElement tx) ? tx : record.GetProperty("tx");

    private static string? MemoTypeOf(JsonElement record) =>
        TxOf(record).TryGetProperty("Memos", out JsonElement memos)
            ? memos[0].GetProperty("Memo").GetProperty("MemoType").GetString()?.ToUpperInvariant()
            : null;

    private static string? MemoDataOf(JsonElement record) =>
        TxOf(record).GetProperty("Memos")[0].GetProperty("Memo").GetProperty("MemoData").GetString();

    private static string? ParentBatchOf(JsonElement record) =>
        record.GetProperty("meta").TryGetProperty("ParentBatchID", out JsonElement parent) ? parent.GetString()
        : TxOf(record).TryGetProperty("ParentBatchID", out JsonElement inTx) ? inTx.GetString()
        : null;
}

using System.Text;
using Xrpl.Client;
using Xrpl.Wallet;
using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// The seam the whole key vault was built for, against a real node.
///
/// The first test says a key that is only ever handed out one signature at a time can still put a
/// file into the ledger and get it back byte for byte. The second is the one that justifies the
/// design: the vault is locked in the middle of a run, the run dies where it stands, and the
/// resume finishes the file without paying for a single chunk twice.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class VaultPublicationStandaloneTests : IDisposable
{
    /// <summary>
    /// Argon2id at the smallest cost the implementation accepts. What is under test here is a
    /// publication, not a KDF, and the real cost — half a second per unlock — would be spent
    /// twice in a test whose point is elsewhere. <c>Argon2idKdfTests</c> is where cost belongs.
    /// </summary>
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);

    private static readonly ReadOnlyMemory<byte> Password = Encoding.UTF8.GetBytes("open sesame");

    private readonly string _directory =
        Directory.CreateTempSubdirectory("xrpl-file-vault-publication-").FullName;

    private string StateDirectory => Path.Combine(_directory, "state");

    private string VaultPath => Path.Combine(_directory, "vault.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>
    /// A funded account whose seed is put into a fresh vault, and a signer over the entry. The
    /// wallet comes back too, because a test still has to fund it — but from here on the signer is
    /// the only way a transaction of that account gets signed.
    /// </summary>
    private async Task<(KeyVault Vault, XrplWallet Wallet, VaultSigner Signer)> VaultBackedAccountAsync(
        IXrplClient client, XrplClientLedgerSink sink, CancellationToken cancellationToken)
    {
        XrplWallet wallet = await StandaloneFixture.FundedAccountAsync(client, sink, 700_000_000, cancellationToken);

        KeyVault vault = new(VaultPath, new Argon2idKdf(), new NoDeviceSecretStore(), FastParameters);
        await vault.CreateAsync(Password, cancellationToken);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "publisher", "1", wallet.ClassicAddress, wallet.Seed),
            cancellationToken);

        VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, cancellationToken);

        // The signer found the account by itself, from the entry alone.
        Assert.Equal(wallet.ClassicAddress, signer.Account);
        Assert.Equal(wallet.PublicKey, signer.PublicKey);

        return (vault, wallet, signer);
    }

    /// <summary>
    /// The chunk index of every validated chunk transaction this account carries from
    /// <paramref name="fromLedger"/> on, in ledger order and with duplicates kept.
    ///
    /// Duplicates are the point. A resume that re-sent what was already in the ledger would still
    /// read back perfectly — the reader takes the first copy of a chunk in canonical order — while
    /// having paid for every one of those chunks twice. The only place that shows is the count.
    /// </summary>
    private static async Task<IReadOnlyList<long>> ChunkTransactionsAsync(
        ILedgerSource source, string account, long fromLedger, CancellationToken cancellationToken)
    {
        long to = await source.CurrentLedgerAsync(cancellationToken);
        List<long> indices = [];

        await foreach (LedgerTransaction tx in source
            .AccountTransactionsAsync(account, fromLedger, to, forward: true, cancellationToken))
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } index) indices.Add(index);
        }

        return indices;
    }

    /// <summary>
    /// Polls until at least <paramref name="count"/> chunk transactions have validated. The
    /// caller's token is the only deadline; the interrupted run submitted its chunks and then
    /// died before anything waited for them, so this is where that wait happens instead.
    /// </summary>
    private static async Task<IReadOnlyList<long>> WaitForChunkTransactionsAsync(
        ILedgerSource source, string account, long fromLedger, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<long> landed = await ChunkTransactionsAsync(source, account, fromLedger, cancellationToken);

            if (landed.Count >= count) return landed;

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    [Fact]
    public async Task A_file_is_published_by_a_key_the_vault_holds_and_reads_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            (KeyVault vault, XrplWallet wallet, VaultSigner signer) = await VaultBackedAccountAsync(client, sink, token);
            await using (vault)
            {
                // Ten chunks in one block: a whole publication — chunks, list, read-back and
                // manifest — inside a handful of ledgers.
                byte[] file = VectorStream.Bytes("vault", 10_000);

                SessionOptions options = new() { StateDirectory = StateDirectory };

                PublicationEstimate priced = await PublicationSession.EstimateAsync(
                    source, signer.Account, file.Length, options, token);
                SequenceStrategyOptions strategyOptions = new() { FeeDrops = (int)priced.FeeDropsPerTransaction };

                await using SequenceStrategy strategy = new(sink, signer, strategyOptions);

                PublicationSession session = await PublicationSession.StartAsync(
                    strategy, source, file, "application/octet-stream", "vaulted.bin",
                    signer.Account, "key-vault", "1", options, token);

                PublicationReport report = await session.RunAsync(null, token);

                // The proof is the read: a stranger's reader gets the file back byte for byte,
                // from an account whose seed was handed out one signature at a time.
                ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);

                Assert.Equal(file, read.Bytes);
                Assert.Equal(wallet.ClassicAddress, read.Writer);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task Locking_the_vault_mid_publication_fails_the_run_and_a_resume_finishes_it()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            (KeyVault vault, XrplWallet wallet, VaultSigner signer) = await VaultBackedAccountAsync(client, sink, token);
            await using (vault)
            {
                byte[] file = VectorStream.Bytes("vault-locked", 10_000);
                IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
                Assert.Equal(10, chunks.Count);

                SessionOptions options = new() { StateDirectory = StateDirectory };

                PublicationEstimate priced = await PublicationSession.EstimateAsync(
                    source, signer.Account, file.Length, options, token);
                SequenceStrategyOptions strategyOptions = new() { FeeDrops = (int)priced.FeeDropsPerTransaction };

                const int LockAfter = 4;

                // The lock is driven by the number of signatures taken, not by a clock. A timer
                // would be a race against the node, and a lock that fires after the last chunk
                // proves nothing at all — it would leave a run that only needed its manifest.
                LockingSigner interrupted = new(signer, vault, LockAfter);

                string statePath;
                long startedIn;

                await using (SequenceStrategy first = new(sink, interrupted, strategyOptions))
                {
                    PublicationSession session = await PublicationSession.StartAsync(
                        first, source, file, "application/octet-stream", "locked.bin",
                        signer.Account, "key-vault", "1", options, token);

                    statePath = session.StatePath;
                    startedIn = session.State.StartedInLedger;

                    VaultException refused = await Assert.ThrowsAsync<VaultException>(
                        () => session.RunAsync(null, token));

                    // Not a transport failure dressed up as one: the run stopped because the user
                    // put the key away, and the error says exactly that.
                    Assert.Equal(VaultError.Locked, refused.Error);
                }

                Assert.False(vault.IsUnlocked);
                Assert.Equal(LockAfter + 1, interrupted.Signatures);

                // Partway, and provably so: the chunks signed before the lock reach the ledger,
                // and the rest were never signed. Between zero and all of them is the only state
                // in which a resume has anything to prove.
                IReadOnlyList<long> beforeResume =
                    await WaitForChunkTransactionsAsync(source, signer.Account, startedIn, LockAfter, token);

                Assert.Equal(LockAfter, beforeResume.Count);
                Assert.Equal<long>([0, 1, 2, 3], beforeResume);

                // What a user does next: type the password again.
                Assert.Equal(VaultUnlockResult.Success, await vault.UnlockAsync(Password, token));

                await using SequenceStrategy second = new(sink, signer, strategyOptions);

                PublicationSession resumed = await PublicationSession.ResumeAsync(
                    second, source, file, statePath, options, token);
                PublicationReport report = await resumed.RunAsync(null, token);

                ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);

                Assert.Equal(file, read.Bytes);
                Assert.Equal(wallet.ClassicAddress, read.Writer);

                // And the money. One transaction per chunk and no more: the four that landed
                // before the lock were adopted from the ledger rather than sent again. A resume
                // that re-sent everything would pass the read above and have paid twice, which is
                // the failure this assertion exists for and the read cannot see.
                IReadOnlyList<long> afterResume =
                    await ChunkTransactionsAsync(source, signer.Account, startedIn, token);

                Assert.Equal(chunks.Count, afterResume.Count);
                Assert.Equal(chunks.Count, afterResume.Distinct().Count());
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }
}

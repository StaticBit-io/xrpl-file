using Xunit;

namespace XrplFile.Tests;

public class BatchPublicationEstimateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FakeLedgerSource Node(long writerBalance = 100_000_000, long payerBalance = 100_000_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), OpenLedgerFee = new OpenLedgerFee(10, 10) };
        source.Balances["rWriter"] = writerBalance;
        source.Balances["rPayer"] = payerBalance;
        return source;
    }

    [Fact]
    public async Task The_seven_megabyte_file_of_the_testnet_run_is_priced_as_the_spec_says()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 7_123_692, Token);

        Assert.Equal(7124, estimate.Chunks);
        Assert.Equal(7, estimate.Blocks);
        Assert.Equal(1, estimate.Lists);
        Assert.Equal(7126, estimate.Memos);
        Assert.Equal(891, estimate.Batches);
        Assert.Equal(2, estimate.SoloTransactions);
        Assert.Equal(15, estimate.FeeUnitDrops);
        Assert.Equal(30, estimate.FeeCeilingDrops);
        Assert.Equal((7124 + 3 * 891) * 15L, estimate.PayerFeeDrops);
        Assert.Equal(2 * 15L, estimate.WriterFeeDrops);
        Assert.Equal(53_445 * 30L, estimate.PayerFeeDropsWorstCase);
        Assert.Equal((7126 * 3 + 250) * 30L, estimate.WriterFeeDropsWorstCase);
        Assert.Equal(1_000_000 + 53_445 * 30L, estimate.PayerRequiredDrops);
    }

    [Fact]
    public async Task A_one_chunk_file_is_two_solo_transactions_and_costs_the_payer_nothing()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 500, Token);

        Assert.Equal(0, estimate.Batches);
        Assert.Equal(2, estimate.SoloTransactions);
        Assert.Equal(0, estimate.PayerFeeDrops);
        Assert.Equal(30, estimate.WriterFeeDrops);
    }

    [Fact]
    public async Task Nine_chunks_are_two_batches_and_the_manifest_a_solo()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 9_000, Token);

        Assert.Equal(2, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
        Assert.Equal((9 + 3 * 2) * 15L, estimate.PayerFeeDrops);
    }

    [Fact]
    public async Task The_spec_example_of_a_1_7_GB_file_runs_104_chunk_rounds()
    {
        FakeLedgerSource source = new();

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            source, "rWriter", "rPayer", 1_689_809_534, TestContext.Current.CancellationToken);

        Assert.Equal(1_689_810, estimate.Chunks);
        Assert.Equal(1_651, estimate.Blocks);
        Assert.Equal(166, estimate.Lists);
        Assert.Equal(104 + 1 + 1, estimate.Rounds);
        Assert.Equal(103 * 2_048 + 283 + 21, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
    }

    [Fact]
    public async Task One_memo_past_a_full_round_is_a_solo_as_the_strategy_sends_it()
    {
        FakeLedgerSource source = new();

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            source, "rWriter", "rPayer", 16_385_000, TestContext.Current.CancellationToken);

        Assert.Equal(16_385, estimate.Chunks);
        Assert.Equal(2, estimate.Lists);
        Assert.Equal(2 + 1 + 1, estimate.Rounds);
        Assert.Equal(2_048 + 1, estimate.Batches);
        Assert.Equal(2, estimate.SoloTransactions);
        Assert.Equal((16_384 + 2 + 3 * 2_049) * estimate.FeeUnitDrops, estimate.PayerFeeDrops);
        Assert.Equal(2 * estimate.FeeUnitDrops, estimate.WriterFeeDrops);
    }

    [Fact]
    public async Task A_small_file_is_one_round_per_pass()
    {
        FakeLedgerSource source = new();

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            source, "rWriter", "rPayer", 9_000, TestContext.Current.CancellationToken);

        Assert.Equal(2, estimate.Rounds);
        Assert.Equal(2, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
    }

    [Fact]
    public async Task A_pass_of_exactly_one_round_has_no_solo()
    {
        // 16,384 chunks fill one round and no list is needed, so the only solo is the manifest.
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 16_384, 1, 0, Token);

        Assert.Equal(2_048, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
        Assert.Equal(1 + 1, estimate.Rounds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public async Task The_estimate_counts_the_batches_and_solos_the_strategy_sends(int memos)
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(memos), null, Token);

        int outers = node.Sink.Transmissions.Count(transmission => transmission.TransactionType == "Batch");
        int solos = node.Sink.Transmissions.Count(transmission => transmission.TransactionType != "Batch");

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", memos, 1, 0, Token);

        Assert.Equal(outers, estimate.Batches);
        Assert.Equal(solos + 1, estimate.SoloTransactions);
    }

    [Fact]
    public async Task Owned_objects_lock_reserve_out_of_each_accounts_spendable_balance()
    {
        FakeLedgerSource node = Node(writerBalance: 2_000_000, payerBalance: 3_000_000);
        node.OwnerCounts["rPayer"] = 5;

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(node, "rWriter", "rPayer", 10_000, Token);

        Assert.Equal(2_000_000, estimate.WriterSpendableDrops);
        Assert.Equal(3_000_000 - 5 * 200_000, estimate.PayerSpendableDrops);
        Assert.True(estimate.WriterCovers);
        Assert.True(estimate.PayerCovers);
    }

    [Fact]
    public void Refusals_come_writer_first_then_payer_then_budget()
    {
        BatchPublicationEstimate broke = new(10, 1, 0, 11, 2, 1, 10, 15, 30, 300, 15, 3000, 1000, 1_000_000, 200_000, 0, 0, 0, 0);
        BatchPublicationEstimate payerBroke = broke with { WriterBalanceDrops = 10_000_000 };
        BatchPublicationEstimate rich = payerBroke with { PayerBalanceDrops = 10_000_000 };

        Assert.Equal(XrplFileError.InsufficientBalance, Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireBatch(broke, 1)).Error);
        Assert.Equal(XrplFileError.PayerInsufficientBalance, Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireBatch(payerBroke, 1)).Error);
        Assert.Equal(XrplFileError.BudgetExceeded, Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireBatch(rich, 1)).Error);
        PublicationEstimator.RequireBatch(rich, null);
    }
}

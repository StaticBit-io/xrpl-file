using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests.Localization;

/// <summary>The phrases of the accounts screens whose English changes with a number or carries a value of another kind.</summary>
public sealed class AccountsAreaTextTests
{
    [Theory]
    [InlineData(1, "About 1 ledger remaining")]
    [InlineData(2, "About 2 ledgers remaining")]
    [InlineData(1_234, "About 1,234 ledgers remaining")]
    public void The_ledgers_still_to_wait_agree_with_their_count(long count, string expected)
    {
        Assert.Equal(expected, TestText.En(UiText.Of(CloseText.LedgersRemaining, ("count", count))));
    }

    [Fact]
    public void The_ledgers_still_to_wait_can_carry_the_estimated_time()
    {
        UiText time = LedgerTiming.Describe(LedgerTiming.Estimate(64))!;

        Assert.Equal("About 64 ledgers remaining (about 4 minutes)", TestText.En(UiText.Of(CloseText.LedgersRemainingWithTime, ("count", 64L), ("time", time))));
    }

    [Theory]
    [InlineData(10, "Checked again every 10 seconds.")]
    [InlineData(1, "Checked again every second.")]
    public void The_funding_poll_note_agrees_with_its_interval(long seconds, string expected)
    {
        Assert.Equal(expected, TestText.En(UiText.Of(FundingText.PollNote, ("count", seconds))));
    }

    [Fact]
    public void The_funding_poll_interval_reads_as_it_always_did()
    {
        Assert.Equal("Checked again every 10 seconds.", TestText.En(UiText.Of(FundingText.PollNote, ("count", (long)FundingModel.PollInterval.TotalSeconds))));
    }

    [Fact]
    public void An_amount_in_a_row_reads_as_xrp()
    {
        Assert.Equal("12.5 XRP", TestText.En(UiText.Of(AccountsText.BalanceAmount, ("amount", new XrpAmount(12_500_000)))));
    }

    [Fact]
    public void A_backup_that_was_not_recorded_carries_the_reason_in_the_same_language()
    {
        UiText text = UiText.Of(KeysText.BackupSavedNotRecorded, ("reason", VaultErrorMessages.For(Keys.VaultError.Locked)));

        Assert.Equal("The backup was saved, but the vault could not record that it was: The vault is locked. Unlock it and try again.", TestText.En(text));
    }
}

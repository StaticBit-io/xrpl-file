namespace XrplFile;

/// <summary>
/// The fees a session has committed to, per account, as it signs (spec B26). Signing past the
/// budget the estimate approved is refused: that is the safety net if an assumption behind the bound breaks.
/// </summary>
internal sealed class BatchSpending(BatchBudget budget)
{
    private readonly Lock _gate = new();
    private long _payer;
    private long _writer;

    public void CommitPayer(long drops)
    {
        lock (_gate)
        {
            if (_payer + drops > budget.PayerBudgetDrops)
            {
                throw new XrplFileException(XrplFileError.BudgetExceeded,
                    $"signing this batch would commit the payer to {_payer + drops} drops, past the {budget.PayerBudgetDrops} the estimate approved");
            }

            _payer += drops;
        }
    }

    public void CommitWriter(long drops)
    {
        lock (_gate)
        {
            if (_writer + drops > budget.WriterBudgetDrops)
            {
                throw new XrplFileException(XrplFileError.BudgetExceeded,
                    $"signing this transaction would commit the writer to {_writer + drops} drops, past the {budget.WriterBudgetDrops} the estimate approved");
            }

            _writer += drops;
        }
    }
}

using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
// Xrpl.Models.Ledger also declares a LedgerTransaction; only the enum is wanted from it.
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;

namespace XrplFile;

public sealed class XrplClientLedgerSink(IXrplClient client) : ILedgerSink
{
    private const int MaxSubmitAttempts = 3;

    private readonly IXrplClient _client = client;

    public async Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        AccountInfo info = await _client.AccountInfo(
            new AccountInfoRequest(account)
            {
                LedgerIndex = new LedgerIndex(validated ? LedgerIndexType.Validated : LedgerIndexType.Current),
            },
            cancellationToken).Typed().ConfigureAwait(false);

        return info.AccountData.Sequence ?? 0;
    }

    /// <summary>
    /// Signs once and submits up to three times. A closing ledger holding hundreds of one
    /// account's queued transactions can make the node take longer than the SDK's request
    /// timeout to answer <c>submit</c> — that alone must not fail the publication, because the
    /// transaction may have gone through anyway. On a timeout, the hash is looked up: if the node
    /// already knows it (validated or still open), the submission is treated as accepted; if not,
    /// the identical blob is resubmitted. Resubmission is safe because it is idempotent by
    /// <c>Sequence</c> — a copy that did land answers <c>tefPAST_SEQ</c>/<c>tefALREADY</c>, which is
    /// itself resolved by the same hash lookup rather than reported as a refusal.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, XrplWallet wallet, CancellationToken cancellationToken)
    {
        SignatureResult signed = wallet.Sign(transaction);
        string hash = signed.Hash.ToUpperInvariant();

        // No upper bound in the loop header: the third attempt's TimeoutException is not caught
        // (the `when` guard below is false) and propagates out of the GRequest call itself, so the
        // loop always ends by returning or throwing — never by falling through.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                XrplResponse<object> response = await _client.GRequest<object, SubmitRequest>(
                    new SubmitRequest { TxBlob = signed.TxBlob }, cancellationToken).ConfigureAwait(false);

                JsonElement answer = response.Raw.ToJsonElement();

                string outcome =
                    answer.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
                    : answer.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
                    : answer.GetRawText();

                if (outcome is "tefPAST_SEQ" or "tefALREADY"
                    && await TransactionKnownAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    return new SubmitOutcome("tesSUCCESS", hash);
                }

                return new SubmitOutcome(outcome, hash);
            }
            catch (Xrpl.Client.Exceptions.TimeoutException) when (attempt < MaxSubmitAttempts)
            {
                if (await TransactionKnownAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    return new SubmitOutcome("tesSUCCESS", hash);
                }

                // Not known yet: the node never got it, or it is still being applied. Resubmit the
                // same signed blob on the next loop iteration.
            }
        }
    }

    /// <summary>Whether the node knows this transaction hash, validated or still in the open ledger.</summary>
    private async Task<bool> TransactionKnownAsync(string hash, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            XrplResponse<object> response = await _client.GRequest<object, TxRequest>(new TxRequest(hash), cancellationToken).ConfigureAwait(false);
            answer = response.Raw.ToJsonElement();
        }
        catch (Exception e) when (e is not OperationCanceledException
            && string.Equals(e.Classify().RawError, XrplErrorCodes.TxnNotFound, StringComparison.Ordinal))
        {
            return false;
        }

        if (answer.TryGetProperty("error", out JsonElement error)
            && string.Equals(error.GetString(), XrplErrorCodes.TxnNotFound, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}

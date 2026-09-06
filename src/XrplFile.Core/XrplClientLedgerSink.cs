using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
// Xrpl.Models.Ledger also declares a LedgerTransaction; only the enum is wanted from it.
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;

namespace XrplFile;

public sealed class XrplClientLedgerSink(IXrplClient client) : ILedgerSink
{
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

    public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, XrplWallet wallet, CancellationToken cancellationToken)
    {
        SignatureResult signed = wallet.Sign(transaction);

        XrplResponse<object> response = await _client.GRequest<object, SubmitRequest>(
            new SubmitRequest { TxBlob = signed.TxBlob }, cancellationToken).ConfigureAwait(false);

        JsonElement answer = response.Raw.ToJsonElement();

        string outcome =
            answer.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
            : answer.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
            : answer.GetRawText();

        return new SubmitOutcome(outcome, signed.Hash.ToUpperInvariant());
    }
}

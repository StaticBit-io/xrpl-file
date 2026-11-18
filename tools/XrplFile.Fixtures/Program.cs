using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xrpl.Client;
using Xrpl.Wallet;
using XrplFile;

// Publishes a plan of files to the XRP Ledger through the Batch writer, in one of two modes.
//
// Throwaway mode (three positional arguments) is testnet only: the accounts are faucet-funded
// throwaways and their seeds stay in <out-dir>/accounts.json, which lives under the ignored run/
// folder and is never printed.
//
//   dotnet run --project tools/XrplFile.Fixtures -- tools/fixtures/plan.json run/fixtures/media run/fixtures
//
// Own-accounts mode (--writer and --payer, required on mainnet) publishes from the owner's own two
// accounts on the named network. Their seeds are typed at a hidden prompt, nothing is read from or
// written to accounts.json, and the faucet is never called. The whole plan is priced first; on mainnet
// nothing is submitted until the owner types the confirmation phrase, and each file's session is capped
// at the estimate that was printed for it.
//
//   dotnet run --project tools/XrplFile.Fixtures -- --network mainnet --writer <rAddress> --payer <rAddress> \
//     tools/examples/plan.json run/examples/media run/examples
//
// The plan is validated before the first network call. A run that stops is run again with the same
// arguments: keys already in links.json are skipped and a state file left under <out-dir>/state/<key>
// is resumed through the Batch ResumeAsync.

const string Usage = """
    usage: XrplFile.Fixtures <plan.json> <media-dir> <out-dir>
           XrplFile.Fixtures <plan.json> <media-dir> <out-dir> --network <testnet|mainnet> --writer <address> --payer <address> [--node <wss-url>]
    """;

RunOptions options;

try
{
    options = RunOptions.Parse(args);
}
catch (PlanException e)
{
    foreach (string problem in e.Problems) Console.Error.WriteLine($"arguments: {problem}");
    Console.Error.WriteLine(Usage);
    return 2;
}

using CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    IReadOnlyList<PlanEntry> plan = Plan.Load(options.PlanPath);
    Plan.Validate(plan, options.MediaDirectory);
    if (options.OwnAccounts) Plan.ValidateOwnAccounts(plan);

    await using FixtureRun run = options.OwnAccounts
        ? await FixtureRun.ConnectOwnAsync(plan, options, cancel.Token)
        : await FixtureRun.ConnectAsync(plan, options, cancel.Token);
    return await run.PublishAllAsync(cancel.Token);
}
catch (PlanException e)
{
    foreach (string problem in e.Problems) Console.Error.WriteLine($"plan: {problem}");
    return 1;
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.Error.WriteLine("interrupted; run the same command again to resume");
    return 130;
}
catch (XrplFileException e)
{
    Console.Error.WriteLine($"{e.Error}: {e.Message}");
    return 1;
}
catch (Exception e) when (e is InvalidOperationException or IOException or JsonException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

/// <summary>One file of the plan. <c>Writer</c> is <c>a</c> or <c>b</c>; <c>Cover</c> is the key of an earlier entry or null.</summary>
internal sealed record PlanEntry(string Key, string File, string Type, string Name, string Writer, string? Cover);

internal sealed class PlanException(IReadOnlyList<string> problems) : Exception("the plan is not valid")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

internal static partial class Plan
{
    /// <summary>The format's cover limit (spec 4.1), repeated here so a bad plan fails before a single transaction.</summary>
    private const long CoverMaxBytes = 65_536;

    private const string BrokenCoverSuffix = "-broken-cover";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex KeyPattern();

    public static IReadOnlyList<PlanEntry> Load(string path)
    {
        List<PlanEntry>? entries = JsonSerializer.Deserialize<List<PlanEntry>>(File.ReadAllText(path), Json);
        if (entries is null || entries.Count == 0) throw new PlanException([$"{path} holds no entries"]);

        return entries;
    }

    /// <summary>Everything that can be wrong with a plan without asking the network, all of it at once.</summary>
    public static void Validate(IReadOnlyList<PlanEntry> plan, string mediaDirectory)
    {
        List<string> problems = [];
        Dictionary<string, int> position = new(StringComparer.Ordinal);

        for (int index = 0; index < plan.Count; index++)
        {
            PlanEntry entry = plan[index];

            // The key names a directory under the output folder, so it must not be able to leave it.
            string key = entry.Key ?? "";
            if (!KeyPattern().IsMatch(key)) problems.Add($"entry '{key}': the key must be lower-case letters, digits and hyphens");
            else if (!position.TryAdd(key, index)) problems.Add($"entry '{key}': the key is used twice");
        }

        for (int index = 0; index < plan.Count; index++)
        {
            PlanEntry entry = plan[index];
            string where = $"entry '{entry.Key}'";

            if (entry.Writer is not ("a" or "b")) problems.Add($"{where}: writer is '{entry.Writer}', expected 'a' or 'b'");
            if (string.IsNullOrWhiteSpace(entry.Type)) problems.Add($"{where}: type is empty");
            if (string.IsNullOrWhiteSpace(entry.Name)) problems.Add($"{where}: name is empty");

            if (!FileExists(entry.File, mediaDirectory)) problems.Add($"{where}: file '{entry.File}' is not in {mediaDirectory}");

            if (entry.Cover is null) continue;

            if (!position.TryGetValue(entry.Cover, out int coverIndex) || coverIndex >= index)
            {
                problems.Add($"{where}: cover '{entry.Cover}' is not an earlier key");
                continue;
            }

            PlanEntry cover = plan[coverIndex];

            if (cover.Type is not ("image/webp" or "image/jpeg"))
            {
                problems.Add($"{where}: cover '{cover.Key}' is {cover.Type}, a cover is image/webp or image/jpeg");
            }

            if (FileExists(cover.File, mediaDirectory) && new FileInfo(Path.Combine(mediaDirectory, cover.File)).Length > CoverMaxBytes)
            {
                problems.Add($"{where}: cover '{cover.Key}' is above {CoverMaxBytes} bytes");
            }

            // The spec's "same Account" rule is what a cover other than a broken one must keep.
            if (!string.Equals(cover.Writer, entry.Writer, StringComparison.Ordinal) && !entry.Key.EndsWith(BrokenCoverSuffix, StringComparison.Ordinal))
            {
                problems.Add($"{where}: cover '{cover.Key}' is by writer {cover.Writer}; only a key ending in '{BrokenCoverSuffix}' may take another writer's cover");
            }
        }

        if (problems.Count > 0) throw new PlanException(problems);
    }

    /// <summary>
    /// The own-accounts mode has one writer and one payer, so a plan that needs a second writer, or the
    /// broken-cover fixture that borrows another writer's cover, cannot be published from it.
    /// </summary>
    public static void ValidateOwnAccounts(IReadOnlyList<PlanEntry> plan)
    {
        List<string> problems = [];

        foreach (PlanEntry entry in plan)
        {
            string where = $"entry '{entry.Key}'";

            if (!string.Equals(entry.Writer, "a", StringComparison.Ordinal)) problems.Add($"{where}: writer '{entry.Writer}' is not available with your own accounts, only writer 'a'");
            if (entry.Key.EndsWith(BrokenCoverSuffix, StringComparison.Ordinal)) problems.Add($"{where}: a broken-cover entry cannot be published with your own accounts");
        }

        if (problems.Count > 0) throw new PlanException(problems);
    }

    private static bool FileExists(string? file, string mediaDirectory) =>
        !string.IsNullOrEmpty(file)
        && string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal)
        && File.Exists(Path.Combine(mediaDirectory, file));
}

internal sealed record AccountRecord(string Address, string Seed, bool Funded);

/// <summary>
/// The three throwaway accounts, kept in a file so a stopped run finds them again. The file is written
/// before the faucet is asked: an account the faucet funded and this process then lost the seed of
/// would be money nobody can reach.
/// </summary>
internal sealed class AccountBook
{
    public const string WriterA = "a";
    public const string WriterB = "b";
    public const string Payer = "payer";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, AccountRecord> _records;

    private AccountBook(string path, Dictionary<string, AccountRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static AccountBook LoadOrCreate(string path)
    {
        Dictionary<string, AccountRecord> records = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, AccountRecord>>(File.ReadAllText(path), Json) ?? []
            : [];

        AccountBook book = new(path, records);
        bool created = false;

        foreach (string role in new[] { WriterA, WriterB, Payer })
        {
            if (records.ContainsKey(role)) continue;

            XrplWallet wallet = XrplWallet.Generate();
            records[role] = new AccountRecord(wallet.ClassicAddress, wallet.Seed, Funded: false);
            created = true;
        }

        if (created) book.Save();

        return book;
    }

    public string Address(string role) => _records[role].Address;

    public XrplWallet Wallet(string role)
    {
        AccountRecord record = _records[role];
        XrplWallet wallet = XrplWallet.FromSeed(record.Seed, null, XrplWallet.Ed25519);

        if (!string.Equals(wallet.ClassicAddress, record.Address, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{_path}: the seed of '{role}' does not derive its address");
        }

        return wallet;
    }

    public IEnumerable<string> Unfunded() => _records.Where(pair => !pair.Value.Funded).Select(pair => pair.Key);

    public void MarkFunded(string role)
    {
        _records[role] = _records[role] with { Funded = true };
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

        string temporary = _path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_records, Json) + "\n");
        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>Reports a run's progress to the console no more often than every 20 seconds, and always at a stage's end.</summary>
internal sealed class ThrottledProgress(string key) : IProgress<PublishProgress>
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private long _last;

    public void Report(PublishProgress value)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _last);
        bool stageEnd = value.Total > 0 && value.Done >= value.Total;

        if (!stageEnd && Stopwatch.GetElapsedTime(last, now) < Interval) return;

        // Reports come from several threads; whoever moves the stamp prints, the others stay quiet.
        if (Interlocked.CompareExchange(ref _last, now, last) != last && !stageEnd) return;

        Console.WriteLine($"  {key}: {value.Stage} {value.Done}/{value.Total}");
    }
}

/// <summary>One run of the tool: a connection, the accounts, and the plan published entry by entry.</summary>
internal sealed class FixtureRun : IAsyncDisposable
{
    /// <summary>How often a run tops up a short account before it gives up.</summary>
    private const int MaxFundings = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IReadOnlyList<PlanEntry> _plan;
    private readonly string _mediaDirectory;
    private readonly string _outDirectory;
    private readonly string _network;
    private readonly string _nodeUrl;
    private readonly bool _mainnet;
    private readonly AccountBook? _accounts;
    private readonly OwnKey? _writerKey;
    private readonly OwnKey? _payerKey;
    private readonly XrplClient _client;
    private readonly XrplClientLedgerSink _sink;
    private readonly XrplClientLedgerSource _source;
    private readonly string _linksPath;
    private readonly Dictionary<string, string> _links;

    private FixtureRun(
        IReadOnlyList<PlanEntry> plan, RunOptions options, AccountBook? accounts, OwnKey? writerKey, OwnKey? payerKey, XrplClient client,
        Dictionary<string, string> links)
    {
        _plan = plan;
        _mediaDirectory = options.MediaDirectory;
        _outDirectory = options.OutDirectory;
        _network = options.NetworkName;
        _nodeUrl = options.NodeUrl;
        _mainnet = options.Network == FixtureNetwork.Mainnet;
        _accounts = accounts;
        _writerKey = writerKey;
        _payerKey = payerKey;
        _client = client;
        _sink = new XrplClientLedgerSink(client);
        _source = new XrplClientLedgerSource(client);
        _linksPath = Path.Combine(options.OutDirectory, "links.json");
        _links = links;
    }

    /// <summary>Throwaway mode: connects to the testnet node and loads (or makes and funds) the accounts. The plan is already valid.</summary>
    public static async Task<FixtureRun> ConnectAsync(IReadOnlyList<PlanEntry> plan, RunOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutDirectory);

        Dictionary<string, string> links = LoadLinks(Path.Combine(options.OutDirectory, "links.json"));
        AccountBook accounts = AccountBook.LoadOrCreate(Path.Combine(options.OutDirectory, "accounts.json"));

        XrplClient client = ClientKeeper.CreateClient(options.NodeUrl);
        await client.Connect(cancellationToken);

        FixtureRun run = new(plan, options, accounts, null, null, client, links);

        foreach (string role in accounts.Unfunded().ToList())
        {
            await run.FundAsync(role, cancellationToken);
        }

        foreach (string role in new[] { AccountBook.WriterA, AccountBook.WriterB, AccountBook.Payer })
        {
            Console.WriteLine($"account {role}: {accounts.Address(role)}");
        }

        return run;
    }

    /// <summary>
    /// Own-accounts mode: the owner's two accounts, on the named network. The links file is checked and the seeds are
    /// read before the first connection, so a wrong network or a refused prompt costs nothing; the signing rights are
    /// checked right after connecting and before anything is priced. No account book is read or written and the faucet
    /// is never reached from this path.
    /// </summary>
    public static async Task<FixtureRun> ConnectOwnAsync(IReadOnlyList<PlanEntry> plan, RunOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutDirectory);

        string linksPath = Path.Combine(options.OutDirectory, "links.json");
        Dictionary<string, string> links = LoadLinks(linksPath);
        Links.RequireNetwork(links, options.NetworkName, linksPath);

        string writerSeed = OwnAccounts.ReadSeed($"seed of the writer {options.WriterAddress}: ", options.Network, Console.In, Console.IsInputRedirected);
        string payerSeed = OwnAccounts.ReadSeed($"seed of the payer {options.PayerAddress}: ", options.Network, Console.In, Console.IsInputRedirected);
        OwnKey writer = OwnAccounts.FromSeed(writerSeed, options.WriterAddress!);
        OwnKey payer = OwnAccounts.FromSeed(payerSeed, options.PayerAddress!);

        XrplClient client = ClientKeeper.CreateClient(options.NodeUrl);
        FixtureRun run = new(plan, options, null, writer, payer, client, links);

        try
        {
            await client.Connect(cancellationToken);
            await OwnAccounts.RequireSigningRightsAsync(client, writer, options.NetworkName, cancellationToken);
            await OwnAccounts.RequireSigningRightsAsync(client, payer, options.NetworkName, cancellationToken);
        }
        catch (Exception)
        {
            await run.DisposeAsync();
            throw;
        }

        Console.WriteLine($"writer {writer.Account}{(writer.IsRegularKey ? " (regular key)" : "")}, payer {payer.Account}{(payer.IsRegularKey ? " (regular key)" : "")} on {options.NetworkName}");
        return run;
    }

    public async ValueTask DisposeAsync()
    {
        ClientKeeper.Retire(_client);

        try
        {
            await _client.Disconnect();
        }
        catch (Exception)
        {
            // The run is over; a socket that is already gone has nothing left to close.
        }
    }

    /// <returns>The process exit code: 0 when every entry of the plan has a link.</returns>
    public async Task<int> PublishAllAsync(CancellationToken cancellationToken)
    {
        Links.RequireNetwork(_links, _network, _linksPath);

        PlanQuote? quote = _writerKey is null ? null : await QuoteAsync(cancellationToken);

        foreach (PlanEntry entry in _plan)
        {
            if (_links.TryGetValue(entry.Key, out string? existing))
            {
                Console.WriteLine($"{entry.Key} -> {existing}");
                continue;
            }

            XrplFileLink link = await PublishAsync(entry, quote, cancellationToken);

            _links[entry.Key] = link.ToString();
            SaveLinks();
            Console.WriteLine($"{entry.Key} -> {link}");
        }

        Console.WriteLine($"{_links.Count} of {_plan.Count} files have links, in {_linksPath}");
        return _links.Count == _plan.Count ? 0 : 1;
    }

    /// <summary>
    /// Own-accounts mode: prices every entry still missing from links.json, prints the whole plan's quote, refuses a plan
    /// the accounts cannot carry, and on mainnet waits for the confirmation phrase. Returns null when nothing is left to
    /// publish. Nothing is submitted before this returns.
    /// </summary>
    private async Task<PlanQuote?> QuoteAsync(CancellationToken cancellationToken)
    {
        List<EntryQuote> entries = [];

        foreach (PlanEntry entry in _plan)
        {
            if (_links.ContainsKey(entry.Key)) continue;

            string path = Path.GetFullPath(Path.Combine(_mediaDirectory, entry.File));
            await using FilePublicationSource file = FilePublicationSource.Open(path);

            // A cover published by this same run has no hash yet; the estimate only needs a manifest of the right size.
            string? cover = entry.Cover is null ? null : _links.TryGetValue(entry.Cover, out string? link) ? XrplFileLink.Parse(link).TxHash : new string('0', 64);
            SessionOptions options = new() { StateDirectory = Path.Combine(_outDirectory, "state", entry.Key), Cover = cover, SourcePath = path };

            BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(
                _source, _writerKey!.Account, _payerKey!.Account, file.Length, options, cancellationToken);

            bool resuming = FindState(options.StateDirectory, entry.Key, _network).Unfinished is not null;
            entries.Add(new EntryQuote(entry.Key, file.Length, estimate, resuming));
        }

        if (entries.Count == 0) return null;

        PlanQuote quote = new(entries);
        Console.WriteLine(PlanQuote.Format(quote, _writerKey!.Account, _payerKey!.Account, _network));

        if (!quote.WriterCovers || !quote.PayerCovers)
        {
            throw new InvalidOperationException(
                $"the plan is not covered: the writer needs {quote.WriterRequiredDrops} drops and the payer {quote.PayerRequiredDrops}; nothing was submitted");
        }

        if (_mainnet)
        {
            Console.Error.Write($"type '{PlanQuote.ConfirmationPhrase}' to submit: ");

            if (!PlanQuote.IsConfirmed(Console.In.ReadLine()))
            {
                throw new InvalidOperationException("not confirmed; nothing was submitted");
            }
        }

        return quote;
    }

    private string WriterAddress(PlanEntry entry) => _writerKey?.Account ?? _accounts!.Address(entry.Writer);

    private string PayerAddress => _payerKey?.Account ?? _accounts!.Address(AccountBook.Payer);

    private XrplWallet WriterWallet(PlanEntry entry) => _writerKey?.Wallet ?? _accounts!.Wallet(entry.Writer);

    private XrplWallet PayerWallet => _payerKey?.Wallet ?? _accounts!.Wallet(AccountBook.Payer);

    private async Task<XrplFileLink> PublishAsync(PlanEntry entry, PlanQuote? quote, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(Path.Combine(_mediaDirectory, entry.File));
        string stateDirectory = Path.Combine(_outDirectory, "state", entry.Key);
        string? cover = entry.Cover is null ? null : XrplFileLink.Parse(_links[entry.Cover]).TxHash;

        (string? unfinished, XrplFileLink? finished) = FindState(stateDirectory, entry.Key, _network);

        await using FilePublicationSource file = FilePublicationSource.Open(path);
        SessionOptions options = quote is null
            ? new() { StateDirectory = stateDirectory, Cover = cover, SourcePath = path }
            : new()
            {
                StateDirectory = stateDirectory,
                Cover = cover,
                SourcePath = path,
                Approval = quote.ApprovalFor(entry.Key, WriterAddress(entry), PayerAddress),
                ApproveResume = (resume, _) => Task.FromResult(quote.ResumeApprovalFor(entry.Key, resume, WriterAddress(entry), PayerAddress)),
            };

        XrplFileLink link;

        if (finished is not null)
        {
            Console.WriteLine($"  {entry.Key}: the state file says it finished; checking the ledger");
            link = finished;
        }
        else
        {
            if (_accounts is not null) await EnsureFundedAsync(entry, file.Length, options, cancellationToken);
            link = await RunSessionAsync(entry, file, options, unfinished, cover, cancellationToken);
        }

        await VerifyAsync(entry, path, file.Length, cover, link, cancellationToken);

        return link;
    }

    private async Task<XrplFileLink> RunSessionAsync(
        PlanEntry entry, FilePublicationSource file, SessionOptions options, string? unfinished, string? cover, CancellationToken cancellationToken)
    {
        ITransactionSigner writer = new WalletSigner(WriterWallet(entry));
        ITransactionSigner payer = new WalletSigner(PayerWallet);
        List<BatchStrategy> built = [];

        BatchStrategyFactory factory = (budget, fence) =>
        {
            BatchStrategy strategy = new(_sink, _source, writer, payer, budget, fence);
            built.Add(strategy);
            return strategy;
        };

        try
        {
            PublicationSession session;

            if (unfinished is null)
            {
                Console.WriteLine($"  {entry.Key}: starting, {file.Length} bytes as {entry.Type}, writer {entry.Writer}{(cover is null ? "" : ", cover " + entry.Cover)}");
                session = await PublicationSession.StartAsync(
                    factory, _source, file, entry.Type, entry.Name, writer.Account, $"writer-{entry.Writer}",
                    payer.Account, "payer", _network, options, null, cancellationToken);
            }
            else
            {
                PublicationState state = PublicationState.Load(unfinished);
                RequireSameRun(entry, state, writer.Account, cover);

                Console.WriteLine($"  {entry.Key}: resuming {Path.GetFileName(unfinished)}");
                session = await PublicationSession.ResumeAsync(
                    factory, _source, file, unfinished, payer.Account, "payer", options, null, cancellationToken);
            }

            PublicationReport report = await session.RunAsync(new ThrottledProgress(entry.Key), cancellationToken);

            return report.Link;
        }
        finally
        {
            foreach (BatchStrategy strategy in built) await strategy.DisposeAsync();
        }
    }

    /// <summary>
    /// A state file under this key's folder belongs to this entry only if it was started for the same
    /// writer and the same cover; anything else is a leftover of a different plan, and resuming it
    /// would publish the wrong file under this key.
    /// </summary>
    private static void RequireSameRun(PlanEntry entry, PublicationState state, string writer, string? cover)
    {
        if (!string.Equals(state.Writer, writer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"entry '{entry.Key}': its state was started for writer {state.Writer}, the plan now says {writer}; delete the state folder to start again");
        }

        if (!string.Equals(state.Cover, cover, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"entry '{entry.Key}': its state was started with another cover; delete the state folder to start again");
        }
    }

    /// <summary>
    /// The state file an earlier run left for this key: the one still to resume, or the link of one that
    /// finished but whose link never reached links.json.
    /// </summary>
    private static (string? Unfinished, XrplFileLink? Finished) FindState(string stateDirectory, string key, string network)
    {
        if (!Directory.Exists(stateDirectory)) return (null, null);

        List<string> unfinished = [];
        XrplFileLink? finished = null;

        foreach (string path in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            PublicationState state = PublicationState.Load(path);

            if (!string.Equals(state.Phase, "done", StringComparison.Ordinal)) unfinished.Add(path);
            else if (state.ManifestHash is { } hash) finished = new XrplFileLink(network, hash);
        }

        if (finished is null && unfinished.Count > 1)
        {
            throw new InvalidOperationException($"entry '{key}': {unfinished.Count} unfinished states in {stateDirectory}; keep the right one and delete the others");
        }

        return (unfinished.Count == 1 ? unfinished[0] : null, finished);
    }

    /// <summary>Tops up the writer or the payer when the estimate says it cannot carry the whole file.</summary>
    private async Task EnsureFundedAsync(PlanEntry entry, long length, SessionOptions options, CancellationToken cancellationToken)
    {
        string writer = _accounts!.Address(entry.Writer);
        string payer = _accounts.Address(AccountBook.Payer);

        for (int fundings = 0; ; fundings++)
        {
            BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(_source, writer, payer, length, options, cancellationToken);

            if (estimate.WriterCovers && estimate.PayerCovers) return;

            if (fundings == MaxFundings)
            {
                throw new InvalidOperationException(
                    $"entry '{entry.Key}': still short after {MaxFundings} fundings; writer holds {estimate.WriterSpendableDrops} of {estimate.WriterRequiredDrops} drops, "
                    + $"payer {estimate.PayerSpendableDrops} of {estimate.PayerRequiredDrops}");
            }

            if (!estimate.WriterCovers) await FundAsync(entry.Writer, cancellationToken);
            if (!estimate.PayerCovers) await FundAsync(AccountBook.Payer, cancellationToken);
        }
    }

    private async Task FundAsync(string role, CancellationToken cancellationToken)
    {
        // A client of its own: the run's client reconnects through its keeper, and a faucet wait of
        // tens of seconds is no reason to share that connection's state.
        XrplClient funding = new(_nodeUrl);
        await funding.Connect(cancellationToken);

        try
        {
            // Xrpl 11.2.0's FundWallet takes no token; the wait is abandoned on cancellation, the faucet's own request is not.
            WalletSugar.Funded funded = await WalletSugar.FundWallet(funding, _accounts!.Wallet(role), WalletSugar.FaucetNetwork.Testnet)
                .WaitAsync(cancellationToken);
            _accounts.MarkFunded(role);
            Console.WriteLine($"funded {role} ({_accounts.Address(role)}): balance {funded.Balance:0.######} XRP");
        }
        finally
        {
            await funding.Disconnect();
        }
    }

    /// <summary>
    /// Reads the manifest back and holds it against the plan; a file the reader accepts whole
    /// (up to its 20,000,000-byte default) is also read and compared by SHA-256.
    /// </summary>
    private async Task VerifyAsync(PlanEntry entry, string path, long length, string? cover, XrplFileLink link, CancellationToken cancellationToken)
    {
        Manifest manifest;
        string writer;

        if (length <= new ReaderOptions().MaxBytes)
        {
            ReadResult read = await new XrplFileReader(_source).ReadAsync(link, cancellationToken);

            byte[] expected;
            await using (FileStream stream = File.OpenRead(path))
            {
                expected = await SHA256.HashDataAsync(stream, cancellationToken);
            }

            if (!SHA256.HashData(read.Bytes).AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException($"entry '{entry.Key}': the file read back from {link} does not match {path}");
            }

            manifest = read.Manifest;
            writer = read.Writer;
        }
        else
        {
            ManifestRecord record = await new XrplFileReader(_source).ManifestAsync(link, cancellationToken);
            manifest = record.Manifest;
            writer = record.Writer;
        }

        if (manifest.Size != length
            || !string.Equals(manifest.Type, entry.Type, StringComparison.Ordinal)
            || !string.Equals(manifest.Cover, cover, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(writer, WriterAddress(entry), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"entry '{entry.Key}': the manifest at {link} says {manifest.Size} bytes, {manifest.Type}, cover {manifest.Cover ?? "none"}, writer {writer}; "
                + $"the plan says {length} bytes, {entry.Type}, cover {cover ?? "none"}, writer {WriterAddress(entry)}");
        }

        Console.WriteLine($"  {entry.Key}: verified ({(length <= new ReaderOptions().MaxBytes ? "whole file, SHA-256" : "manifest")})");
    }

    private static Dictionary<string, string> LoadLinks(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) ?? []
            : [];

    /// <summary>Written after every entry, in plan order, beside and then moved into place.</summary>
    private void SaveLinks()
    {
        Dictionary<string, string> ordered = [];
        foreach (PlanEntry entry in _plan)
        {
            if (_links.TryGetValue(entry.Key, out string? link)) ordered[entry.Key] = link;
        }

        string temporary = _linksPath + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ordered, Json) + "\n");
        File.Move(temporary, _linksPath, overwrite: true);
    }
}

internal static class Links
{
    /// <summary>Refuses a links.json that holds a link of another network, which would otherwise be skipped as already published.</summary>
    public static void RequireNetwork(IReadOnlyDictionary<string, string> links, string network, string path)
    {
        foreach ((string key, string text) in links)
        {
            if (!XrplFileLink.TryParse(text, out XrplFileLink? link) || !string.Equals(link.Network, network, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{path}: '{key}' is not a {network} link");
            }
        }
    }
}

using System.Diagnostics;
using System.Text;
using Konscious.Security.Cryptography;

static long TimeArgon2(int memoryKib, int iterations, int parallelism)
{
    byte[] password = Encoding.UTF8.GetBytes("correct horse battery staple");
    byte[] salt = new byte[16];
    Random.Shared.NextBytes(salt);

    // One untimed run so that JIT and the first large allocation do not land in the measurement.
    Derive(password, salt, memoryKib, iterations, parallelism);

    Stopwatch watch = Stopwatch.StartNew();
    Derive(password, salt, memoryKib, iterations, parallelism);
    watch.Stop();
    return watch.ElapsedMilliseconds;
}

static byte[] Derive(byte[] password, byte[] salt, int memoryKib, int iterations, int parallelism)
{
    using Argon2id argon = new(password)
    {
        Salt = salt,
        MemorySize = memoryKib,
        Iterations = iterations,
        DegreeOfParallelism = parallelism,
    };
    return argon.GetBytes(32);
}

static void TimeWalletDerivation()
{
    Xrpl.Wallet.XrplWallet generated = Xrpl.Wallet.XrplWallet.Generate();
    string seed = generated.Seed;

    Xrpl.Wallet.XrplWallet.FromSeed(seed); // untimed, for JIT

    Stopwatch watch = Stopwatch.StartNew();
    for (int i = 0; i < 1000; i++)
    {
        Xrpl.Wallet.XrplWallet.FromSeed(seed);
    }

    watch.Stop();

    Console.WriteLine($"FromSeed x1000 -> {watch.ElapsedMilliseconds} ms total, {watch.Elapsed.TotalMilliseconds / 1000:F3} ms each");
}

foreach (int memoryKib in new[] { 65_536, 131_072, 262_144, 524_288 })
{
    foreach (int iterations in new[] { 1, 2, 3, 4 })
    {
        Console.WriteLine($"m={memoryKib / 1024,4} MiB t={iterations} p=4 -> {TimeArgon2(memoryKib, iterations, 4)} ms");
    }
}

TimeWalletDerivation();

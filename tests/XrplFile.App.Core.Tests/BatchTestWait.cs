namespace XrplFile.App.Core.Tests;

internal static class BatchTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("the condition did not become true in 20 s");
            await Task.Delay(5);
        }
    }
}

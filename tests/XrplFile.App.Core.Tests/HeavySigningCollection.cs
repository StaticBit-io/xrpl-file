using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Groups every test class in this project whose fixture does real cryptographic signing over
/// roughly a thousand or more chunk transactions onto one xUnit collection, so they run one after
/// another instead of racing for CPU.
///
/// xUnit parallelizes different test collections against each other by default, and a class with no
/// explicit <c>[Collection]</c> attribute is its own implicit collection -- so, without this,
/// <c>ReadRunTests</c>'s differential test (the fix for this part's Critical 1, which has to publish
/// a genuinely multi-block file or it proves nothing -- see its own remarks) and
/// <c>PublicationRunTests</c>'s own <c>Progress_touches_the_idle_lock</c> (which already publishes a
/// two-block, ~1,025-chunk file, unrelated to this task) would run at the same time by default.
///
/// Measured on this machine: run together, the two reliably -- reproduced repeatedly with
/// <c>-method</c> filters isolating exactly those two tests, never once passing across several
/// attempts -- push real wall-clock scheduling far enough off that <c>VaultAutoLock</c>'s
/// fire-and-forget idle-lock callback (<c>FireAndForgetLock</c> / <c>LockQuietlyAsync</c> in
/// <c>VaultAutoLock.cs</c>) locks the vault mid-run, failing that test with the vault-is-locked
/// sentence. That is a pre-existing fragility in the fire-and-forget design
/// <c>VaultAutoLock.LockQuietlyAsync</c>'s own remarks already acknowledge ("a lock already in
/// effect (or in flight) stays exactly that") -- neither <c>ReadRunTests</c> nor
/// <c>PublicationRunTests</c> had, on its own, ever done enough concurrent signing to reveal it.
/// Confirmed it is not about the differential test's exact size: even the smallest file the fix
/// requires (1,024,001 bytes -- one byte over the two-block floor) reproduced it every time paired
/// with <c>Progress_touches_the_idle_lock</c>, while the original, single-block 40,960-byte version
/// never did. Confirmed absent under <c>-parallelMode none</c>, and absent again once this
/// collection serializes the two classes against each other -- which is the same fix at a much
/// narrower scope, so the rest of the suite keeps its parallelism (only these two classes stop
/// racing each other; every other collection is unaffected).
///
/// Nothing about which class is "wrong" is asserted by adding this: no production code
/// (<c>ReadRun</c>, <c>PublicationRun</c>, <c>VaultAutoLock</c>) changed to make it necessary, and
/// no assertion in either test class changed either. See the fix-round report for the fuller
/// investigation.
/// </summary>
[CollectionDefinition("HeavySigning")]
public class HeavySigningCollection
{
}

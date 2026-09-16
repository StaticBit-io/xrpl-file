using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Groups every test class in this project whose fixture does real cryptographic signing over
/// roughly a thousand or more chunk transactions onto one xUnit collection, so they run one after
/// another instead of racing for the thread pool.
///
/// xUnit parallelizes different test collections against each other by default, and a class with no
/// explicit <c>[Collection]</c> attribute is its own implicit collection -- so, without this,
/// <c>ReadRunTests</c>'s differential test (the fix for this part's Critical 1, which has to publish
/// a genuinely multi-block file or it proves nothing -- see its own remarks) and
/// <c>PublicationRunTests</c>'s own <c>Progress_touches_the_idle_lock</c> (which already publishes a
/// two-block, ~1,025-chunk file, unrelated to this task) would run at the same time by default.
///
/// <para>
/// <b>Corrected finding, round 2:</b> an earlier version of this comment (and of
/// <c>PublicationRunTests</c>' own note, and of commit <c>44fa61e</c>'s message) blamed
/// <c>VaultAutoLock</c>'s fire-and-forget idle-lock callback and, more generally, CPU contention.
/// Both were wrong, and a second, independent round of experiments ruled them out directly:
/// locking <c>ManualTimeProvider</c>'s own internals still failed 5/5 (not a race on the clock
/// itself); sixteen external CPU-burning threads on a twelve-core machine slowed the run from ~3.5s
/// to 8-19s and it still passed 4/4 (not plain CPU starvation); and replacing
/// <c>src/XrplFile.Core/XrplFileWriter.cs</c>'s two <c>new Progress&lt;int&gt;(...)</c> wrappers
/// (around line 155, for chunk-submission progress, and around line 238, for block-list-submission
/// progress) with a synchronous adapter made it pass 5/5.
/// </para>
/// <para>
/// The real cause: <see cref="System.Progress{T}"/> captures whatever
/// <see cref="System.Threading.SynchronizationContext"/> is current at construction and posts every
/// <c>Report</c> call there, falling back to the thread pool when there is none -- which is exactly
/// the case here, since both of those <c>new Progress&lt;int&gt;</c> instances are constructed deep
/// inside a publication running under <c>Task.Run</c>, off any synchronization context (this is the
/// precise hazard <c>PublicationRun.SynchronousProgress</c>'s own remark describes and promises to
/// avoid -- a promise <c>XrplFileWriter</c> keeps for its own "read-back" reports, called directly,
/// but not for these two, which re-wrap the already-synchronous reporter it was handed). This
/// fixture's roughly eleven hundred chunk-submission progress reports each post one more work item
/// to the shared process-wide thread-pool queue; with <c>ReadRunTests</c>' own eleven-hundred-chunk
/// differential test flooding that same queue at the same time, the posted callbacks that eventually
/// call <c>PublicationRun.Observe</c> -- and so <c>VaultAutoLock.Touch()</c> -- arrive late enough,
/// against the fake clock <c>ClockAdvancingSource</c> advances synchronously and independently of
/// any of this, that the five-minute idle window it tracks expires and the vault genuinely locks
/// mid-publication. <c>VaultAutoLock</c> behaves exactly as designed throughout; the defect is
/// <c>XrplFileWriter</c> silently discarding the synchronous-reporting guarantee
/// <c>PublicationRun</c> depends on.
/// </para>
/// <para>
/// <b>The fix belongs in <c>XrplFileWriter</c>, not here.</b> This part (C4) may not touch
/// <c>src/XrplFile.Core</c>, and a behaviour change to a published package needs its own branch and
/// release note regardless -- tracked as a separate task. This collection is a symptom-level
/// workaround, kept only because two test classes in this project happen to generate enough
/// concurrent signing to trigger a real defect elsewhere; it should be removed once
/// <c>XrplFileWriter</c>'s progress reporting is fixed to report synchronously the way
/// <c>PublicationRun.SynchronousProgress</c>'s own contract already promises.
/// </para>
/// <para>
/// Verified absent under <c>-parallelMode none</c>, and absent again once this collection serializes
/// the two classes against each other -- the same fix at a much narrower scope, so the rest of the
/// suite keeps its parallelism (only these two classes stop racing each other; every other
/// collection is unaffected). See the fix-round report for the fuller investigation.
/// </para>
/// </summary>
[CollectionDefinition("HeavySigning")]
public class HeavySigningCollection
{
}

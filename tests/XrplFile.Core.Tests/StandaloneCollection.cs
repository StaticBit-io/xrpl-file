using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Groups every integration test class that funds itself from the genesis account onto one
/// xUnit collection, so they run one after another instead of racing.
///
/// xUnit parallelizes different test collections against each other by default, but a class
/// with no explicit <c>[Collection]</c> attribute is its own implicit collection. Two such
/// classes both reading and bumping the genesis account's sequence at the same time is a race:
/// the loser's submission answers <c>terPRE_SEQ</c>, failing an assertion that has nothing to
/// do with what the test actually measures. Putting every class that touches the genesis
/// account under <c>[Collection("Standalone")]</c> serializes them against each other; that
/// alone is enough, and setting <c>DisableParallelization</c> additionally keeps this
/// collection from running alongside any other collection in the assembly.
/// </summary>
[CollectionDefinition("Standalone", DisableParallelization = true)]
public class StandaloneCollectionDefinition
{
}

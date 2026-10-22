namespace XrplFile;

/// <summary>
/// Builds a run's <see cref="BatchStrategy"/> once the session knows its budget, with the session as
/// its fence recorder (spec 4.1, 6.1). The session is what persists the fence, and it exists only
/// once the estimate has approved the run, so it builds the strategy rather than being handed one.
/// </summary>
public delegate BatchStrategy BatchStrategyFactory(BatchBudget budget, IBatchFenceRecorder fence);

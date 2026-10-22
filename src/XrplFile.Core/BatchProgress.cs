namespace XrplFile;

/// <summary>
/// The call's progress (spec B20, 4.4): the number of its memos a sweep found landed with tesSUCCESS.
/// It counts memos found, never sequence movement, so it only grows.
/// </summary>
internal sealed class BatchProgress(IProgress<int>? inner)
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _found = [];
    private int _reported;
    private bool _sent;

    /// <summary>
    /// A transmission of the call was answered. The first one reports the count found so far, once,
    /// usually zero: the first sweep finds landed memos 5 s or more into sending, and until then a
    /// caller showing this progress could not tell a pass that is sending from one not started.
    /// </summary>
    public void Sent()
    {
        lock (_gate)
        {
            if (_sent) return;

            _sent = true;
            inner?.Report(_reported);
        }
    }

    public void Found(IEnumerable<int> indices)
    {
        lock (_gate)
        {
            foreach (int index in indices) _found.Add(index);

            if (_found.Count <= _reported) return;

            _reported = _found.Count;
            inner?.Report(_reported);
        }
    }
}

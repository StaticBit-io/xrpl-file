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

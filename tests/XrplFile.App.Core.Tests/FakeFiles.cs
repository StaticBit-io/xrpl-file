using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

/// <summary>Files by path, opened as sources that count their reads and remember their disposal.</summary>
public sealed class FakeFiles
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CountingPublicationSource> _opened = [];

    public IReadOnlyList<CountingPublicationSource> Opened
    {
        get { lock (_opened) return [.. _opened]; }
    }

    public string Add(string path, byte[] bytes)
    {
        _files[path] = bytes;
        return path;
    }

    public IPublicationSource Open(string path)
    {
        if (!_files.TryGetValue(path, out byte[]? bytes)) throw new FileNotFoundException("fake: no such file", path);

        CountingPublicationSource source = new(new MemoryPublicationSource(bytes));
        lock (_opened) _opened.Add(source);
        return source;
    }
}

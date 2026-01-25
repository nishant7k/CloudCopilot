using System.Collections.Concurrent;

namespace CloudCopilot.Services;

public sealed class ToolCallStore
{
    private readonly ConcurrentQueue<ToolCallLog> _entries = new();
    private const int MaxEntries = 50;

    public IReadOnlyCollection<ToolCallLog> Entries => _entries.ToArray();

    public void Add(ToolCallLog entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }
}

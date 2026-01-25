using System.Collections.Concurrent;

namespace CloudCopilot.Services;

public sealed record McpResultLog(string ToolName, string RawJson, DateTimeOffset Timestamp);

public sealed class McpResultStore
{
    private readonly ConcurrentQueue<McpResultLog> _entries = new();
    private const int MaxEntries = 20;

    public IReadOnlyCollection<McpResultLog> Entries => _entries.ToArray();

    public void Add(McpResultLog entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }
}

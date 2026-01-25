using System.Collections.Concurrent;

namespace CloudCopilot.Services;

public sealed class ChatState
{
    private readonly ConcurrentQueue<ChatMessage> _messages = new();

    public IReadOnlyCollection<ChatMessage> Messages => _messages.ToArray();

    public bool IsBusy { get; set; }

    public string? LastError { get; set; }

    public void AddMessage(ChatMessage message) => _messages.Enqueue(message);

    public void Clear()
    {
        while (_messages.TryDequeue(out _))
        {
        }
        LastError = null;
    }
}

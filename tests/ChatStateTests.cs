using CloudCopilot.Services;

namespace CloudCopilot.Tests;

public class ChatStateTests
{
    [Fact]
    public void AddMessage_AppendsToMessages()
    {
        var state = new ChatState();
        var message = new ChatMessage("user", "hello", DateTimeOffset.UtcNow);

        state.AddMessage(message);

        Assert.Contains(message, state.Messages);
    }

    [Fact]
    public void Clear_RemovesAllMessagesAndResetsLastError()
    {
        var state = new ChatState();
        state.AddMessage(new ChatMessage("assistant", "hi", DateTimeOffset.UtcNow));
        state.LastError = "boom";

        state.Clear();

        Assert.Empty(state.Messages);
        Assert.Null(state.LastError);
    }

    [Fact]
    public void Messages_ReturnsSnapshot()
    {
        var state = new ChatState();
        state.AddMessage(new ChatMessage("user", "first", DateTimeOffset.UtcNow));

        var snapshot = state.Messages;
        state.AddMessage(new ChatMessage("assistant", "second", DateTimeOffset.UtcNow));

        Assert.Single(snapshot);
        Assert.Equal(2, state.Messages.Count);
    }
}

public class ConnectionStatusTests
{
    [Fact]
    public void McpTools_DefaultsToEmptyList()
    {
        var status = new ConnectionStatus();

        Assert.NotNull(status.McpTools);
        Assert.Empty(status.McpTools);
    }
}

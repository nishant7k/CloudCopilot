namespace CloudCopilot.Services;

public sealed record ChatMessage(string Role, string Content, DateTimeOffset Timestamp);

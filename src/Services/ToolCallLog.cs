namespace CloudCopilot.Services;

public sealed record ToolCallLog(
    string Name,
    string ArgumentsJson,
    TimeSpan Duration,
    DateTimeOffset Timestamp,
    bool Succeeded,
    string? Error);

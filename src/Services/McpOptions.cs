namespace CloudCopilot.Services;

public sealed class McpOptions
{
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

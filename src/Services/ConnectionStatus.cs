namespace CloudCopilot.Services;

public sealed class ConnectionStatus
{
    public bool McpConnected { get; set; }
    public string? McpError { get; set; }
    public IReadOnlyList<McpToolDefinition> McpTools { get; set; } = Array.Empty<McpToolDefinition>();

    public bool CopilotConnected { get; set; }
    public string? CopilotError { get; set; }
}

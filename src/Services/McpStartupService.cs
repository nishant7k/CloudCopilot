namespace CloudCopilot.Services;

public sealed class McpStartupService : IHostedService
{
    private readonly McpClient _mcpClient;
    private readonly ConnectionStatus _status;
    private readonly ILogger<McpStartupService> _logger;

    public McpStartupService(McpClient mcpClient, ConnectionStatus status, ILogger<McpStartupService> logger)
    {
        _mcpClient = mcpClient;
        _status = status;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tools = await _mcpClient.ListToolsAsync(cancellationToken);
            _status.McpTools = tools;
            _status.McpConnected = true;
            _status.McpError = null;
            _logger.LogInformation("MCP tools loaded {ToolCount}", tools.Count);
        }
        catch (Exception ex)
        {
            _status.McpConnected = false;
            _status.McpError = ex.Message;
            _logger.LogError(ex, "Failed to load MCP tools");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

namespace CloudCopilot.Services;

public sealed class CopilotDebugPromptService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CopilotDebugPromptService> _logger;

    public CopilotDebugPromptService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<CopilotDebugPromptService> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var prompt = _configuration["COPILOT_DEBUG_PROMPT"];
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        _logger.LogInformation("Sending debug prompt to Copilot: {Prompt}", prompt);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var agent = scope.ServiceProvider.GetRequiredService<CopilotAgent>();
            var response = await agent.HandleAsync(prompt, cancellationToken);
            _logger.LogInformation("Debug prompt response: {Response}", response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debug prompt failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

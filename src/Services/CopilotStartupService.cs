namespace CloudCopilot.Services;

public sealed class CopilotStartupService : IHostedService
{
    private readonly CopilotClientManager _copilotClientManager;

    public CopilotStartupService(CopilotClientManager copilotClientManager)
    {
        _copilotClientManager = copilotClientManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _copilotClientManager.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using CloudCopilot.Components;
using CloudCopilot.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<ConnectionStatus>();
builder.Services.AddSingleton<ToolCallStore>();
builder.Services.AddSingleton<McpResultStore>();
builder.Services.AddSingleton<CopilotClientManager>();
builder.Services.AddScoped<ChatState>();
builder.Services.AddScoped<CopilotAgent>();

builder.Services.Configure<McpOptions>(options =>
{
    options.Url = builder.Configuration["VANTAGE_INSTANCES_MCP_URL"];
    options.ApiKey = builder.Configuration["VANTAGE_INSTANCES_MCP_KEY"]
        ?? builder.Configuration["VANTAGE_INSTANCES_MCP_API_KEY"];
});

builder.Services.AddHttpClient<McpClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHostedService<McpStartupService>();
builder.Services.AddHostedService<CopilotStartupService>();
builder.Services.AddHostedService<CopilotDebugPromptService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapGet("/health", (ConnectionStatus status) =>
{
    return Results.Ok(new
    {
        mcp = new
        {
            connected = status.McpConnected,
            error = status.McpError,
            tools = status.McpTools.Select(tool => tool.Name).ToArray()
        },
        copilot = new
        {
            connected = status.CopilotConnected,
            error = status.CopilotError
        }
    });
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

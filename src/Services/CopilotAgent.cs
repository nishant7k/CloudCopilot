using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Reflection;

namespace CloudCopilot.Services;

public sealed class CopilotAgent : IAsyncDisposable
{
    private static readonly string[] AllowedTools =
    {
        "list_providers",
        "list_families",
        "search_instances",
        "get_pricing",
        "compare_instances"
    };

    private static readonly Regex AwsRegionRegex = new("\\b[a-z]{2}-[a-z]+-\\d\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GcpRegionRegex = new("\\b[a-z]{2,}-[a-z]+\\d\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AzureRegionRegex = new(
        "\\b(eastus2?|westus2?|centralus|northcentralus|southcentralus|westcentralus|westeurope|northeurope|southeastasia|eastasia|uksouth|ukwest|australiaeast|australiasoutheast|brazilsouth|canadacentral|canadaeast|francecentral|germanywestcentral|japaneast|japanwest|koreacentral|koreasouth|southindia|westindia|centralindia|southafricanorth|uaenorth)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CopilotClientManager _copilotClientManager;
    private readonly McpClient _mcpClient;
    private readonly ILogger<CopilotAgent> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private object? _session;

    public CopilotAgent(CopilotClientManager copilotClientManager, McpClient mcpClient, ILogger<CopilotAgent> logger)
    {
        _copilotClientManager = copilotClientManager;
        _mcpClient = mcpClient;
        _logger = logger;
    }

    public async Task<string> HandleAsync(string userMessage, CancellationToken cancellationToken)
    {
        var clarification = BuildClarifyingQuestion(userMessage);
        if (!string.IsNullOrWhiteSpace(clarification))
        {
            return clarification;
        }

        var planPrompt = BuildPlanPrompt(userMessage);
        _logger.LogInformation("Copilot plan prompt {Prompt}", planPrompt);
        var planResponse = await SendPromptAsync(planPrompt, cancellationToken);
        if (IsCopilotError(planResponse))
        {
            return planResponse;
        }
        var plan = ParsePlan(planResponse);

        if (plan is null)
        {
            return "I couldn't interpret that request. Could you rephrase?";
        }

        if (string.Equals(plan.Action, "ask_clarifying_question", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(plan.Question))
        {
            return SanitizeQuestion(plan.Question!);
        }

        if (string.Equals(plan.Action, "direct_answer", StringComparison.OrdinalIgnoreCase))
        {
            var answer = ExtractAnswer(plan.Answer);
            if (!string.IsNullOrWhiteSpace(answer))
            {
                return answer!;
            }
        }

        if (!string.Equals(plan.Action, "call_tools", StringComparison.OrdinalIgnoreCase) || plan.Calls is null)
        {
            return "I need more detail to continue. What provider and region should I use?";
        }

        var toolResults = new List<object>();
        foreach (var call in plan.Calls)
        {
            if (call.Name is null || !AllowedTools.Contains(call.Name, StringComparer.OrdinalIgnoreCase))
            {
                return "I can only use: list_providers, list_families, search_instances, get_pricing, compare_instances.";
            }

            try
            {
                var result = await ExecuteToolAsync(call, userMessage, cancellationToken);
                toolResults.Add(new { tool = call.Name, result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool call failed {ToolName}", call.Name);
                return "I couldn't complete that request because the data source rejected the tool input. Try adding a specific provider and region, or rephrase the request.";
            }
        }

        var answerPrompt = BuildAnswerPrompt(userMessage, toolResults);
        _logger.LogInformation("Copilot answer prompt {Prompt}", answerPrompt);
        var answerResponse = await SendPromptAsync(answerPrompt, cancellationToken);
        if (IsCopilotError(answerResponse))
        {
            return answerResponse;
        }
        if (TryParsePlan(answerResponse, out var answerPlan) && answerPlan is not null)
        {
            if (string.Equals(answerPlan.Action, "ask_clarifying_question", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(answerPlan.Question))
            {
                return SanitizeQuestion(answerPlan.Question!);
            }

            if (string.Equals(answerPlan.Action, "direct_answer", StringComparison.OrdinalIgnoreCase))
            {
                var answer = ExtractAnswer(answerPlan.Answer);
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    return answer!;
                }
            }
        }

        return StripCodeFences(answerResponse);
    }

    private static bool IsCopilotError(string? response)
    {
        return !string.IsNullOrWhiteSpace(response)
               && response.StartsWith("Copilot error:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<object?> ExecuteToolAsync(CopilotToolCall call, string userMessage, CancellationToken cancellationToken)
    {
        var args = call.Args ?? new Dictionary<string, object?>();
        var provider = NormalizeProvider(GetStringArg(args, "provider") ?? ExtractProvider(userMessage));

        return call.Name switch
        {
            "list_providers" => await ListProvidersAsync(cancellationToken),
            "list_families" => await ListFamiliesAsync(provider, cancellationToken),
            "search_instances" => await SearchInstancesAsync(provider, args, cancellationToken),
            "get_pricing" => await GetPricingAsync(provider, args, cancellationToken),
            "compare_instances" => await CompareInstancesAsync(provider, args, cancellationToken),
            _ => new { error = $"Unsupported tool {call.Name}" }
        };
    }

    private async Task<object?> ListProvidersAsync(CancellationToken cancellationToken)
    {
        var tools = await _mcpClient.ListToolsAsync(cancellationToken);
        var providers = tools
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.StartsWith("get-", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[1])
            .Select(MapProviderToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToArray();

        return new { providers };
    }

    private async Task<object?> ListFamiliesAsync(string provider, CancellationToken cancellationToken)
    {
        var tool = GetFamiliesTool(provider);
        if (tool is null)
        {
            return new { error = "Provider not supported for list_families." };
        }

        return await _mcpClient.CallToolAsync(tool, null, cancellationToken);
    }

    private async Task<object?> SearchInstancesAsync(string provider, Dictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var family = GetStringArg(args, "family");
        if (!string.IsNullOrWhiteSpace(family))
        {
            var tool = GetInstancesForFamilyTool(provider);
            if (tool is null)
            {
                return new { error = "Provider not supported for search_instances." };
            }

            return await _mcpClient.CallToolAsync(tool, new { family }, cancellationToken);
        }

        var indexesTool = GetIndexesTool(provider);
        if (indexesTool is null)
        {
            return new { error = "Provider not supported for search_instances." };
        }

        return await _mcpClient.CallToolAsync(indexesTool, null, cancellationToken);
    }

    private async Task<object?> GetPricingAsync(string provider, Dictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var instanceType = GetStringArg(args, "instanceType");
        var region = GetStringArg(args, "region");

        if (string.IsNullOrWhiteSpace(instanceType) || string.IsNullOrWhiteSpace(region))
        {
            return new { error = "instanceType and region are required for get_pricing." };
        }

        var tool = GetRegionPricingTool(provider);
        if (tool is null)
        {
            return new { error = "Provider not supported for get_pricing." };
        }

        return await _mcpClient.CallToolAsync(tool, new { instanceType, region }, cancellationToken);
    }

    private async Task<object?> CompareInstancesAsync(string provider, Dictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var region = GetStringArg(args, "region");
        var list = GetStringListArg(args, "list");

        if (string.IsNullOrWhiteSpace(region) || list.Count == 0)
        {
            return new { error = "region and list are required for compare_instances." };
        }

        var tool = GetRegionPricingTool(provider);
        if (tool is null)
        {
            return new { error = "Provider not supported for compare_instances." };
        }

        var results = new List<object>();
        foreach (var instanceType in list)
        {
            var result = await _mcpClient.CallToolAsync(tool, new { instanceType, region }, cancellationToken);
            results.Add(new { instanceType, result });
        }

        return new { region, provider, results };
    }

    private async Task<string> SendPromptAsync(string prompt, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken);
        var responseBuilder = new StringBuilder();
        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = SubscribeToEvents(session, responseBuilder, responseTcs, _logger);
        var sendResult = await InvokeSendAsync(session, prompt, cancellationToken);

        if (TryExtractContent(sendResult, out var immediateContent) && !string.IsNullOrWhiteSpace(immediateContent))
        {
            _logger.LogInformation("Copilot response (immediate) {Response}", immediateContent);
            if (!Guid.TryParse(immediateContent, out _))
            {
                return immediateContent;
            }
        }

        if (subscription is null)
        {
            if (TryExtractContent(sendResult, out var responseId) && Guid.TryParse(responseId, out _))
            {
                var fetched = await TryFetchResponseByIdAsync(session, responseId!, cancellationToken);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    _logger.LogInformation("Copilot response (fetched) {Response}", fetched);
                    return fetched!;
                }
            }

            return "Copilot session did not stream a response.";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        using var registration = timeoutCts.Token.Register(() => responseTcs.TrySetCanceled(timeoutCts.Token));

        try
        {
            var response = await responseTcs.Task;
            _logger.LogInformation("Copilot response (streamed) {Response}", response);
            return string.IsNullOrWhiteSpace(response) ? "No response from Copilot." : response.Trim();
        }
        catch (OperationCanceledException)
        {
            return "Copilot did not respond in time.";
        }
    }

    private async Task<object> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return _session;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                _session = await _copilotClientManager.CreateSessionAsync(cancellationToken);
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        return _session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_session is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static IDisposable? SubscribeToEvents(
        object session,
        StringBuilder responseBuilder,
        TaskCompletionSource<string> responseTcs,
        ILogger logger)
    {
        var sessionType = session.GetType();
        var onMethod = sessionType.GetMethods()
            .FirstOrDefault(m => m.Name == "On" && m.GetParameters().Length == 1);

        if (onMethod is null)
        {
            return null;
        }

        var handlerType = onMethod.GetParameters()[0].ParameterType;
        var handler = SessionEventHandler.Create(handlerType, responseBuilder, responseTcs, logger);

        return onMethod.Invoke(session, new[] { handler }) as IDisposable;
    }

    private static async Task<object?> InvokeSendAsync(object session, string prompt, CancellationToken cancellationToken)
    {
        var sessionType = session.GetType();
        var methods = sessionType.GetMethods().Where(m => m.Name == "SendAsync").ToArray();

        var messageOptions = CreateMessageOptions(sessionType.Assembly, prompt);
        MethodInfo? target = methods.FirstOrDefault(m => m.GetParameters().Length == 1 && messageOptions != null && m.GetParameters()[0].ParameterType.IsInstanceOfType(messageOptions))
            ?? methods.FirstOrDefault(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
            ?? methods.FirstOrDefault(m => m.GetParameters().Length == 2);

        if (target is null)
        {
            throw new InvalidOperationException("Copilot session SendAsync method not found.");
        }

        object?[] parameters = target.GetParameters().Length switch
        {
            1 when messageOptions is not null => new[] { messageOptions },
            1 => new object?[] { prompt },
            2 when messageOptions is not null => new object?[] { messageOptions, cancellationToken },
            2 => new object?[] { prompt, cancellationToken },
            _ => new object?[] { prompt }
        };

        var result = target.Invoke(session, parameters);
        if (result is Task task)
        {
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        return result;
    }

    private static object? CreateMessageOptions(System.Reflection.Assembly assembly, string prompt)
    {
        var optionsType = assembly.GetType("GitHub.Copilot.SDK.MessageOptions");
        if (optionsType is null)
        {
            return null;
        }

        var options = Activator.CreateInstance(optionsType);
        if (options is null)
        {
            return null;
        }

        SetProperty(options, "Content", prompt);
        SetProperty(options, "Prompt", prompt);
        SetProperty(options, "Message", prompt);
        SetProperty(options, "Role", "user");
        return options;
    }

    private static bool TryExtractContent(object? value, out string? content)
    {
        content = null;
        if (value is null)
        {
            return false;
        }

        if (value is string direct)
        {
            content = direct;
            return true;
        }

        var contentProperty = value.GetType().GetProperty("Content") ?? value.GetType().GetProperty("Message");
        if (contentProperty is not null)
        {
            content = contentProperty.GetValue(value)?.ToString();
            return true;
        }

        return false;
    }

    private async Task<string?> TryFetchResponseByIdAsync(object session, string responseId, CancellationToken cancellationToken)
    {
        var sessionType = session.GetType();
        var candidates = sessionType.GetMethods()
            .Where(m => m.ReturnType == typeof(Task) || (m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
            .Where(m => m.Name.Contains("Response", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
            {
                continue;
            }

            object?[] args = parameters.Length switch
            {
                1 => new object?[] { responseId },
                2 when parameters[1].ParameterType == typeof(CancellationToken) => new object?[] { responseId, cancellationToken },
                _ => new object?[] { responseId }
            };

            try
            {
                var result = method.Invoke(session, args);
                if (result is Task task)
                {
                    await task;
                    var taskResult = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (TryExtractContent(taskResult, out var content) && !string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }

                    if (taskResult is not null)
                    {
                        return taskResult.ToString();
                    }
                }
                else if (result is not null)
                {
                    return result.ToString();
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static CopilotPlan? ParsePlan(string response)
    {
        return TryParsePlan(response, out var plan) ? plan : null;
    }

    private static bool TryParsePlan(string response, out CopilotPlan? plan)
    {
        plan = null;
        var json = ExtractJson(response);
        if (json is null)
        {
            return false;
        }

        try
        {
            plan = JsonSerializer.Deserialize<CopilotPlan>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return plan is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractJson(string response)
    {
        response = StripCodeFences(response);
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return response[start..(end + 1)];
    }

    private static string StripCodeFences(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return response;
        }

        var trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return response;
        }

        var fenceEnd = trimmed.IndexOf('\n');
        if (fenceEnd < 0)
        {
            return response;
        }

        var withoutStart = trimmed[(fenceEnd + 1)..];
        var endIndex = withoutStart.LastIndexOf("```", StringComparison.Ordinal);
        if (endIndex >= 0)
        {
            return withoutStart[..endIndex].Trim();
        }

        return withoutStart.Trim();
    }

    private static string? ExtractAnswer(JsonElement answer)
    {
        return answer.ValueKind switch
        {
            JsonValueKind.String => answer.GetString(),
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => null,
            _ => answer.GetRawText()
        };
    }

    private static string BuildPlanPrompt(string userMessage)
    {
        return $$$"""
SYSTEM:
You are CloudCopilot. You must respond with ONLY JSON, no markdown.
Allowed tools:
- list_providers
- list_families(provider)
- search_instances(provider, region?, vcpus?, memoryGiB?, gpu?, family?, priceMax?, purchaseOption?, os?)
- get_pricing(provider, instanceType, region, purchaseOption, os)
- compare_instances(provider, list, region, purchaseOption, os)
Never call any tool other than the list above. Do not call bash or filesystem tools.
Default purchaseOption to on-demand and os to linux when not specified. Default provider to aws when not specified.
For pricing by family, if an instance size isn't specified, pick the smallest instance type returned by get-*-instances-for-family and proceed. If the family list is large, limit to the first 10 families and note that in the answer.
Avoid asking multiple follow-up questions; make reasonable defaults and proceed.
If a tool call is required, reply with:
{"action":"call_tools","calls":[{"name":"tool_name","args":{...}}]}
If clarification is required, reply with:
{"action":"ask_clarifying_question","question":"..."}
If no tool is needed, reply with:
{"action":"direct_answer","answer":"..."}
For comparisons across providers, call get_pricing multiple times (one per provider) and then respond.
USER:
{{{userMessage}}}
""";
    }

    private static string BuildAnswerPrompt(string userMessage, List<object> toolResults)
    {
        var toolsJson = JsonSerializer.Serialize(toolResults, new JsonSerializerOptions { WriteIndented = false });
        return $$$"""
SYSTEM:
You are CloudCopilot. Use ONLY the tool results provided. Never invent prices or specs.
Keep the response short. If pricing is requested, include provider, region, OS, purchase option, hourly price, and key specs.
If required data is missing, ask exactly one clarifying question.
If returning a structured answer, use:
{"action":"direct_answer","answer":{"message":"...","comparable_options":[{"instanceType":"...","minPrice":"..."}],"note":"..."}}
For comparisons, prefer the structured direct_answer format.
If you need to return a table, use:
{"action":"direct_answer","answer":{"message":"...","table":[["col1","col2"],["row1","row1"]],"note":"..."}}
Do not omit requested details; avoid summarizing unless the user asks for a summary.
USER:
{{{userMessage}}}
TOOL_RESULTS:
{{{toolsJson}}}
""";
    }

    private static string? BuildClarifyingQuestion(string userMessage)
    {
        if (!RequiresPricing(userMessage))
        {
            return null;
        }

        var hasRegion = ExtractRegion(userMessage) is not null;

        if (!hasRegion)
        {
            return "Which region should I use?";
        }

        return null;
    }

    private static bool RequiresPricing(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("price")
               || lower.Contains("cost")
               || lower.Contains("pricing")
               || lower.Contains("hourly")
               || lower.Contains("per hour")
               || lower.Contains("compare")
               || lower.Contains("cheapest")
               || lower.Contains("rate");
    }

    private static string? ExtractRegion(string message)
    {
        var awsMatch = AwsRegionRegex.Match(message);
        if (awsMatch.Success)
        {
            return awsMatch.Value;
        }

        var azureMatch = AzureRegionRegex.Match(message);
        if (azureMatch.Success)
        {
            return azureMatch.Value.ToLowerInvariant();
        }

        var gcpMatch = GcpRegionRegex.Match(message);
        if (gcpMatch.Success)
        {
            return gcpMatch.Value;
        }

        return null;
    }

    private static string? ExtractPurchaseOption(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("spot") || lower.Contains("preemptible"))
        {
            return "spot";
        }

        if (lower.Contains("reserved"))
        {
            return "reserved";
        }

        if (lower.Contains("savings plan") || lower.Contains("savings"))
        {
            return "savings_plan";
        }

        if (lower.Contains("on-demand") || lower.Contains("on demand") || lower.Contains("ondemand"))
        {
            return "on_demand";
        }

        return null;
    }

    private static string? ExtractOperatingSystem(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("windows"))
        {
            return "windows";
        }

        if (lower.Contains("linux") || lower.Contains("ubuntu") || lower.Contains("rhel") || lower.Contains("debian"))
        {
            return "linux";
        }

        return null;
    }

    private static string? ExtractProvider(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("aws") || lower.Contains("ec2"))
        {
            return "aws";
        }

        if (lower.Contains("azure"))
        {
            return "azure";
        }

        if (lower.Contains("gcp") || lower.Contains("google"))
        {
            return "gcp";
        }

        return null;
    }

    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "aws";
        }

        var lower = provider.ToLowerInvariant();
        return lower switch
        {
            "ec2" => "aws",
            "aws" => "aws",
            "azure" => "azure",
            "gcp" => "gcp",
            "google" => "gcp",
            "rds" => "rds",
            "elasticache" => "elasticache",
            "opensearch" => "opensearch",
            "redshift" => "redshift",
            _ => lower
        };
    }

    private static string MapProviderToken(string token)
    {
        return token.ToLowerInvariant() switch
        {
            "ec2" => "aws",
            "aws" => "aws",
            "azure" => "azure",
            "gcp" => "gcp",
            "rds" => "rds",
            "elasticache" => "elasticache",
            "opensearch" => "opensearch",
            "redshift" => "redshift",
            _ => token.ToLowerInvariant()
        };
    }

    private static string? GetRegionPricingTool(string provider)
    {
        return provider switch
        {
            "aws" => "get-ec2-region-pricing",
            "azure" => "get-azure-region-pricing",
            "gcp" => "get-gcp-region-pricing",
            "rds" => "get-rds-region-pricing",
            "elasticache" => "get-elasticache-region-pricing",
            "opensearch" => "get-opensearch-region-pricing",
            "redshift" => "get-redshift-region-pricing",
            _ => null
        };
    }

    private static string? GetFamiliesTool(string provider)
    {
        return provider switch
        {
            "aws" => "get-ec2-instance-families",
            "azure" => "get-azure-instance-families",
            "gcp" => "get-gcp-instance-families",
            "rds" => "get-rds-instance-families",
            "elasticache" => "get-elasticache-instance-families",
            "opensearch" => "get-opensearch-instance-families",
            _ => null
        };
    }

    private static string? GetInstancesForFamilyTool(string provider)
    {
        return provider switch
        {
            "aws" => "get-ec2-instances-for-family",
            "azure" => "get-azure-instances-for-family",
            "gcp" => "get-gcp-instances-for-family",
            "rds" => "get-rds-instances-for-family",
            "elasticache" => "get-elasticache-instances-for-family",
            "opensearch" => "get-opensearch-instances-for-family",
            _ => null
        };
    }

    private static string? GetIndexesTool(string provider)
    {
        return provider switch
        {
            "aws" => "get-ec2-indexes",
            "azure" => "get-azure-indexes",
            "gcp" => "get-gcp-indexes",
            "rds" => "get-rds-indexes",
            "elasticache" => "get-elasticache-indexes",
            "opensearch" => "get-opensearch-indexes",
            _ => null
        };
    }

    private static string SanitizeQuestion(string question)
    {
        var trimmed = question.Trim();
        var index = trimmed.IndexOf('?');
        if (index >= 0)
        {
            return trimmed[..(index + 1)];
        }

        return trimmed.EndsWith('.') ? trimmed[..^1] + "?" : trimmed + "?";
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        return value.ToString();
    }

    private static int? GetIntArg(Dictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            if (element.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (int.TryParse(element.ToString(), out intValue))
            {
                return intValue;
            }
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static double? GetDoubleArg(Dictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            if (element.TryGetDouble(out var doubleValue))
            {
                return doubleValue;
            }

            if (double.TryParse(element.ToString(), out doubleValue))
            {
                return doubleValue;
            }
        }

        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static bool? GetBoolArg(Dictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (bool.TryParse(element.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return bool.TryParse(value.ToString(), out var boolParsed) ? boolParsed : null;
    }

    private static List<string> GetStringListArg(Dictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return new List<string>();
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        if (value is IEnumerable<object> list)
        {
            return list.Select(item => item.ToString() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        return new List<string> { value.ToString() ?? string.Empty };
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        if (property.PropertyType.IsInstanceOfType(value))
        {
            property.SetValue(target, value);
            return;
        }

        try
        {
            var converted = Convert.ChangeType(value, property.PropertyType);
            property.SetValue(target, converted);
        }
        catch
        {
        }
    }

    private sealed class SessionEventHandler
    {
        private readonly StringBuilder _builder;
        private readonly TaskCompletionSource<string> _tcs;
        private readonly ILogger _logger;

        private SessionEventHandler(StringBuilder builder, TaskCompletionSource<string> tcs, ILogger logger)
        {
            _builder = builder;
            _tcs = tcs;
            _logger = logger;
        }

        public static Delegate Create(Type handlerType, StringBuilder builder, TaskCompletionSource<string> tcs, ILogger logger)
        {
            var handler = new SessionEventHandler(builder, tcs, logger);
            var invoke = handlerType.GetMethod("Invoke");
            var returnsTask = invoke?.ReturnType == typeof(Task);
            var methodName = returnsTask ? nameof(HandleAsync) : nameof(Handle);
            var method = typeof(SessionEventHandler).GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Session event handler method not found.");
            return Delegate.CreateDelegate(handlerType, handler, method);
        }

        private void Handle(object? evt)
        {
            if (evt is null)
            {
                return;
            }

            var eventType = GetEventType(evt);
            _logger.LogInformation("Copilot event {EventType} {EventPayload}", eventType, SerializeEvent(evt));
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return;
            }

            if (eventType.Contains("assistant.message", StringComparison.OrdinalIgnoreCase))
            {
                var content = ExtractContent(evt);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    _builder.Append(content);
                }

                if (!eventType.Contains("delta", StringComparison.OrdinalIgnoreCase))
                {
                    if (_builder.Length > 0)
                    {
                        _tcs.TrySetResult(_builder.ToString());
                    }
                    else if (!HasToolRequests(evt))
                    {
                        _tcs.TrySetResult(string.Empty);
                    }
                }
            }
            else if (eventType.Contains("session.error", StringComparison.OrdinalIgnoreCase))
            {
                var errorMessage = ExtractErrorMessage(evt);
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    _builder.Append(errorMessage);
                    _tcs.TrySetResult(_builder.ToString());
                }
            }
            else if (eventType.Contains("session.idle", StringComparison.OrdinalIgnoreCase)
                     || eventType.Contains("response.done", StringComparison.OrdinalIgnoreCase))
            {
                _tcs.TrySetResult(_builder.ToString());
            }
        }

        private Task HandleAsync(object? evt)
        {
            Handle(evt);
            return Task.CompletedTask;
        }

        private static string SerializeEvent(object evt)
        {
            try
            {
                return JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = false });
            }
            catch
            {
                return evt.ToString() ?? string.Empty;
            }
        }

        private static string? GetEventType(object evt)
        {
            var typeProperty = evt.GetType().GetProperty("Type") ?? evt.GetType().GetProperty("EventType");
            var typeValue = typeProperty?.GetValue(evt)?.ToString();
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                return typeValue;
            }

            return evt.GetType().Name;
        }

        private static string? ExtractContent(object evt)
        {
            var dataProperty = evt.GetType().GetProperty("Data");
            var data = dataProperty?.GetValue(evt) ?? evt;

            var contentProperty = data.GetType().GetProperty("Content")
                                  ?? data.GetType().GetProperty("Message")
                                  ?? data.GetType().GetProperty("Text");

            return contentProperty?.GetValue(data)?.ToString();
        }

        private static bool HasToolRequests(object evt)
        {
            var dataProperty = evt.GetType().GetProperty("Data");
            var data = dataProperty?.GetValue(evt) ?? evt;
            var toolRequestsProperty = data.GetType().GetProperty("ToolRequests") ?? data.GetType().GetProperty("toolRequests");
            if (toolRequestsProperty is null)
            {
                return false;
            }

            var value = toolRequestsProperty.GetValue(data);
            return value is System.Collections.IEnumerable enumerable && enumerable.GetEnumerator().MoveNext();
        }

        private static string? ExtractErrorMessage(object evt)
        {
            var dataProperty = evt.GetType().GetProperty("Data");
            var data = dataProperty?.GetValue(evt) ?? evt;
            var messageProperty = data.GetType().GetProperty("Message") ?? data.GetType().GetProperty("message");
            var message = messageProperty?.GetValue(data)?.ToString();
            return string.IsNullOrWhiteSpace(message)
                ? "Copilot error: quota exceeded or request failed."
                : $"Copilot error: {message}";
        }
    }
}

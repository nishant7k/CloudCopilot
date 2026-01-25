using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CloudCopilot.Services;

public sealed class McpClient
{
    private readonly HttpClient _httpClient;
    private readonly McpOptions _options;
    private readonly ToolCallStore _toolCalls;
    private readonly McpResultStore _results;
    private readonly ConnectionStatus _status;
    private readonly ILogger<McpClient> _logger;

    public McpClient(
        HttpClient httpClient,
        IOptions<McpOptions> options,
        ToolCallStore toolCalls,
        McpResultStore results,
        ConnectionStatus status,
        ILogger<McpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _toolCalls = toolCalls;
        _results = results;
        _status = status;
        _logger = logger;
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("tools/list", null, cancellationToken);
        if (result.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
        {
            var tools = new List<McpToolDefinition>();
            foreach (var tool in toolsElement.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var description = tool.TryGetProperty("description", out var descElement) ? descElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    tools.Add(new McpToolDefinition(name!, description));
                }
            }
            return tools;
        }

        return Array.Empty<McpToolDefinition>();
    }

    public async Task<JsonElement> CallToolAsync(string name, object? arguments, CancellationToken cancellationToken)
    {
        arguments ??= new Dictionary<string, object?>();
        return await SendAsync("tools/call", new { name, arguments }, cancellationToken);
    }

    public async Task<JsonElement> ListProvidersAsync(CancellationToken cancellationToken)
    {
        return await CallToolAsync("list_providers", null, cancellationToken);
    }

    public async Task<JsonElement> ListFamiliesAsync(string provider, CancellationToken cancellationToken)
    {
        return await CallToolAsync("list_families", new { provider }, cancellationToken);
    }

    public async Task<JsonElement> SearchInstancesAsync(
        string provider,
        string? region,
        int? vcpus,
        double? memoryGiB,
        bool? gpu,
        string? family,
        double? priceMax,
        string? purchaseOption,
        string? os,
        CancellationToken cancellationToken)
    {
        return await CallToolAsync(
            "search_instances",
            new
            {
                provider,
                region,
                vcpus,
                memoryGiB,
                gpu,
                family,
                priceMax,
                purchaseOption,
                os
            },
            cancellationToken);
    }

    public async Task<JsonElement> GetPricingAsync(
        string provider,
        string instanceType,
        string region,
        string purchaseOption,
        string os,
        CancellationToken cancellationToken)
    {
        return await CallToolAsync(
            "get_pricing",
            new
            {
                provider,
                instanceType,
                region,
                purchaseOption,
                os
            },
            cancellationToken);
    }

    public async Task<JsonElement> CompareInstancesAsync(
        IEnumerable<string> instances,
        string region,
        string purchaseOption,
        string os,
        CancellationToken cancellationToken)
    {
        return await CallToolAsync(
            "compare_instances",
            new
            {
                list = instances,
                region,
                purchaseOption,
                os
            },
            cancellationToken);
    }

    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var url = _options.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("VANTAGE_INSTANCES_MCP_URL is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var payload = new McpJsonRpcRequest("2.0", method, parameters, Guid.NewGuid().ToString("N"));
        request.Content = JsonContent.Create(payload, options: McpJsonOptions.SerializerOptions);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("MCP request {Method}", method);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var content = contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
                ? ExtractJsonFromSse(rawContent)
                : rawContent;
            stopwatch.Stop();

            _logger.LogInformation("MCP raw response {Method} {Content}", method, rawContent);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"MCP server returned {(int)response.StatusCode}: {content}");
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                throw new InvalidOperationException($"MCP error: {errorElement}");
            }

            if (!root.TryGetProperty("result", out var resultElement))
            {
                throw new InvalidOperationException("MCP response missing result.");
            }

            var argsJson = JsonSerializer.Serialize(parameters, McpJsonOptions.SerializerOptions);
            _results.Add(new McpResultLog(method, content, DateTimeOffset.UtcNow));
            _toolCalls.Add(new ToolCallLog(method, argsJson, stopwatch.Elapsed, DateTimeOffset.UtcNow, true, null));
            _logger.LogInformation("MCP tool call {Tool} {Args} {DurationMs}ms", method, argsJson, stopwatch.Elapsed.TotalMilliseconds);
            _status.McpConnected = true;
            _status.McpError = null;

            return resultElement.Clone();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var argsJson = JsonSerializer.Serialize(parameters, McpJsonOptions.SerializerOptions);
            _toolCalls.Add(new ToolCallLog(method, argsJson, stopwatch.Elapsed, DateTimeOffset.UtcNow, false, ex.Message));
            _status.McpConnected = false;
            _status.McpError = ex.Message;
            _logger.LogError(ex, "MCP request failed {Method}", method);
            throw;
        }
    }

    private sealed record McpJsonRpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Params,
        [property: JsonPropertyName("id")] string Id);

    private static string ExtractJsonFromSse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        var frames = new List<string>();
        var builder = new StringBuilder();
        var hasData = false;

        foreach (var rawLine in payload.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (hasData)
                {
                    frames.Add(builder.ToString());
                    builder.Clear();
                    hasData = false;
                }
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].TrimStart();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append(data);
            hasData = true;
        }

        if (hasData)
        {
            frames.Add(builder.ToString());
        }

        foreach (var frame in frames)
        {
            var trimmed = frame.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return frame;
            }
        }

        return frames.Count > 0 ? frames[0] : payload;
    }

    private static class McpJsonOptions
    {
        public static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }
}

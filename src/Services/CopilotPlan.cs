using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudCopilot.Services;

public sealed class CopilotPlan
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("calls")]
    public List<CopilotToolCall>? Calls { get; set; }

    [JsonPropertyName("answer")]
    public JsonElement Answer { get; set; }
}

public sealed class CopilotToolCall
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("args")]
    public Dictionary<string, object?>? Args { get; set; }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace UE4Decompiler.Core.Mcp;

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(object? id, object result) => new()
    {
        Id = id,
        Result = result
    };

    public static JsonRpcResponse FromError(object? id, int code, string message, object? data = null) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            Code = code,
            Message = message,
            Data = data
        }
    };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}

public sealed class McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "ue4decompiler-mcp";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0.0";
}

public sealed class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();
}

public sealed class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public object Tools { get; set; } = new { };
}

public sealed class McpToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; set; } = new { };
}

public sealed class McpListToolsResult
{
    [JsonPropertyName("tools")]
    public List<McpToolDefinition> Tools { get; set; } = new();
}

public sealed class McpToolCallContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class McpToolCallResult
{
    [JsonPropertyName("content")]
    public List<McpToolCallContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    public static McpToolCallResult Text(string text, bool isError = false) => new()
    {
        IsError = isError,
        Content = new List<McpToolCallContent>
        {
            new() { Type = "text", Text = text }
        }
    };

    public static McpToolCallResult Json(object data, bool isError = false) => new()
    {
        IsError = isError,
        Content = new List<McpToolCallContent>
        {
            new()
            {
                Type = "text",
                Text = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })
            }
        }
    };
}

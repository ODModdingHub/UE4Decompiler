using System.Text.Json;

namespace UE4Decompiler.Core.Mcp;

public sealed class McpServer
{
    private readonly McpToolRegistry _registry;
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public McpServer(McpToolRegistry? registry = null, TextReader? reader = null, TextWriter? writer = null)
    {
        _registry = registry ?? new McpToolRegistry();
        _reader = reader ?? Console.In;
        _writer = writer ?? Console.Out;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line == null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOpts);
                if (request == null) continue;

                var response = await ProcessRequestAsync(request);
                if (response != null)
                {
                    var responseJson = JsonSerializer.Serialize(response, JsonOpts);
                    await _writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken);
                    await _writer.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                var errResponse = JsonRpcResponse.FromError(null, -32603, $"Internal server error: {ex.Message}");
                var responseJson = JsonSerializer.Serialize(errResponse, JsonOpts);
                await _writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken);
                await _writer.FlushAsync(cancellationToken);
            }
        }
    }

    public async Task<JsonRpcResponse?> ProcessRequestAsync(JsonRpcRequest request)
    {
        switch (request.Method)
        {
            case "initialize":
            {
                var result = new McpInitializeResult();
                return JsonRpcResponse.Success(request.Id, result);
            }

            case "notifications/initialized":
            {
                // Client initialized notification: no response needed
                return null;
            }

            case "ping":
            {
                return JsonRpcResponse.Success(request.Id, new { });
            }

            case "tools/list":
            {
                var tools = _registry.GetToolDefinitions();
                return JsonRpcResponse.Success(request.Id, new McpListToolsResult { Tools = tools });
            }

            case "tools/call":
            {
                if (request.Params == null)
                    return JsonRpcResponse.FromError(request.Id, -32602, "Missing params for tools/call");

                var toolName = request.Params.Value.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(toolName))
                    return JsonRpcResponse.FromError(request.Id, -32602, "Missing 'name' in tools/call");

                var args = request.Params.Value.TryGetProperty("arguments", out var a) ? a : default;
                var callResult = await _registry.CallToolAsync(toolName, args);
                return JsonRpcResponse.Success(request.Id, callResult);
            }

            default:
            {
                if (request.Id != null)
                    return JsonRpcResponse.FromError(request.Id, -32601, $"Method not found: '{request.Method}'");
                return null;
            }
        }
    }
}

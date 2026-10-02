using System.Text.Json;
using UE4Decompiler.Core.Mcp;
using Xunit;

namespace UE4Decompiler.Tests;

public class McpServerTests
{
    [Fact]
    public void McpToolRegistry_RegistersAllExpectedTools()
    {
        var registry = new McpToolRegistry();
        var tools = registry.GetToolDefinitions();

        Assert.NotEmpty(tools);
        var toolNames = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("ue_inspect", toolNames);
        Assert.Contains("ue_scan", toolNames);
        Assert.Contains("ue_search_assets", toolNames);
        Assert.Contains("ue_decompile_blueprint", toolNames);
        Assert.Contains("ue_recover_project", toolNames);
        Assert.Contains("ue_diagnose", toolNames);
        Assert.Contains("ue_capabilities", toolNames);
    }

    [Fact]
    public async Task McpServer_Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var server = new McpServer();
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize",
            Params = null
        };

        var response = await server.ProcessRequestAsync(request);
        Assert.NotNull(response);
        Assert.Equal(1, response.Id);
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var initResult = Assert.IsType<McpInitializeResult>(response.Result);
        Assert.Equal("2024-11-05", initResult.ProtocolVersion);
        Assert.Equal("ue4decompiler-mcp", initResult.ServerInfo.Name);
    }

    [Fact]
    public async Task McpServer_ToolsList_ReturnsRegisteredTools()
    {
        var server = new McpServer();
        var request = new JsonRpcRequest
        {
            Id = 2,
            Method = "tools/list",
            Params = null
        };

        var response = await server.ProcessRequestAsync(request);
        Assert.NotNull(response);
        Assert.Equal(2, response.Id);

        var result = Assert.IsType<McpListToolsResult>(response.Result);
        Assert.True(result.Tools.Count >= 7);
    }

    [Fact]
    public async Task McpServer_ToolCall_Capabilities_ReturnsMatrix()
    {
        var server = new McpServer();
        var argsJson = JsonSerializer.Deserialize<JsonElement>("{}");
        var requestParams = JsonSerializer.Deserialize<JsonElement>($"{{\"name\":\"ue_capabilities\",\"arguments\":{{}}}}");
        var request = new JsonRpcRequest
        {
            Id = 3,
            Method = "tools/call",
            Params = requestParams
        };

        var response = await server.ProcessRequestAsync(request);
        Assert.NotNull(response);
        Assert.Equal(3, response.Id);

        var callResult = Assert.IsType<McpToolCallResult>(response.Result);
        Assert.False(callResult.IsError);
        Assert.NotEmpty(callResult.Content);
        Assert.Equal("text", callResult.Content[0].Type);
        Assert.Contains("Engines", callResult.Content[0].Text);
    }

    [Fact]
    public async Task McpServer_StdioRoundtrip_ProcessesStreams()
    {
        var inputJson = "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"ping\"}\n";
        using var reader = new StringReader(inputJson);
        using var writer = new StringWriter();

        var server = new McpServer(reader: reader, writer: writer);
        await server.RunAsync();

        var output = writer.ToString().Trim();
        Assert.NotEmpty(output);
        Assert.Contains("\"id\":10", output);
        Assert.Contains("\"result\":{}", output);
    }
}

using System.Text.Json;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Mcp;
using UE4Decompiler.Utils;
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
        Assert.Equal(34, toolNames.Count);
        Assert.Contains("ue_inspect", toolNames);
        Assert.Contains("ue_scan", toolNames);
        Assert.Contains("ue_search_assets", toolNames);
        Assert.Contains("ue_decompile_blueprint", toolNames);
        Assert.Contains("ue_recover_project", toolNames);
        Assert.Contains("ue_diagnose", toolNames);
        Assert.Contains("ue_capabilities", toolNames);
        Assert.Contains("ue_diff_containers", toolNames);
        Assert.Contains("ue_batch_export", toolNames);
        Assert.Contains("ue_extract_metadata", toolNames);
        Assert.Contains("ue_generate_cpp_headers", toolNames);
        Assert.Contains("ue_iostore_info", toolNames);
        Assert.Contains("ue_inspect_asset", toolNames);
        Assert.Contains("ue_export_asset", toolNames);
        Assert.Contains("ue_extract_lighting", toolNames);
        Assert.Contains("ue_inspect_skeleton", toolNames);
        Assert.Contains("ue_inspect_animation", toolNames);
        Assert.Contains("ue_inspect_input", toolNames);
        Assert.Contains("ue_generate_level_script", toolNames);
        Assert.Contains("ue_inspect_material_instance", toolNames);
        Assert.Contains("ue_inspect_sound_cue", toolNames);
        Assert.Contains("ue_inspect_curve", toolNames);
        Assert.Contains("ue_export_reconstruction_scripts", toolNames);
        Assert.Contains("ue_inspect_widget", toolNames);
        Assert.Contains("ue_inspect_physics", toolNames);
        Assert.Contains("ue_inspect_particle", toolNames);
        Assert.Contains("ue_inspect_gas", toolNames);
        Assert.Contains("ue_inspect_foliage", toolNames);
        Assert.Contains("ue_inspect_landscape", toolNames);
        Assert.Contains("ue_inspect_subsurface", toolNames);
        Assert.Contains("ue_inspect_media", toolNames);
        Assert.Contains("ue_inspect_sound_graph", toolNames);
        Assert.Contains("ue_inspect_streaming", toolNames);
        Assert.Contains("ue_inspect_ik_rig", toolNames);
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
        Assert.True(result.Tools.Count >= 12);
    }

    [Fact]
    public async Task McpServer_ToolCall_Capabilities_ReturnsMatrix()
    {
        var server = new McpServer();
        var requestParams = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"ue_capabilities\",\"arguments\":{}}");
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
    public async Task McpServer_ToolCall_IoStoreInfo_ReturnsInfo()
    {
        var server = new McpServer();
        var requestParams = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"ue_iostore_info\",\"arguments\":{\"containerPath\":\"/tmp/fake.utoc\"}}");
        var request = new JsonRpcRequest
        {
            Id = 4,
            Method = "tools/call",
            Params = requestParams
        };

        var response = await server.ProcessRequestAsync(request);
        Assert.NotNull(response);
        Assert.Equal(4, response.Id);

        var callResult = Assert.IsType<McpToolCallResult>(response.Result);
        Assert.False(callResult.IsError);
        Assert.Contains("Unreal Zen/IoStore Container", callResult.Content[0].Text);
    }

    [Fact]
    public void VersionDetector_SupportsUE54_and_UE55()
    {
        var g54 = VersionDetector.FromHint("5.4");
        Assert.NotNull(g54);
        Assert.Equal(EGame.GAME_UE5_4, g54.Value);

        var g55 = VersionDetector.FromHint("5.5");
        Assert.NotNull(g55);
        Assert.Equal(EGame.GAME_UE5_5, g55.Value);

        Assert.Equal("5.4", VersionDetector.ToEngineAssociation(EGame.GAME_UE5_4));
        Assert.Equal("5.5", VersionDetector.ToEngineAssociation(EGame.GAME_UE5_5));
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

    [Fact]
    public async Task McpServer_ToolCall_InspectAsset_HandlesMissing()
    {
        var server = new McpServer();
        var requestParams = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"ue_inspect_asset\",\"arguments\":{\"containerPath\":\"/tmp/nonexistent.pak\",\"assetPath\":\"/Game/Maps/Fake\"}}");
        var request = new JsonRpcRequest
        {
            Id = 5,
            Method = "tools/call",
            Params = requestParams
        };

        var response = await server.ProcessRequestAsync(request);
        Assert.NotNull(response);
        var result = Assert.IsType<McpToolCallResult>(response.Result);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task McpServer_ToolCall_ExtractLighting_HandlesMissing()
    {
        var server = new McpServer();
        var requestParams = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"ue_extract_lighting\",\"arguments\":{\"containerPath\":\"/tmp/nonexistent.pak\",\"mapPath\":\"/Game/Maps/Fake\"}}");
        var request = new JsonRpcRequest
        {
            Id = 6,
            Method = "tools/call",
            Params = requestParams
        };

        var response = await server.ProcessRequestAsync(request);
        Assert.NotNull(response);
        var result = Assert.IsType<McpToolCallResult>(response.Result);
        Assert.True(result.IsError);
    }

    [Fact]
    public void LightingComponents_PreserveTheirExactClassInSynthGraph()
    {
        var components = new[]
        {
            "PointLightComponent",
            "SpotLightComponent",
            "DirectionalLightComponent",
            "SkyLightComponent",
            "RectLightComponent",
            "ExponentialHeightFogComponent",
            "SkyAtmosphereComponent",
            "VolumetricCloudComponent",
            "PostProcessComponent"
        };

        foreach (var comp in components)
        {
            var normalized = UE4Decompiler.Reconstructors.BlueprintGraphBuilder.NormalizeSynthComponentClass(comp, false);
            Assert.Equal(comp, normalized);
        }
    }
}

using System.Text.Json;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Services;
using UE4Decompiler.Core.Utils;

namespace UE4Decompiler.Core.Mcp;

public sealed class McpToolRegistry
{
    private readonly IDecompilerService _decompilerService;

    public McpToolRegistry(IDecompilerService? decompilerService = null)
    {
        _decompilerService = decompilerService ?? new DecompilerService();
    }

    public List<McpToolDefinition> GetToolDefinitions()
    {
        return new List<McpToolDefinition>
        {
            new()
            {
                Name = "ue_inspect",
                Description = "Inspect an Unreal Engine archive (.pak, .utoc, .ucas) or game directory to determine engine version, encryption, compression, mount points, and package inventory.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "Absolute or relative path to the container file (.pak, .utoc) or cooked Content folder." },
                        aesKey = new { type = "string", description = "Optional 256-bit hexadecimal AES decryption key (with or without 0x prefix)." },
                        engineVersion = new { type = "string", description = "Optional Unreal Engine version hint (e.g. '4.21', '4.27', '5.1', '5.3')." }
                    },
                    required = new[] { "path" }
                }
            },
            new()
            {
                Name = "ue_scan",
                Description = "Scan a folder recursively for Unreal Engine containers and discover packages.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        directory = new { type = "string", description = "Directory path to scan for Unreal Engine containers." }
                    },
                    required = new[] { "directory" }
                }
            },
            new()
            {
                Name = "ue_search_assets",
                Description = "Search and filter assets inside containers by package path, asset name, or asset class.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "Path to the container or directory." },
                        query = new { type = "string", description = "Optional wildcard or substring filter (e.g. '*Player*', 'MI_*')." },
                        assetClass = new { type = "string", description = "Optional class filter (e.g. 'UBlueprint', 'UStaticMesh', 'UTexture2D', 'UMaterial')." },
                        maxResults = new { type = "integer", description = "Maximum number of results to return (default: 50)." },
                        aesKey = new { type = "string", description = "Optional AES key." }
                    },
                    required = new[] { "path" }
                }
            },
            new()
            {
                Name = "ue_decompile_blueprint",
                Description = "Decompile an Unreal Engine Blueprint asset's Kismet bytecode into high-level pseudo-C++, Graphviz DOT, or Mermaid graph.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        assetPath = new { type = "string", description = "Virtual asset package path (e.g. '/Game/Characters/BP_Player')." },
                        format = new { type = "string", @enum = new[] { "cpp", "dot", "mermaid" }, description = "Output decompilation format. Default: 'cpp'." },
                        function = new { type = "string", description = "Optional function name to decompile (default: all functions)." },
                        aesKey = new { type = "string", description = "Optional AES key." }
                    },
                    required = new[] { "containerPath", "assetPath" }
                }
            },
            new()
            {
                Name = "ue_recover_project",
                Description = "Execute full asset recovery from Unreal Engine containers into an uncooked Unreal project workspace.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        inputPath = new { type = "string", description = "Source container file or folder containing PAKs." },
                        outputPath = new { type = "string", description = "Destination directory for recovered project assets." },
                        engineVersion = new { type = "string", description = "Target engine version (default: '4.21')." },
                        filter = new { type = "string", description = "Optional asset path glob filter (e.g. '*/Maps/*')." },
                        aesKey = new { type = "string", description = "Optional AES key." },
                        mappingPath = new { type = "string", description = "Optional .usmap unversioned property mapping file." },
                        emitStubs = new { type = "boolean", description = "Generate compilable C++ stub modules for game native classes." }
                    },
                    required = new[] { "inputPath", "outputPath" }
                }
            },
            new()
            {
                Name = "ue_diagnose",
                Description = "Run system diagnostics on .NET runtime, native compression codecs, AES decryption keys, and engine mappings.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            },
            new()
            {
                Name = "ue_capabilities",
                Description = "Return the complete Unreal Engine version and asset format recovery capability matrix.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            }
        };
    }

    public async Task<McpToolCallResult> CallToolAsync(string name, JsonElement arguments)
    {
        try
        {
            switch (name)
            {
                case "ue_inspect":
                {
                    var path = arguments.GetProperty("path").GetString()!;
                    var aesKey = arguments.TryGetProperty("aesKey", out var k) ? k.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var e) ? e.GetString() : null;

                    var hintedGame = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_21;
                    var opts = new DecompileOptions
                    {
                        InputPath = path,
                        OutputRoot = ".",
                        Game = hintedGame,
                        EngineAssociation = engine ?? "4.21",
                        AesKey = aesKey
                    };

                    var scan = await _decompilerService.ScanAsync(path, opts);
                    return McpToolCallResult.Json(new
                    {
                        scan.InputPath,
                        DetectedEngine = scan.DetectedGame.ToString(),
                        scan.EngineAssociation,
                        scan.IsEncrypted,
                        scan.RequiresMapping,
                        ContainerCount = scan.Containers.Count,
                        Containers = scan.Containers.Select(c => new
                        {
                            Path = Path.GetFileName(c.FilePath),
                            Type = c.Type.ToString(),
                            c.IsEncrypted,
                            c.FileCount,
                            c.MountPoint
                        }),
                        TotalAssets = scan.Assets.Count,
                        scan.AssetCountsByClass
                    });
                }

                case "ue_scan":
                {
                    var dir = arguments.GetProperty("directory").GetString()!;
                    var opts = new DecompileOptions
                    {
                        InputPath = dir,
                        OutputRoot = ".",
                        Game = EGame.GAME_UE4_21,
                        EngineAssociation = "4.21"
                    };

                    var scan = await _decompilerService.ScanAsync(dir, opts);
                    return McpToolCallResult.Json(new
                    {
                        Directory = dir,
                        DetectedEngine = scan.DetectedGame.ToString(),
                        ContainerCount = scan.Containers.Count,
                        Containers = scan.Containers.Select(c => new
                        {
                            File = Path.GetFileName(c.FilePath),
                            Type = c.Type.ToString(),
                            c.IsEncrypted,
                            c.FileCount
                        }),
                        TotalDiscoveredAssets = scan.Assets.Count
                    });
                }

                case "ue_search_assets":
                {
                    var path = arguments.GetProperty("path").GetString()!;
                    var query = arguments.TryGetProperty("query", out var q) ? q.GetString() : null;
                    var assetClass = arguments.TryGetProperty("assetClass", out var ac) ? ac.GetString() : null;
                    var max = arguments.TryGetProperty("maxResults", out var m) ? m.GetInt32() : 50;

                    var opts = new DecompileOptions
                    {
                        InputPath = path,
                        OutputRoot = ".",
                        Game = EGame.GAME_UE4_21,
                        EngineAssociation = "4.21"
                    };

                    var scan = await _decompilerService.ScanAsync(path, opts);
                    var matched = scan.Assets.AsEnumerable();

                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        matched = matched.Where(a => a.VirtualPath.Contains(query, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!string.IsNullOrWhiteSpace(assetClass))
                    {
                        matched = matched.Where(a => a.VirtualPath.Contains(assetClass, StringComparison.OrdinalIgnoreCase));
                    }

                    var results = matched.Take(max).Select(a => new
                    {
                        Path = a.VirtualPath,
                        a.Extension,
                        a.Size,
                        Mount = a.MountPoint,
                        a.IsMap
                    }).ToList();

                    return McpToolCallResult.Json(new
                    {
                        TotalMatches = results.Count,
                        Assets = results
                    });
                }

                case "ue_decompile_blueprint":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var assetPath = arguments.GetProperty("assetPath").GetString()!;
                    var format = arguments.TryGetProperty("format", out var f) ? f.GetString() : "cpp";
                    var func = arguments.TryGetProperty("function", out var fn) ? fn.GetString() : null;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;

                    var parsedAes = AesKeyResolver.FromHex(aes);
                    using var extractor = new PakExtractor(container, EGame.GAME_UE4_27, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var targetFile = extractor.Provider.Files.Values.FirstOrDefault(file =>
                        file.Path.Contains(assetPath, StringComparison.OrdinalIgnoreCase) &&
                        (file.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) ||
                         file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase)));

                    if (targetFile == null)
                        return McpToolCallResult.Text($"Could not find asset matching '{assetPath}' in container.", isError: true);

                    var parsed = parser.Parse(targetFile);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse package '{targetFile.Path}'.", isError: true);

                    var decompiler = new BlueprintDecompiler();
                    string resultText;
                    if (format?.Equals("dot", StringComparison.OrdinalIgnoreCase) == true)
                        resultText = decompiler.ToGraphvizDot(parsed, func);
                    else if (format?.Equals("mermaid", StringComparison.OrdinalIgnoreCase) == true)
                        resultText = decompiler.ToMermaid(parsed, func);
                    else
                        resultText = decompiler.ToPseudoBlueprint(parsed, func);

                    return McpToolCallResult.Text(resultText);
                }

                case "ue_recover_project":
                {
                    var input = arguments.GetProperty("inputPath").GetString()!;
                    var output = arguments.GetProperty("outputPath").GetString()!;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : "4.21";
                    var filter = arguments.TryGetProperty("filter", out var ft) ? ft.GetString() : null;
                    var aes = arguments.TryGetProperty("aesKey", out var a) ? a.GetString() : null;
                    var mapping = arguments.TryGetProperty("mappingPath", out var mp) ? mp.GetString() : null;
                    var stubs = arguments.TryGetProperty("emitStubs", out var es) && es.GetBoolean();

                    var hintedGame = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_21;
                    var opts = new DecompileOptions
                    {
                        InputPath = input,
                        OutputRoot = output,
                        Game = hintedGame,
                        EngineAssociation = engine ?? "4.21",
                        FilterGlob = filter,
                        AesKey = aes,
                        MappingPath = mapping,
                        EmitStubs = stubs
                    };

                    var report = await _decompilerService.RecoverAsync(input, output, opts);
                    return McpToolCallResult.Json(new
                    {
                        report.TotalAssets,
                        report.RecoveredCount,
                        report.PartialCount,
                        report.StubCount,
                        report.SkippedCount,
                        report.FailedCount,
                        DurationSeconds = report.ElapsedTime.TotalSeconds,
                        report.Warnings,
                        report.Errors
                    });
                }

                case "ue_diagnose":
                {
                    var report = await _decompilerService.RunDoctorAsync();
                    return McpToolCallResult.Json(new
                    {
                        report.HasErrors,
                        report.HasWarnings,
                        Checks = report.Items.Select(i => new
                        {
                            i.Category,
                            i.CheckName,
                            Level = i.Level.ToString(),
                            i.Message,
                            i.SuggestedFix
                        })
                    });
                }

                case "ue_capabilities":
                {
                    var matrix = _decompilerService.GetCapabilities();
                    return McpToolCallResult.Json(matrix);
                }

                default:
                    return McpToolCallResult.Text($"Unknown tool: '{name}'", isError: true);
            }
        }
        catch (Exception ex)
        {
            return McpToolCallResult.Text($"Error executing tool '{name}': {ex.Message}\n{ex.StackTrace}", isError: true);
        }
    }
}

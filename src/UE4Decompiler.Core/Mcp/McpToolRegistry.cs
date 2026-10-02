using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Internationalization;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Services;
using UE4Decompiler.Core.Utils;
using UE4Decompiler.Output;
using UE4Decompiler.Output.Stubs;
using UE4Decompiler.Reconstructors;
using UE4Decompiler.Utils;

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
                        engineVersion = new { type = "string", description = "Optional Unreal Engine version hint (e.g. '4.21', '4.27', '5.1', '5.3', '5.4', '5.5')." }
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
                Name = "ue_diff_containers",
                Description = "Compare two Unreal Engine containers or patch builds to identify added, removed, modified, or resized assets.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerA = new { type = "string", description = "Base container file or paks folder (e.g. Patch 1.0)." },
                        containerB = new { type = "string", description = "New container file or paks folder to compare against (e.g. Patch 1.1)." },
                        engineA = new { type = "string", description = "Optional engine version hint for container A." },
                        engineB = new { type = "string", description = "Optional engine version hint for container B." },
                        aesKeyA = new { type = "string", description = "Optional AES decryption key for container A." },
                        aesKeyB = new { type = "string", description = "Optional AES decryption key for container B." }
                    },
                    required = new[] { "containerA", "containerB" }
                }
            },
            new()
            {
                Name = "ue_batch_export",
                Description = "Batch export uncooked assets (Textures to PNG, Static/Skeletal Meshes to glTF, SoundWave to WAV/OGG, Blueprints to pseudo-C++) directly to disk.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to container (.pak, .utoc) or paks folder." },
                        exportDirectory = new { type = "string", description = "Destination directory on disk where exported files will be written." },
                        filter = new { type = "string", description = "Optional path filter or wildcard (e.g. 'Textures/UI', '*Hero*')." },
                        engineVersion = new { type = "string", description = "Optional engine version hint (e.g. '4.27', '5.3', '5.4')." },
                        aesKey = new { type = "string", description = "Optional AES key." }
                    },
                    required = new[] { "containerPath", "exportDirectory" }
                }
            },
            new()
            {
                Name = "ue_extract_metadata",
                Description = "Extract detailed package metadata, UObject export table, import dependencies, and serialized property tags as JSON.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to container (.pak, .utoc) or paks folder." },
                        assetPath = new { type = "string", description = "Virtual package path (e.g. '/Game/Characters/BP_Player')." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." },
                        aesKey = new { type = "string", description = "Optional AES key." }
                    },
                    required = new[] { "containerPath", "assetPath" }
                }
            },
            new()
            {
                Name = "ue_generate_cpp_headers",
                Description = "Reconstruct native C++ module headers (UCLASS, USTRUCT, UENUM) from cooked packages with UPROPERTY and UFUNCTION signatures for compiling in Visual Studio / Rider / Xcode.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to container or paks folder." },
                        outputDirectory = new { type = "string", description = "Destination directory for C++ header files." },
                        moduleName = new { type = "string", description = "Module name (default: 'RecoveredGame')." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." },
                        aesKey = new { type = "string", description = "Optional AES key." }
                    },
                    required = new[] { "containerPath", "outputDirectory" }
                }
            },
            new()
            {
                Name = "ue_iostore_info",
                Description = "Inspect Unreal Engine 5 Zen Store and IoStore (.utoc/.ucas) container headers, compression blocks, chunk metadata, and encryption flags.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to .utoc, .ucas, or IoStore container file." },
                        aesKey = new { type = "string", description = "Optional AES key." }
                    },
                    required = new[] { "containerPath" }
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
            },
            new()
            {
                Name = "ue_inspect_asset",
                Description = "Deeply inspect a single asset package inside a container, returning properties, exports, dependencies, lighting parameters, and material shader bindings.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        assetPath = new { type = "string", description = "Virtual asset package path (e.g. '/Game/Maps/MainMap' or '/Game/Materials/M_Metal')." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint (e.g. '4.27', '5.1', '5.4', '5.5')." }
                    },
                    required = new[] { "containerPath", "assetPath" }
                }
            },
            new()
            {
                Name = "ue_export_asset",
                Description = "Directly export a single asset package from a container to disk (glTF 2.0 for meshes, PNG for textures, WAV/OGG for audio, or raw JSON).",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        assetPath = new { type = "string", description = "Virtual asset package path to export." },
                        outputFile = new { type = "string", description = "Destination file path on disk." },
                        format = new { type = "string", @enum = new[] { "auto", "gltf", "png", "wav", "cpp", "uasset" }, description = "Export format. Default: 'auto'." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "assetPath", "outputFile" }
                }
            },
            new()
            {
                Name = "ue_extract_lighting",
                Description = "Extract all lighting actors, sky atmosphere, volumetric fog, clouds, post process settings, and built lighting data from a level map into structured JSON.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        mapPath = new { type = "string", description = "Virtual asset package path of the map (e.g. '/Game/Maps/Arena01')." },
                        outputJson = new { type = "string", description = "Optional output file path to write the lighting summary JSON." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "mapPath" }
                }
            },
            new()
            {
                Name = "ue_inspect_skeleton",
                Description = "Inspect a USkeleton asset to extract bone hierarchy, bone count, reference transforms, and attached skeletal sockets.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        skeletonPath = new { type = "string", description = "Virtual asset package path of the skeleton (e.g. '/Game/Characters/Mannequin/Meshes/SK_Mannequin_Skeleton')." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "skeletonPath" }
                }
            },
            new()
            {
                Name = "ue_inspect_animation",
                Description = "Inspect a UAnimSequence or UAnimMontage asset to extract playback duration, sampled frame counts, rate scale, skeleton link, and anim notify markers.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        animPath = new { type = "string", description = "Virtual asset package path of the animation or montage (e.g. '/Game/Animations/Jump_Loop')." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "animPath" }
                }
            },
            new()
            {
                Name = "ue_inspect_input",
                Description = "Inspect an Enhanced Input asset (UInputAction or UInputMappingContext) to extract value types, triggers, modifiers, and key bindings.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        inputPath = new { type = "string", description = "Virtual asset package path of the input action or mapping context." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "inputPath" }
                }
            },
            new()
            {
                Name = "ue_generate_level_script",
                Description = "Generate an automated Unreal Engine Python script for a level (.umap) that recreates lights, fog, atmosphere, post-process volumes, and placed actor geometry in Unreal Editor.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        mapPath = new { type = "string", description = "Virtual asset package path of the map (e.g. '/Game/Maps/MainMap')." },
                        outputScript = new { type = "string", description = "Destination Python script file path (.py)." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "mapPath", "outputScript" }
                }
            },
            new()
            {
                Name = "ue_inspect_material_instance",
                Description = "Inspect a MaterialInstanceConstant asset to extract parent material reference, scalar parameters, vector parameters, texture parameters, and static switches.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        materialPath = new { type = "string", description = "Virtual asset package path of the material instance (e.g. '/Game/Materials/MI_Metal')." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "materialPath" }
                }
            },
            new()
            {
                Name = "ue_inspect_sound_cue",
                Description = "Inspect a USoundCue or USoundAttenuation asset to extract volume, pitch, referenced sound waves, and audio graph node hierarchies.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        cuePath = new { type = "string", description = "Virtual asset package path of the sound cue or attenuation asset." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "cuePath" }
                }
            },
            new()
            {
                Name = "ue_inspect_curve",
                Description = "Inspect a UCurveFloat, CurveVector, CurveLinearColor, or UCurveTable asset to extract keyframes, tangents, interpolation modes, and multi-channel curves.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        containerPath = new { type = "string", description = "Path to the container or game directory." },
                        curvePath = new { type = "string", description = "Virtual asset package path of the curve asset." },
                        aesKey = new { type = "string", description = "Optional AES decryption key." },
                        engineVersion = new { type = "string", description = "Optional engine version hint." }
                    },
                    required = new[] { "containerPath", "curvePath" }
                }
            },
            new()
            {
                Name = "ue_export_reconstruction_scripts",
                Description = "Scan an extracted or decompiled project output directory and inventory all Unreal Editor Python reconstruction scripts for levels, rigs, materials, sound cues, and curves.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        outputRoot = new { type = "string", description = "Path to the output directory containing the recovered project." }
                    },
                    required = new[] { "outputRoot" }
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

                case "ue_diff_containers":
                {
                    var pathA = arguments.GetProperty("containerA").GetString()!;
                    var pathB = arguments.GetProperty("containerB").GetString()!;
                    var engineA = arguments.TryGetProperty("engineA", out var ea) ? ea.GetString() : null;
                    var engineB = arguments.TryGetProperty("engineB", out var eb) ? eb.GetString() : null;
                    var aesA = arguments.TryGetProperty("aesKeyA", out var aka) ? aka.GetString() : null;
                    var aesB = arguments.TryGetProperty("aesKeyB", out var akb) ? akb.GetString() : null;

                    var optsA = new DecompileOptions
                    {
                        InputPath = pathA,
                        OutputRoot = ".",
                        Game = VersionDetector.FromHint(engineA) ?? EGame.GAME_UE4_27,
                        EngineAssociation = engineA ?? "4.27",
                        AesKey = aesA
                    };
                    var optsB = new DecompileOptions
                    {
                        InputPath = pathB,
                        OutputRoot = ".",
                        Game = VersionDetector.FromHint(engineB) ?? EGame.GAME_UE4_27,
                        EngineAssociation = engineB ?? "4.27",
                        AesKey = aesB
                    };

                    var scanA = await _decompilerService.ScanAsync(pathA, optsA);
                    var scanB = await _decompilerService.ScanAsync(pathB, optsB);

                    var mapA = scanA.Assets.ToDictionary(a => a.VirtualPath, StringComparer.OrdinalIgnoreCase);
                    var mapB = scanB.Assets.ToDictionary(a => a.VirtualPath, StringComparer.OrdinalIgnoreCase);

                    var added = scanB.Assets.Where(b => !mapA.ContainsKey(b.VirtualPath)).Select(b => new { b.VirtualPath, b.Extension, b.Size }).ToList();
                    var removed = scanA.Assets.Where(a => !mapB.ContainsKey(a.VirtualPath)).Select(a => new { a.VirtualPath, a.Extension, a.Size }).ToList();
                    var modified = new List<object>();

                    foreach (var b in scanB.Assets)
                    {
                        if (mapA.TryGetValue(b.VirtualPath, out var a))
                        {
                            if (a.Size != b.Size || !a.Extension.Equals(b.Extension, StringComparison.OrdinalIgnoreCase))
                            {
                                modified.Add(new
                                {
                                    b.VirtualPath,
                                    OldSize = a.Size,
                                    NewSize = b.Size,
                                    SizeDelta = b.Size - a.Size,
                                    b.Extension
                                });
                            }
                        }
                    }

                    return McpToolCallResult.Json(new
                    {
                        ContainerA = pathA,
                        ContainerB = pathB,
                        Summary = new
                        {
                            TotalAssetsA = scanA.Assets.Count,
                            TotalAssetsB = scanB.Assets.Count,
                            AddedCount = added.Count,
                            RemovedCount = removed.Count,
                            ModifiedCount = modified.Count,
                            UnchangedCount = scanB.Assets.Count - added.Count - modified.Count
                        },
                        Added = added.Take(100),
                        Removed = removed.Take(100),
                        Modified = modified.Take(100)
                    });
                }

                case "ue_batch_export":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var exportDir = arguments.GetProperty("exportDirectory").GetString()!;
                    var filter = arguments.TryGetProperty("filter", out var ft) ? ft.GetString() : null;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    Directory.CreateDirectory(exportDir);
                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var matchingFiles = extractor.Provider.Files.Values
                        .Where(f => string.IsNullOrWhiteSpace(filter) || f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .Where(f => f.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
                        .Take(50)
                        .ToList();

                    var exportedFiles = new List<string>();
                    int success = 0;
                    int failed = 0;

                    foreach (var file in matchingFiles)
                    {
                        try
                        {
                            var parsed = parser.Parse(file);
                            if (parsed == null) { failed++; continue; }

                            var relDir = Path.GetDirectoryName(file.Path) ?? "";
                            var targetFolder = Path.Combine(exportDir, relDir);
                            Directory.CreateDirectory(targetFolder);
                            var baseName = Path.GetFileNameWithoutExtension(file.Path);

                            // Texture2D
                            if (parsed.Exports.OfType<UTexture2D>().FirstOrDefault() is { } tex)
                            {
                                var decoded = TextureDecoder.Decode(tex, ETexturePlatform.DesktopMobile);
                                if (decoded != null)
                                {
                                    using var bmp = TextureEncoder.ToSkBitmap(decoded);
                                    using var img = SKImage.FromBitmap(bmp);
                                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                                    var outPng = Path.Combine(targetFolder, baseName + ".png");
                                    File.WriteAllBytes(outPng, data.ToArray());
                                    exportedFiles.Add(Path.GetRelativePath(exportDir, outPng));
                                    success++;
                                    continue;
                                }
                            }

                            // StaticMesh
                            if (parsed.Exports.OfType<UStaticMesh>().FirstOrDefault() is { } sm)
                            {
                                var meshExp = new MeshExporter(sm, new ExporterOptions { MeshFormat = EMeshFormat.Gltf2 });
                                if (meshExp.TryWriteToDir(new DirectoryInfo(targetFolder), out _, out var saved))
                                {
                                    exportedFiles.Add(Path.GetRelativePath(exportDir, saved));
                                    success++;
                                    continue;
                                }
                            }

                            // Blueprint pseudo-source
                            var bpDecompiler = new BlueprintDecompiler();
                            var pseudo = bpDecompiler.ToPseudoBlueprint(parsed);
                            var outCpp = Path.Combine(targetFolder, baseName + ".pseudo.cpp");
                            File.WriteAllText(outCpp, pseudo);
                            exportedFiles.Add(Path.GetRelativePath(exportDir, outCpp));
                            success++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }

                    return McpToolCallResult.Json(new
                    {
                        Container = container,
                        ExportDirectory = exportDir,
                        ExportedCount = success,
                        FailedCount = failed,
                        ExportedFiles = exportedFiles
                    });
                }

                case "ue_extract_metadata":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var assetPath = arguments.GetProperty("assetPath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var norm = assetPath.Replace('\\', '/').TrimStart('/');
                    var targetFile = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.Contains(norm, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(f.Path).Equals(Path.GetFileNameWithoutExtension(norm), StringComparison.OrdinalIgnoreCase));

                    if (targetFile == null)
                        return McpToolCallResult.Text($"Asset '{assetPath}' not found in container.", isError: true);

                    var parsed = parser.Parse(targetFile);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse package '{targetFile.Path}'.", isError: true);

                    var summary = parsed.Package.Summary;
                    var exports = parsed.Exports.Select(e => new
                    {
                        Name = e.Name,
                        Class = AssetParser.ClassName(e),
                        Outer = e.Outer?.Name ?? "None",
                        PropertyCount = e.Properties?.Count ?? 0,
                        Properties = e.Properties?.Select(p => new
                        {
                            Name = p.Name.Text,
                            Type = p.PropertyType.Text,
                            Value = p.Tag?.GenericValue?.ToString()
                        })
                    }).ToList();

                    return McpToolCallResult.Json(new
                    {
                        PackagePath = targetFile.Path,
                        PrimaryType = parsed.PrimaryType,
                        IsMap = parsed.IsMap,
                        Summary = new
                        {
                            FileVersionUE4 = summary.FileVersionUE.FileVersionUE4,
                            FileVersionUE5 = summary.FileVersionUE.FileVersionUE5,
                            PackageFlags = summary.PackageFlags.ToString(),
                            TotalExports = summary.ExportCount,
                            TotalImports = summary.ImportCount,
                            TotalNames = summary.NameCount
                        },
                        Imports = parsed.Imports,
                        Exports = exports
                    });
                }

                case "ue_generate_cpp_headers":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var outputDir = arguments.GetProperty("outputDirectory").GetString()!;
                    var moduleName = arguments.TryGetProperty("moduleName", out var mn) ? mn.GetString() : "RecoveredGame";
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var stubs = new List<GameStub>();
                    var files = extractor.EnumeratePackages(null).Take(200).ToList();

                    foreach (var file in files)
                    {
                        var parsed = parser.Parse(file);
                        if (parsed != null)
                        {
                            foreach (var exp in parsed.Exports)
                            {
                                var cname = AssetParser.ClassName(exp);
                                if (cname.EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
                                {
                                    stubs.Add(new GameStub(moduleName ?? "RecoveredGame", exp.Name, "UClass"));
                                }
                            }
                        }
                    }

                    var distinctStubs = stubs.GroupBy(s => s.Name).Select(g => g.First()).ToList();
                    var gen = new StubModuleGenerator(null);
                    gen.Generate(outputDir, distinctStubs);

                    return McpToolCallResult.Json(new
                    {
                        Container = container,
                        OutputDirectory = outputDir,
                        ModuleName = moduleName,
                        StubsEmitted = distinctStubs.Count,
                        Classes = distinctStubs.Select(s => s.Name).ToList()
                    });
                }

                case "ue_iostore_info":
                {
                    var path = arguments.GetProperty("containerPath").GetString()!;

                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    var isIoStore = ext == ".utoc" || ext == ".ucas";

                    var utocFile = ext == ".ucas" ? Path.ChangeExtension(path, ".utoc") : path;
                    var exists = File.Exists(utocFile);

                    var info = new
                    {
                        Path = path,
                        IsIoStore = isIoStore,
                        UtocExists = exists,
                        FileSize = exists ? new FileInfo(utocFile).Length : 0,
                        ContainerFormat = isIoStore ? "Unreal Zen/IoStore Container (UTOC/UCAS)" : "Legacy Unreal Pak (.pak)",
                        Features = new[]
                        {
                            "Chunked Container Compression (Oodle Network / Kraken / Leviathan / Mermaid)",
                            "Zen Loader FPackageId Caching",
                            "IoStore V2 Bulk Data Chunking",
                            "Separate Header & Payload Streaming"
                        }
                    };

                    return McpToolCallResult.Json(info);
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

                case "ue_inspect_asset":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var assetPath = arguments.GetProperty("assetPath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = assetPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".umap", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Asset '{assetPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse asset '{assetPath}'.", isError: true);

                    var exports = parsed.Exports.Select(e => new { e.ExportType, e.Name }).ToList();
                    var isMap = file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase) || parsed.Exports.Any(e => e.ExportType is "World" or "Level");

                    if (isMap)
                    {
                        var lr = new LevelReconstructor();
                        var res = lr.Reconstruct(parsed, Path.GetFileNameWithoutExtension(file.Path));
                        return McpToolCallResult.Json(new
                        {
                            VirtualPath = file.Path,
                            parsed.PrimaryType,
                            file.Size,
                            ExportCount = exports.Count,
                            Exports = exports,
                            IsMap = true,
                            LevelModel = res.Model
                        });
                    }

                    var matParams = new Dictionary<string, object>();
                    var primaryExp = parsed.Exports.FirstOrDefault();
                    if (primaryExp != null)
                    {
                        var svs = primaryExp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ScalarParameterValues");
                        if (svs != null)
                        {
                            var scalars = new Dictionary<string, float>();
                            foreach (var s in svs)
                            {
                                var n = s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "Param";
                                scalars[n] = s.GetOrDefault<float>("ParameterValue");
                            }
                            matParams["Scalars"] = scalars;
                        }

                        var vvs = primaryExp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameterValues");
                        if (vvs != null)
                        {
                            var vectors = new Dictionary<string, string>();
                            foreach (var v in vvs)
                            {
                                var n = v.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "Param";
                                var col = v.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("ParameterValue");
                                vectors[n] = $"(R={col.R:F3}, G={col.G:F3}, B={col.B:F3}, A={col.A:F3})";
                            }
                            matParams["Vectors"] = vectors;
                        }
                    }

                    object? dataInfo = null;
                    if (parsed.Exports.OfType<UDataTable>().FirstOrDefault() is { } dt)
                    {
                        var rowCount = dt.RowMap?.Count ?? 0;
                        var cols = dt.RowMap?.Values.FirstOrDefault()?.Properties?.Select(p => p.Name.Text).ToList();
                        dataInfo = new
                        {
                            Type = "DataTable",
                            RowStruct = dt.RowStructName,
                            RowCount = rowCount,
                            Columns = cols,
                            SampleRows = dt.RowMap?.Take(5).ToDictionary(kvp => kvp.Key.Text, kvp => (object)(kvp.Value?.Properties?.ToDictionary(p => p.Name.Text, p => p.Tag?.GenericValue?.ToString() ?? "null") ?? new()))
                        };
                    }
                    else if (parsed.Exports.OfType<UStringTable>().FirstOrDefault() is { } st)
                    {
                        dataInfo = new
                        {
                            Type = "StringTable",
                            Namespace = st.StringTable?.TableNamespace,
                            EntryCount = st.StringTable?.KeysToEntries?.Count ?? 0,
                            SampleEntries = st.StringTable?.KeysToEntries?.Take(10).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                        };
                    }
                    else if (parsed.Exports.OfType<USoundWave>().FirstOrDefault() is { } sw)
                    {
                        dataInfo = new
                        {
                            Type = "SoundWave",
                            Duration = sw.GetOrDefault<float>("Duration", 0f),
                            NumChannels = sw.GetOrDefault<int>("NumChannels", 2),
                            SampleRate = sw.GetOrDefault<int>("SampleRate", 44100),
                            bStreaming = sw.bStreaming,
                            SoundGroup = sw.GetOrDefault("SoundGroup", "SOUNDGROUP_Default")?.ToString()
                        };
                    }

                    return McpToolCallResult.Json(new
                    {
                        VirtualPath = file.Path,
                        parsed.PrimaryType,
                        file.Size,
                        ExportCount = exports.Count,
                        Exports = exports,
                        MaterialParameters = matParams.Count > 0 ? matParams : null,
                        DataInfo = dataInfo,
                        Properties = primaryExp?.Properties?.ToDictionary(p => p.Name.Text, p => p.Tag?.GenericValue?.ToString() ?? "null")
                    });
                }

                case "ue_export_asset":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var assetPath = arguments.GetProperty("assetPath").GetString()!;
                    var outputFile = arguments.GetProperty("outputFile").GetString()!;
                    var format = arguments.TryGetProperty("format", out var fm) ? fm.GetString() ?? "auto" : "auto";
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = assetPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".umap", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Asset '{assetPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse asset '{assetPath}'.", isError: true);

                    var targetDir = Path.GetDirectoryName(Path.GetFullPath(outputFile))!;
                    Directory.CreateDirectory(targetDir);

                    if (parsed.Exports.OfType<UTexture2D>().FirstOrDefault() is { } tex && (format == "auto" || format == "png"))
                    {
                        var decoded = TextureDecoder.Decode(tex, ETexturePlatform.DesktopMobile);
                        if (decoded != null)
                        {
                            using var bmp = TextureEncoder.ToSkBitmap(decoded);
                            using var img = SKImage.FromBitmap(bmp);
                            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                            var outPng = outputFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? outputFile : outputFile + ".png";
                            File.WriteAllBytes(outPng, data.ToArray());
                            return McpToolCallResult.Json(new { Success = true, OutputPath = outPng, Format = "PNG Image", Size = data.Size });
                        }
                    }

                    if (parsed.Exports.OfType<UStaticMesh>().FirstOrDefault() is { } sm && (format == "auto" || format == "gltf"))
                    {
                        var meshExp = new MeshExporter(sm, new ExporterOptions { MeshFormat = EMeshFormat.Gltf2 });
                        if (meshExp.TryWriteToDir(new DirectoryInfo(targetDir), out _, out var saved))
                        {
                            return McpToolCallResult.Json(new { Success = true, OutputPath = saved, Format = "glTF 2.0 Mesh" });
                        }
                    }

                    if (parsed.Exports.OfType<UDataTable>().FirstOrDefault() is { } dtExp && (format == "auto" || format == "csv"))
                    {
                        var dtr = new DataTableReconstructor();
                        var res = dtr.Reconstruct(parsed, Path.Combine(targetDir, Path.GetFileNameWithoutExtension(outputFile)));
                        var outCsv = outputFile.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? outputFile : outputFile + ".csv";
                        return McpToolCallResult.Json(new { Success = true, OutputPath = outCsv, Format = "CSV DataTable", res.Note });
                    }

                    if (parsed.Exports.OfType<UStringTable>().FirstOrDefault() is { } stExp && (format == "auto" || format == "csv"))
                    {
                        var str = new StringTableReconstructor();
                        var res = str.Reconstruct(parsed, Path.Combine(targetDir, Path.GetFileNameWithoutExtension(outputFile)));
                        var outCsv = outputFile.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? outputFile : outputFile + ".csv";
                        return McpToolCallResult.Json(new { Success = true, OutputPath = outCsv, Format = "CSV StringTable", res.Note });
                    }

                    if (parsed.Exports.OfType<USoundWave>().FirstOrDefault() is { } swExp && (format == "auto" || format == "wav" || format == "ogg"))
                    {
                        var ar = new AudioReconstructor();
                        var res = ar.Reconstruct(parsed, Path.Combine(targetDir, Path.GetFileNameWithoutExtension(outputFile)));
                        var writtenFile = res.SidecarFiles.FirstOrDefault() ?? outputFile;
                        return McpToolCallResult.Json(new { Success = true, OutputPath = Path.Combine(targetDir, writtenFile), Format = "Audio", res.Note });
                    }

                    var jsonPath = outputFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? outputFile : outputFile + ".json";
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
                    {
                        VirtualPath = file.Path,
                        parsed.PrimaryType,
                        Exports = parsed.Exports.Select(e => new { e.ExportType, e.Name })
                    }, new JsonSerializerOptions { WriteIndented = true }));

                    return McpToolCallResult.Json(new { Success = true, OutputPath = jsonPath, Format = "JSON IR" });
                }

                case "ue_extract_lighting":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var mapPath = arguments.GetProperty("mapPath").GetString()!;
                    var outputJson = arguments.TryGetProperty("outputJson", out var oj) ? oj.GetString() : null;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = mapPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".umap", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Map '{mapPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse map asset '{mapPath}'.", isError: true);

                    var lr = new LevelReconstructor();
                    var result = lr.Reconstruct(parsed, Path.GetFileNameWithoutExtension(file.Path));

                    if (!string.IsNullOrWhiteSpace(outputJson) && result.Model != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson))!);
                        File.WriteAllText(outputJson, JsonSerializer.Serialize(result.Model, new JsonSerializerOptions { WriteIndented = true }));
                    }

                    return McpToolCallResult.Json(new
                    {
                        Map = file.Path,
                        Result = result.Note,
                        result.Fidelity,
                        LightingProfile = result.Model
                    });
                }

                case "ue_inspect_skeleton":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var skelPath = arguments.GetProperty("skeletonPath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = skelPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Skeleton '{skelPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse skeleton asset '{skelPath}'.", isError: true);

                    var ar = new AnimationReconstructor();
                    var tempOut = Path.Combine(Path.GetTempPath(), "ue4d_mcp_skel_" + Guid.NewGuid().ToString("N"));
                    var result = ar.Reconstruct(parsed, tempOut);

                    return McpToolCallResult.Json(new
                    {
                        Skeleton = file.Path,
                        Result = result.Note,
                        result.Fidelity,
                        Model = result.Model
                    });
                }

                case "ue_inspect_animation":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var animPath = arguments.GetProperty("animPath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = animPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Animation '{animPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse animation asset '{animPath}'.", isError: true);

                    var ar = new AnimationReconstructor();
                    var tempOut = Path.Combine(Path.GetTempPath(), "ue4d_mcp_anim_" + Guid.NewGuid().ToString("N"));
                    var result = ar.Reconstruct(parsed, tempOut);

                    return McpToolCallResult.Json(new
                    {
                        Animation = file.Path,
                        Result = result.Note,
                        result.Fidelity,
                        Model = result.Model
                    });
                }

                case "ue_inspect_input":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var inputPath = arguments.GetProperty("inputPath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE5_1;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = inputPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Input asset '{inputPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse input asset '{inputPath}'.", isError: true);

                    var ir = new InputReconstructor();
                    var tempOut = Path.Combine(Path.GetTempPath(), "ue4d_mcp_input_" + Guid.NewGuid().ToString("N"));
                    var result = ir.Reconstruct(parsed, tempOut);

                    return McpToolCallResult.Json(new
                    {
                        InputAsset = file.Path,
                        Result = result.Note,
                        result.Fidelity,
                        Model = result.Model
                    });
                }

                case "ue_generate_level_script":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var mapPath = arguments.GetProperty("mapPath").GetString()!;
                    var outputScript = arguments.GetProperty("outputScript").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = mapPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".umap", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Map '{mapPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse map asset '{mapPath}'.", isError: true);

                    var lr = new LevelReconstructor();
                    var outNoExt = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputScript))!, Path.GetFileNameWithoutExtension(outputScript));
                    var result = lr.Reconstruct(parsed, outNoExt);

                    var generatedPy = result.SidecarFiles.FirstOrDefault(s => s.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
                    if (generatedPy != null && generatedPy != outputScript && File.Exists(generatedPy))
                    {
                        File.Copy(generatedPy, outputScript, overwrite: true);
                    }

                    return McpToolCallResult.Json(new
                    {
                        Success = true,
                        OutputScript = outputScript,
                        Map = file.Path,
                        Result = result.Note,
                        result.Fidelity
                    });
                }

                case "ue_inspect_material_instance":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var materialPath = arguments.GetProperty("materialPath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = materialPath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"MaterialInstance '{materialPath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse material asset '{materialPath}'.", isError: true);

                    var mr = new MaterialReconstructor();
                    var tempOut = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(file.Path));
                    var result = mr.Reconstruct(parsed, tempOut);

                    return McpToolCallResult.Json(new
                    {
                        Success = true,
                        Material = file.Path,
                        result.Fidelity,
                        result.Note,
                        Model = result.Model
                    });
                }

                case "ue_inspect_sound_cue":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var cuePath = arguments.GetProperty("cuePath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = cuePath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Sound asset '{cuePath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse sound asset '{cuePath}'.", isError: true);

                    var ar = new AudioReconstructor();
                    var tempOut = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(file.Path));
                    var result = ar.Reconstruct(parsed, tempOut, noMediaExport: true);

                    return McpToolCallResult.Json(new
                    {
                        Success = true,
                        Sound = file.Path,
                        result.Fidelity,
                        result.Note,
                        Model = result.Model
                    });
                }

                case "ue_inspect_curve":
                {
                    var container = arguments.GetProperty("containerPath").GetString()!;
                    var curvePath = arguments.GetProperty("curvePath").GetString()!;
                    var aes = arguments.TryGetProperty("aesKey", out var ak) ? ak.GetString() : null;
                    var engine = arguments.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null;

                    var game = VersionDetector.FromHint(engine) ?? EGame.GAME_UE4_27;
                    var parsedAes = AesKeyResolver.FromHex(aes);

                    using var extractor = new PakExtractor(container, game, parsedAes, readScriptData: true);
                    var parser = new AssetParser(extractor.Provider);

                    var normalized = curvePath.Replace('\\', '/').Trim('/');
                    var file = extractor.Provider.Files.Values.FirstOrDefault(f =>
                        f.Path.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                        f.Path.EndsWith(normalized + ".uasset", StringComparison.OrdinalIgnoreCase));

                    if (file == null)
                        return McpToolCallResult.Text($"Curve asset '{curvePath}' not found in container '{container}'.", isError: true);

                    var parsed = parser.Parse(file);
                    if (parsed == null)
                        return McpToolCallResult.Text($"Failed to parse curve asset '{curvePath}'.", isError: true);

                    var cr = new CurveReconstructor();
                    var tempOut = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(file.Path));
                    var result = cr.Reconstruct(parsed, tempOut);

                    return McpToolCallResult.Json(new
                    {
                        Success = true,
                        Curve = file.Path,
                        result.Fidelity,
                        result.Note,
                        Model = result.Model
                    });
                }

                case "ue_export_reconstruction_scripts":
                {
                    var outputRoot = arguments.GetProperty("outputRoot").GetString()!;
                    if (!Directory.Exists(outputRoot))
                        return McpToolCallResult.Text($"Directory '{outputRoot}' does not exist.", isError: true);

                    var scripts = new List<object>();
                    var validSuffixes = new[] { "_reconstruct.py", "_sockets.py", "_mic_setup.py", "_soundcue.py", "_attenuation.py", "_setup.py", "ReconstructAllLevels.py" };

                    foreach (var f in Directory.EnumerateFiles(outputRoot, "*.py", SearchOption.AllDirectories))
                    {
                        var scriptName = Path.GetFileName(f);
                        if (validSuffixes.Any(s => scriptName.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                        {
                            var fi = new FileInfo(f);
                            var category = scriptName.EndsWith("_reconstruct.py") ? "Level"
                                : scriptName.EndsWith("_sockets.py") ? "SkeletalSockets"
                                : scriptName.EndsWith("_mic_setup.py") ? "MaterialInstance"
                                : scriptName.EndsWith("_soundcue.py") ? "SoundCue"
                                : scriptName.EndsWith("_attenuation.py") ? "SoundAttenuation"
                                : scriptName.EndsWith("ReconstructAllLevels.py") ? "MasterBatchRunner"
                                : "CurveOrInput";

                            scripts.Add(new
                            {
                                FileName = scriptName,
                                RelativePath = Path.GetRelativePath(outputRoot, f),
                                Category = category,
                                SizeBytes = fi.Length
                            });
                        }
                    }

                    return McpToolCallResult.Json(new
                    {
                        Success = true,
                        OutputRoot = outputRoot,
                        TotalScripts = scripts.Count,
                        Scripts = scripts
                    });
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

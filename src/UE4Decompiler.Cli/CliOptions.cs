using CommandLine;

namespace UE4Decompiler.Cli;

public abstract class BaseCliOptions
{
    [Option("verbose", HelpText = "Enable verbose (debug) logging.")]
    public bool Verbose { get; set; }

    [Option("quiet", HelpText = "Suppress non-essential console output.")]
    public bool Quiet { get; set; }

    [Option("json", HelpText = "Format command output as structured JSON.")]
    public bool Json { get; set; }

    [Option("log-file", HelpText = "Path to write diagnostic logs.")]
    public string? LogFile { get; set; }

    [Option("no-color", HelpText = "Disable ANSI color formatting.")]
    public bool NoColor { get; set; }
}

[Verb("inspect", HelpText = "Inspect container metadata, compression, encryption, and package inventory.")]
public sealed class InspectOptions : BaseCliOptions
{
    [Value(0, Required = true, MetaName = "input", HelpText = "Path to .pak, .utoc/.ucas, or container directory.")]
    public string Input { get; set; } = "";

    [Option("engine", HelpText = "Target Unreal Engine version hint (e.g. 4.27, 5.3). Auto-detected if omitted.")]
    public string? Engine { get; set; }

    [Option("aes-key", HelpText = "AES-256 key as hex (0x...); required if encrypted.")]
    public string? AesKey { get; set; }

    [Option("mapping", HelpText = "Path to .usmap unversioned property mapping file.")]
    public string? Mapping { get; set; }
}

[Verb("scan", HelpText = "Scan a folder for Unreal Engine containers and enumerate assets.")]
public sealed class ScanOptions : BaseCliOptions
{
    [Value(0, Required = true, MetaName = "directory", HelpText = "Directory containing game packages or containers.")]
    public string Directory { get; set; } = "";

    [Option("filter", HelpText = "Glob pattern to filter assets, e.g. \"Characters/**\".")]
    public string? Filter { get; set; }

    [Option("aes-key", HelpText = "AES-256 key as hex (0x...).")]
    public string? AesKey { get; set; }

    [Option("engine", HelpText = "Target Unreal Engine version hint (e.g. 4.27, 5.3).")]
    public string? Engine { get; set; }
}

[Verb("recover", HelpText = "Recover assets from containers into an uncooked Unreal Engine project.")]
public sealed class RecoverOptions : BaseCliOptions
{
    [Value(0, Required = true, MetaName = "input", HelpText = "Path to container or directory of containers.")]
    public string Input { get; set; } = "";

    [Option('o', "output", Required = true, HelpText = "Target output project directory.")]
    public string Output { get; set; } = "";

    [Option("engine", HelpText = "Unreal Engine version hint (e.g. 4.27, 5.3).")]
    public string? Engine { get; set; }

    [Option("aes-key", HelpText = "AES-256 key in hex (0x...).")]
    public string? AesKey { get; set; }

    [Option("mapping", HelpText = "Path to .usmap unversioned property mappings file.")]
    public string? Mapping { get; set; }

    [Option("filter", HelpText = "Asset virtual path glob filter, e.g. 'Maps/**'.")]
    public string? Filter { get; set; }

    [Option("skip-blueprints", HelpText = "Emit lightweight Blueprint stubs without attempting graph reconstruction.")]
    public bool SkipBlueprints { get; set; }

    [Option("full-recovery", HelpText = "Decompile complete Kismet bytecode and anim-graph ordering into JSON IR.")]
    public bool FullRecovery { get; set; }

    [Option("no-media", HelpText = "Skip GLB mesh / PNG texture transcoding; write only .uasset / JSON.")]
    public bool NoMedia { get; set; }

    [Option("emit-stubs", HelpText = "Generate compilable C++ stub modules for referenced game classes.")]
    public bool EmitStubs { get; set; }

    [Option("threads", Default = 0, HelpText = "Maximum concurrent extraction threads (0 = auto).")]
    public int Threads { get; set; }

    [Option("dry-run", HelpText = "Simulate recovery and report planned operations without writing to disk.")]
    public bool DryRun { get; set; }

    [Option("dangerously-dump-bpgraph", HelpText = "Clone raw cooked internals for all Blueprints.")]
    public bool DangerBpGraph { get; set; }

    [Option("template", HelpText = "Path to clean editor Blueprint or map template for synthesis.")]
    public string? Template { get; set; }

    [Option("cube", HelpText = "Path to clean engine Cube StaticMesh for placeholder synthesis.")]
    public string? Cube { get; set; }
}

[Verb("export", HelpText = "Export assets directly to standard formats (GLB, PNG, WAV, JSON).")]
public sealed class ExportOptions : BaseCliOptions
{
    [Value(0, Required = true, MetaName = "input", HelpText = "Input container or directory.")]
    public string Input { get; set; } = "";

    [Option('o', "output", Required = true, HelpText = "Export destination directory.")]
    public string Output { get; set; } = "";

    [Option("filter", HelpText = "Filter glob for exported assets.")]
    public string? Filter { get; set; }

    [Option("textures", HelpText = "Export Texture2D assets to PNG.")]
    public bool Textures { get; set; } = true;

    [Option("meshes", HelpText = "Export StaticMesh and SkeletalMesh assets to glTF (.glb).")]
    public bool Meshes { get; set; } = true;

    [Option("materials", HelpText = "Export Material graphs to JSON IR and HLSL.")]
    public bool Materials { get; set; } = true;

    [Option("engine", HelpText = "Engine version hint.")]
    public string? Engine { get; set; }

    [Option("aes-key", HelpText = "AES-256 key in hex.")]
    public string? AesKey { get; set; }
}

[Verb("graph", HelpText = "Decompile Blueprint Kismet bytecode into graph visualization or pseudo-code.")]
public sealed class GraphOptions : BaseCliOptions
{
    [Value(0, Required = true, MetaName = "input", HelpText = "Container path or single package file.")]
    public string Input { get; set; } = "";

    [Value(1, Required = true, MetaName = "asset", HelpText = "Virtual path to the target Blueprint (e.g. '/Game/BP_Player').")]
    public string AssetPath { get; set; } = "";

    [Option("function", HelpText = "Optional specific function name to decompile.")]
    public string? Function { get; set; }

    [Option("format", Default = "pseudo", HelpText = "Output format: 'pseudo' (pseudo-code), 'dot' (Graphviz), 'mermaid'.")]
    public string Format { get; set; } = "pseudo";

    [Option('o', "output", HelpText = "File path to save the output graph; prints to stdout if omitted.")]
    public string? OutputFile { get; set; }

    [Option("engine", HelpText = "Engine version hint.")]
    public string? Engine { get; set; }

    [Option("aes-key", HelpText = "AES-256 key in hex.")]
    public string? AesKey { get; set; }
}

[Verb("validate", HelpText = "Validate recovered assets in a project directory.")]
public sealed class ValidateOptions : BaseCliOptions
{
    [Value(0, Required = true, MetaName = "directory", HelpText = "Directory of recovered assets to validate.")]
    public string Directory { get; set; } = "";

    [Option("engine", HelpText = "Engine version to validate against (defaults to 4.21).")]
    public string? Engine { get; set; }
}

[Verb("doctor", HelpText = "Run environment, dependency, and storage diagnostics.")]
public sealed class DoctorOptions : BaseCliOptions
{
    [Option("input", HelpText = "Optional input container path to check.")]
    public string? Input { get; set; }

    [Option("output", HelpText = "Optional output directory to verify write permissions.")]
    public string? Output { get; set; }
}

[Verb("capabilities", HelpText = "Display the engine and asset format capability matrix.")]
public sealed class CapabilitiesOptions : BaseCliOptions
{
}

[Verb("version", HelpText = "Display application version and engine support details.")]
public sealed class VersionOptions : BaseCliOptions
{
}

/// <summary>
/// Backward-compatible legacy flag model.
/// </summary>
public sealed class LegacyCliOptions
{
    [Option("input", HelpText = "Path to a .pak/.utoc or a directory of containers.")]
    public string? Input { get; set; }

    [Option("output", HelpText = "Output project root directory.")]
    public string? Output { get; set; }

    [Option("version", HelpText = "UE version hint, e.g. 4.27 or 5.1.")]
    public string? Version { get; set; }

    [Option("aes-key", HelpText = "AES-256 key as hex (0x...).")]
    public string? AesKey { get; set; }

    [Option("filter", HelpText = "Glob to extract a subset, e.g. \"Characters/**\".")]
    public string? Filter { get; set; }

    [Option("skip-blueprints", HelpText = "Emit blueprint stubs instead of attempting graph reconstruction.")]
    public bool SkipBlueprints { get; set; }

    [Option("dangerously-dump-bpgraph", HelpText = "Reconstruct EVERY blueprint by cloning cooked guts.")]
    public bool DangerouslyDumpBpGraph { get; set; }

    [Option("full-recovery", HelpText = "Emit full Kismet bytecode into JSON.")]
    public bool FullRecovery { get; set; }

    [Option("force-write", HelpText = "Dev: bypass whitelist and emit uncooked package for every asset.")]
    public bool ForceWrite { get; set; }

    [Option("emit-stubs", HelpText = "Generate compilable C++ stub modules.")]
    public bool EmitStubs { get; set; }

    [Option("no-media-export", HelpText = "Skip media exports (PNG/GLB).")]
    public bool NoMediaExport { get; set; }

    [Option("sdk-dump", HelpText = "Path to an SDK dump dir.")]
    public string? SdkDump { get; set; }

    [Option("dry-run", HelpText = "List what would be extracted without writing.")]
    public bool DryRun { get; set; }

    [Option("report", HelpText = "Write a JSON manifest of all assets.")]
    public bool Report { get; set; }

    [Option("verbose", HelpText = "Verbose (debug) logging.")]
    public bool Verbose { get; set; }

    [Option("validate-write", HelpText = "Dev: write one package's uncooked .uasset and re-parse.")]
    public string? ValidateWrite { get; set; }

    [Option("list-files", HelpText = "Dev: list mounted virtual paths containing substring.")]
    public string? ListFiles { get; set; }

    [Option("extract-file", HelpText = "Dev: extract one package.")]
    public string? ExtractFile { get; set; }

    [Option("gen-mesh-real", HelpText = "Tier2 mesh: build REAL-geometry editor StaticMesh.")]
    public string? GenMeshReal { get; set; }

    [Option("decode", HelpText = "Dev: raw-decode local .uasset/.umap summary+imports.")]
    public string? Decode { get; set; }

    [Option("dump-package", HelpText = "Dev: parse local .uasset and dump properties.")]
    public string? DumpPackage { get; set; }

    [Option("reemit", HelpText = "Dev: round-trip test.")]
    public string? Reemit { get; set; }

    [Option("build-graph", HelpText = "Tier2: build editor-openable blueprint.")]
    public string? BuildGraph { get; set; }

    [Option("hex-export", HelpText = "Dev: hex-dump exports.")]
    public string? HexExport { get; set; }

    [Option("clone-bp", HelpText = "Dev: clone editor .uasset verbatim.")]
    public string? CloneBp { get; set; }

    [Option("inject-event", HelpText = "Tier2: inject K2Node_Event.")]
    public string? InjectEvent { get; set; }

    [Option("walk-bytecode", HelpText = "Dev: walk Kismet bytecode.")]
    public string? WalkBytecode { get; set; }

    [Option("probe-level", HelpText = "Dev: print map actors array.")]
    public string? ProbeLevel { get; set; }

    [Option("inject-call", HelpText = "Tier2: inject CallFunction(PrintString).")]
    public string? InjectCall { get; set; }

    [Option("reconstruct", HelpText = "Tier2: reconstruct cooked BP onto template.")]
    public string? Reconstruct { get; set; }

    [Option("place-actors", HelpText = "Tier2: synthesize cooked map actors onto template.")]
    public string? PlaceActors { get; set; }

    [Option("cube", HelpText = "Engine Cube StaticMesh path.")]
    public string? Cube { get; set; }

    [Option("content-root", HelpText = "Project Content dir.")]
    public string? ContentRoot { get; set; }

    [Option("template", HelpText = "Editor BP template path.")]
    public string? Template { get; set; }

    [Option("gen-mesh-all", HelpText = "Generate real meshes for every StaticMesh.")]
    public bool GenMeshAll { get; set; }

    [Option("tex-max", Default = 1024, HelpText = "Max texture source dimension.")]
    public int TexMax { get; set; }

    [Option("gen-tex", HelpText = "Dev: build one editor texture.")]
    public string? GenTex { get; set; }

    [Option("gen-mat", HelpText = "Dev: clone material template.")]
    public string? GenMat { get; set; }

    [Option("call-message", Default = "reconstructed", HelpText = "PrintString message.")]
    public string? CallMessage { get; set; }

    [Option("reskin", HelpText = "Tier2: reskin loadable editor BP template.")]
    public string? Reskin { get; set; }

    [Option("reskin-name", Default = "BP_HandProxyExample", HelpText = "Target name for --reskin.")]
    public string? ReskinName { get; set; }

    [Option("reskin-path", Default = "/Game/Maps/BP_HandProxyExample", HelpText = "Target path for --reskin.")]
    public string? ReskinPath { get; set; }

    [Option("event-name", Default = "ReceiveEndPlay", HelpText = "Event name for --inject-event.")]
    public string? EventName { get; set; }
}

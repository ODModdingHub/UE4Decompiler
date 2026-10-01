using CUE4Parse.UE4.Versions;

namespace UE4Decompiler.Core.Models;

/// <summary>A game-module type (UClass/UScriptStruct/UEnum) referenced by extracted assets but not
/// present in the stock engine — a candidate for stub generation so the assets can load.</summary>
public sealed record GameStub(string Module, string Name, string Kind);

/// <summary>How faithfully a single asset was recovered.</summary>
public enum Fidelity
{
    Full,      // round-tripped with high confidence (e.g. materials, textures)
    Partial,   // most data recovered, some approximation (e.g. meshes, levels)
    Stub,      // structure/metadata only, content best-effort (e.g. blueprints)
    Copied,    // raw bulk data copied verbatim (e.g. sounds)
    Failed     // parsing threw; a placeholder was emitted so the project still opens
}

/// <summary>
/// Status of an individual asset's recovery result.
/// </summary>
public enum RecoveryStatus
{
    Recovered,
    PartiallyRecovered,
    MetadataOnly,
    Skipped,
    Failed
}

/// <summary>
/// Capability tier of a reconstructor or pipeline for an asset type.
/// </summary>
[Flags]
public enum AssetRecoveryCapability
{
    Unsupported = 0,
    MetadataOnly = 1,
    PartiallyRecovered = 2,
    Recovered = 4,
    EditorLoadable = 8
}

/// <summary>
/// Predefined recovery profiles.
/// </summary>
public enum RecoveryProfile
{
    StandardRecovery,
    MetadataOnly,
    FastRecovery,
    FullRecovery,
    BlueprintResearch,
    AssetExport,
    ProjectReconstruction
}

/// <summary>
/// Type of container discovered.
/// </summary>
public enum ContainerType
{
    Pak,
    IoStoreUtoc,
    Loose
}

/// <summary>
/// Information about a discovered container file.
/// </summary>
public sealed record ContainerInfo(
    string FilePath,
    ContainerType Type,
    bool IsEncrypted,
    string? EncryptionKeyGuid,
    int FileCount,
    string MountPoint
);

/// <summary>
/// Discovered game asset before recovery.
/// </summary>
public sealed record DiscoveredAsset(
    string VirtualPath,
    string Extension,
    string MountPoint,
    long Size,
    bool IsPackage,
    bool IsMap
);

/// <summary>
/// Detailed result for a single asset (Phase 76 design).
/// </summary>
public sealed class RecoveryResult
{
    public required string AssetPath { get; init; }
    public required string PackagePath { get; init; }
    public string ObjectPath { get; init; } = "";
    public required string ClassName { get; init; }
    public required string EngineVersion { get; init; }
    public RecoveryStatus Status { get; set; } = RecoveryStatus.Recovered;
    public AssetRecoveryCapability Capability { get; set; } = AssetRecoveryCapability.Recovered;
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Outputs { get; } = new();
    public List<string> Dependencies { get; } = new();
    public TimeSpan Duration { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
    public List<string> SidecarFiles { get; } = new();
    public string? Note { get; set; }
}

/// <summary>One row in the output manifest / --report JSON.</summary>
public sealed class ManifestEntry
{
    public required string VirtualPath { get; init; }
    public required string OutputPath { get; init; }
    public required string AssetType { get; init; }
    public Fidelity Fidelity { get; set; }
    public string? Note { get; set; }
    public string? BCookedPatched { get; set; } // [10.1] "patched" | "skipped" | "ambiguous" | "n/a"
    public List<string> SidecarFiles { get; } = new();
}

/// <summary>What a reconstructor produced for one asset.</summary>
public sealed class ReconstructionResult
{
    public Fidelity Fidelity { get; init; }
    public string? Note { get; init; }

    /// <summary>JSON-serializable recovered model (written as the .json sidecar).</summary>
    public object? Model { get; init; }

    /// <summary>Extra files the reconstructor already wrote to disk (relative leaf names), for the manifest.</summary>
    public List<string> SidecarFiles { get; } = new();

    public static ReconstructionResult Failed(string note) => new() { Fidelity = Fidelity.Failed, Note = note };
}

/// <summary>Fully resolved run options (post CLI parsing + version/key resolution).</summary>
public sealed class DecompileOptions
{
    public required string InputPath { get; init; }
    public required string OutputRoot { get; init; }
    public required EGame Game { get; init; }
    public required string EngineAssociation { get; init; }
    public string? FilterGlob { get; init; }
    public string? MappingPath { get; init; }
    public string? AesKey { get; init; }
    public bool SkipBlueprints { get; init; }
    public bool FullRecovery { get; init; }
    public bool ForceWrite { get; init; }
    public bool NoMediaExport { get; init; }
    public bool DryRun { get; init; }
    public bool Verbose { get; init; }
    public bool EmitStubs { get; init; }
    public bool DangerBpGraph { get; init; }
    public string ProjectName { get; init; } = "DecompiledProject";
    public string? MapTemplate { get; init; }
    public string? CubePath { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = 0; // 0 = default (ProcessorCount)
    public string? CacheDirectory { get; init; }
    public bool UseCache { get; init; } = true;
    public RecoveryProfile Profile { get; init; } = RecoveryProfile.StandardRecovery;

    public string ContentRoot => Path.Combine(OutputRoot, "Content");
}

/// <summary>
/// Progress reporting payload for CLI and GUI.
/// </summary>
public sealed record RecoveryProgress(
    int ProcessedCount,
    int TotalCount,
    string CurrentAsset,
    string Stage,
    double Percentage
);

/// <summary>
/// Complete scan result of an input container or folder.
/// </summary>
public sealed class ScanResult
{
    public required string InputPath { get; init; }
    public required EGame DetectedGame { get; init; }
    public required string EngineAssociation { get; init; }
    public required IReadOnlyList<ContainerInfo> Containers { get; init; }
    public required IReadOnlyList<DiscoveredAsset> Assets { get; init; }
    public required IReadOnlyDictionary<string, int> AssetCountsByClass { get; init; }
    public bool IsEncrypted { get; init; }
    public bool RequiresMapping { get; init; }
    public string? DetectedProjectName { get; init; }
}

/// <summary>
/// Machine-readable and human-readable final decompile report (Phase 25).
/// </summary>
public sealed class RecoveryReport
{
    public int SchemaVersion { get; init; } = 3;
    public string ToolVersion { get; init; } = "2.0.0";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public required string InputPath { get; init; }
    public required string OutputRoot { get; init; }
    public required string Engine { get; init; }
    public TimeSpan ElapsedTime { get; set; }
    public int TotalAssets { get; set; }
    public int RecoveredCount { get; set; }
    public int PartialCount { get; set; }
    public int StubCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public Dictionary<string, int> ByFidelity { get; init; } = new();
    public Dictionary<string, int> ByAssetClass { get; init; } = new();
    public List<ManifestEntry> Manifest { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Diagnostic item produced by 'doctor'.
/// </summary>
public enum DiagnosticLevel
{
    Pass,
    Info,
    Warning,
    Error
}

public sealed record DiagnosticItem(
    string Category,
    string CheckName,
    DiagnosticLevel Level,
    string Message,
    string? SuggestedFix = null
);

public sealed class DiagnosticReport
{
    public required IReadOnlyList<DiagnosticItem> Items { get; init; }
    public bool HasErrors => Items.Any(i => i.Level == DiagnosticLevel.Error);
    public bool HasWarnings => Items.Any(i => i.Level == DiagnosticLevel.Warning);
}

/// <summary>
/// Validation result item for an asset on disk.
/// </summary>
public sealed record ValidationItem(
    string FilePath,
    bool IsValid,
    string AssetType,
    string? ErrorMessage,
    IReadOnlyList<string> ChecksPassed
);

public sealed class ValidationReport
{
    public required string TargetDirectory { get; init; }
    public required IReadOnlyList<ValidationItem> Items { get; init; }
    public int TotalChecked => Items.Count;
    public int ValidCount => Items.Count(i => i.IsValid);
    public int InvalidCount => Items.Count(i => !i.IsValid);
    public bool IsAllValid => InvalidCount == 0;
}

/// <summary>
/// Capability matrix entry models (Phase 77).
/// </summary>
public sealed record EngineCapabilityEntry(string EngineVersion, bool ReadSupported, bool UncookedWriteSupported, string Notes);
public sealed record ContainerCapabilityEntry(string ContainerName, bool Supported, string Notes);
public sealed record AssetCapabilityEntry(string AssetClass, AssetRecoveryCapability Capability, string RecoveryMethod, string Formats);

public sealed class CapabilityMatrix
{
    public required IReadOnlyList<EngineCapabilityEntry> Engines { get; init; }
    public required IReadOnlyList<ContainerCapabilityEntry> Containers { get; init; }
    public required IReadOnlyList<AssetCapabilityEntry> Assets { get; init; }
}

/// <summary>
/// Dependency graph structures for assets.
/// </summary>
public sealed class DependencyNode
{
    public required string PackagePath { get; init; }
    public required string ClassName { get; init; }
    public List<string> References { get; } = new();
    public List<string> ReferencedBy { get; } = new();
}

public sealed class DependencyGraph
{
    public Dictionary<string, DependencyNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Cached asset recovery output.
/// </summary>
public sealed class CachedAssetResult
{
    public required string CacheKey { get; init; }
    public required string VirtualPath { get; init; }
    public required string ClassName { get; init; }
    public required RecoveryStatus Status { get; init; }
    public required List<string> CreatedFiles { get; init; }
    public required DateTime TimestampUtc { get; init; }
}



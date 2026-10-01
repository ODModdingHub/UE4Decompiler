using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Core.Abstractions;

/// <summary>
/// Main orchestration service consumed by CLI, GUI, and tests.
/// </summary>
public interface IDecompilerService
{
    /// <summary>
    /// Inspect and scan an input container or directory, gathering asset inventory and metadata.
    /// </summary>
    Task<ScanResult> ScanAsync(string inputPath, DecompileOptions options, CancellationToken ct = default);

    /// <summary>
    /// Execute asset recovery pipeline with live progress reporting and cancellation support.
    /// </summary>
    Task<RecoveryReport> RecoverAsync(
        string inputPath,
        string outputPath,
        DecompileOptions options,
        IProgress<RecoveryProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validate recovered packages in the target directory (checks package summaries, export maps, re-parses).
    /// </summary>
    Task<ValidationReport> ValidateAsync(string outputPath, CancellationToken ct = default);

    /// <summary>
    /// Execute system diagnostics: checks .NET runtime, disk access, native libraries, container integrity.
    /// </summary>
    Task<DiagnosticReport> RunDoctorAsync(string? inputPath = null, string? outputPath = null, CancellationToken ct = default);

    /// <summary>
    /// Programmatically query engine, container, and asset type recovery capabilities.
    /// </summary>
    CapabilityMatrix GetCapabilities();
}

/// <summary>
/// Discovers containers (.pak, .utoc/.ucas, loose) and enumerates asset files.
/// </summary>
public interface IAssetDiscoveryService
{
    IReadOnlyList<ContainerInfo> DiscoverContainers(string inputPath);
    IReadOnlyList<DiscoveredAsset> EnumerateAssets(AbstractFileProvider provider, string? filterGlob = null);
}

/// <summary>
/// Resolves and loads UE .usmap unversioned property mapping files into CUE4Parse.
/// </summary>
public interface IMappingProvider
{
    bool TryLoadMapping(string? mappingPath, AbstractFileProvider provider, out string? message);
    IReadOnlyList<string> FindMappingCandidates(string searchDirectory);
}

/// <summary>
/// Recovers and decompiles Blueprint bytecode graphs into Pseudo-code, Graphviz DOT, Mermaid, or JSON AST.
/// </summary>
public interface IBlueprintDecompiler
{
    string ToPseudoBlueprint(ParsedAsset asset, string? functionName = null);
    string ToGraphvizDot(ParsedAsset asset, string? functionName = null);
    string ToMermaid(ParsedAsset asset, string? functionName = null);
}

/// <summary>
/// Builds dependency references between packages (Materials -> Textures, Blueprints -> Parents/Components, etc.).
/// </summary>
public interface IDependencyGraphService
{
    DependencyGraph BuildGraph(IReadOnlyList<ParsedAsset> assets);
}

/// <summary>
/// Persistent asset recovery cache to accelerate large incremental jobs.
/// </summary>
public interface ICacheManager
{
    bool TryGetCached(string cacheKey, out CachedAssetResult? cached);
    void Store(string cacheKey, CachedAssetResult result);
    void Clear();
    string ComputeCacheKey(string assetPath, long size, string engineVersion, string optionsFingerprint);
}

/// <summary>
/// Validates recovered packages on disk.
/// </summary>
public interface IValidationService
{
    Task<ValidationReport> ValidateDirectoryAsync(string outputPath, EGame game, CancellationToken ct = default);
}

/// <summary>
/// System health and troubleshooting service for 'doctor' diagnostics.
/// </summary>
public interface IDiagnosticsService
{
    Task<DiagnosticReport> RunDiagnosticsAsync(string? inputPath, string? outputPath, CancellationToken ct = default);
}

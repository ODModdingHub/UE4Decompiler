using CUE4Parse.UE4.Versions;

namespace UE4Decompiler.Core;

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
    public bool SkipBlueprints { get; init; }
    public bool FullRecovery { get; init; }
    public bool ForceWrite { get; init; }
    public bool NoMediaExport { get; init; }
    public bool DryRun { get; init; }
    public bool Verbose { get; init; }
    public string ProjectName { get; init; } = "DecompiledProject";

    public string ContentRoot => Path.Combine(OutputRoot, "Content");
}

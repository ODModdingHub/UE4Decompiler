using CUE4Parse.UE4.Versions;

namespace UE4Decompiler.Core;

/// <summary>A game-module type (UClass/UScriptStruct/UEnum) referenced by extracted assets but not
/// present in the stock engine — a candidate for stub generation so the assets can load.</summary>
public sealed record GameStub(string Module, string Name, string Kind);

/// <summary>Safe post-dump Blueprint reparent request. The package bytes stay template-shaped during extraction;
/// Unreal Editor consumes these requests later so it can rebuild the generated class/CDO/SCS layout itself.</summary>
public sealed record BlueprintReparentRequest(string BlueprintPath, string ParentClassPath, string OutputPath, string? EngineBase);

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
    /// <summary>Generate compilable C++ stub modules for referenced game-native classes. When on, blueprints that
    /// subclass game natives can also be reconstructed (their stubbed ParentClass resolves, so no content-browser
    /// crash) — at the cost of the project becoming a C++ project that must be compiled before it opens.</summary>
    public bool EmitStubs { get; init; }
    /// <summary>Reconstruct EVERY blueprint via the cooked-guts clone (the BP editor crashes opening complex/widget
    /// ones). When false (default), only simple, open-safe blueprints are reconstructed.</summary>
    public bool DangerBpGraph { get; init; }
    public string ProjectName { get; init; } = "DecompiledProject";

    /// <summary>Empty editor map (.umap) used as the reskin base for in-pipeline actor placement. When set,
    /// maps are emitted via the filtered PlaceActors path instead of the crashing cooked uncooked-map write.</summary>
    public string? MapTemplate { get; init; }
    /// <summary>Engine Cube StaticMesh path; cloned as a loadable placeholder for each mesh referenced by a placed map.</summary>
    public string? CubePath { get; init; }
    /// <summary>Empty editor Blueprint (.uasset) used as the reskin base so cooked BPs (incl. UE5/Zen) show + open in
    /// the content browser. Cloned + renamed per BP (the byte-based reconstructor can't handle Zen).</summary>
    public string? BpTemplate { get; init; }

    /// <summary>Experimental: append recovered K2 CallFunction nodes from cooked bytecode into cloned Blueprint templates.</summary>
    public bool BpRecoverCalls { get; init; }

    /// <summary>Experimental: emit recovered name-only native methods on generated game stubs for K2 member binding.</summary>
    public bool EmitStubMethods { get; init; }

    /// <summary>Recovered game source modules copied into the output project. Blueprints may be reparented to these
    /// real native classes because UBT can compile and load them, unlike generated placeholder stubs.</summary>
    public IReadOnlyList<string> NativeSourceModules { get; init; } = Array.Empty<string>();

    public string? NativeSourcePath { get; init; }

    public string ContentRoot => Path.Combine(OutputRoot, "Content");
}

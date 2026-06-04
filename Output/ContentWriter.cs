using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using Serilog;
using UE4Decompiler.Core;
using UE4Decompiler.Output.Writer;
using UE4Decompiler.Reconstructors;

namespace UE4Decompiler.Output;

/// <summary>
/// Mirrors the pak's virtual path structure under Content/, routes each parsed asset to the right
/// reconstructor, and writes the recovered model + binary header via <see cref="AssetWriter"/>.
/// Engine content references are preserved as soft paths (never copied). Maintains the run manifest.
/// </summary>
public sealed class ContentWriter
{
    private readonly DecompileOptions _opts;
    private readonly AbstractFileProvider _provider;
    private readonly AssetWriter _writer = new();
    private readonly UncookedPackageWriter _packageWriter;

    private readonly TextureReconstructor _texture = new();
    private readonly MeshReconstructor _mesh = new();
    private readonly MaterialReconstructor _material = new();
    private readonly BlueprintReconstructor _blueprint;
    private readonly LevelReconstructor _level = new();

    public List<ManifestEntry> Manifest { get; } = new();

    /// <summary>Game-module types referenced by imports, keyed "Module.Name" — for stub generation.</summary>
    public Dictionary<string, GameStub> GameStubs { get; } = new();

    // Engine + common engine-plugin script modules the editor already provides; never stub these.
    private static readonly HashSet<string> EngineModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Core","CoreUObject","Engine","UMG","Slate","SlateCore","InputCore","RenderCore","RHI","Renderer",
        "ApplicationCore","NetCore","Json","JsonUtilities","HTTP","AIModule","NavigationSystem","GameplayTags",
        "GameplayTasks","GameplayAbilities","PhysicsCore","AnimGraphRuntime","MovieScene","MovieSceneTracks",
        "MovieSceneCapture","LevelSequence","Niagara","NiagaraCore","Paper2D","Foliage","Landscape","AudioMixer",
        "Synthesis","SoundUtilities","SoundVisualizations","SteamAudio","SteamVR","SteamVRInput","OpenVR",
        "OculusHMD","OculusInput","OculusVR","OculusPlatform","HeadMountedDisplay","AugmentedReality","ApexDestruction",
        "PhysXVehicles","CableComponent","CustomMeshComponent","ProceduralMeshComponent","MediaAssets","MediaUtils",
        "OnlineSubsystem","OnlineSubsystemUtils","PacketHandler","Voice","AndroidPermission","UnrealEd","BlueprintGraph",
        "AnimGraph","Kismet","KismetCompiler","PropertyEditor","EditorStyle","Foliage","Landscape","CinematicCamera",
        // Engine runtime/plugin modules the editor already ships — stubbing these collides with the real
        // module (e.g. UBT: "plugin does not contain the module"), so never stub them.
        "ClothingSystemRuntime","ClothingSystemRuntimeInterface","ClothingSystemRuntimeNv","ClothingSystemRuntimeCommon",
        "FacialAnimation","FacialAnimationEditor","GeometryCache","GeometryCollectionEngine","ChaosCloth",
    };

    private void CollectGameTypes(Package pkg)
    {
        var imports = pkg.ImportMap;
        foreach (var imp in imports)
        {
            var kind = imp.ClassName.Text;
            if (kind is not ("Class" or "ScriptStruct" or "Enum")) continue;     // only type definitions
            var pkgName = OutermostPackageName(imports, imp);
            if (pkgName is null || !pkgName.StartsWith("/Script/", StringComparison.Ordinal)) continue;
            var module = pkgName["/Script/".Length..];
            if (EngineModules.Contains(module)) continue;                        // editor already has it
            var name = imp.ObjectName.Text;
            GameStubs.TryAdd($"{module}.{name}", new GameStub(module, name, kind));
        }
    }

    /// <summary>Walk an import's OuterIndex chain to the top-level package import name (e.g. "/Script/Pavlov").</summary>
    private static string? OutermostPackageName(FObjectImport[] imports, FObjectImport imp)
    {
        var cur = imp;
        for (var guard = 0; guard < 64; guard++)
        {
            var outer = cur.OuterIndex;
            if (outer is null || outer.Index >= 0) break;                        // null or export-outer => stop
            var idx = -outer.Index - 1;
            if (idx < 0 || idx >= imports.Length) break;
            cur = imports[idx];
        }
        return cur.ObjectName.Text;
    }

    public ContentWriter(DecompileOptions opts, AbstractFileProvider provider)
    {
        _opts = opts;
        _provider = provider;
        _packageWriter = new UncookedPackageWriter(opts.Game);
        _blueprint = new BlueprintReconstructor(opts.SkipBlueprints, opts.FullRecovery);
    }

    /// <summary>Process one parsed asset end-to-end, returning the manifest entry (also appended to <see cref="Manifest"/>).</summary>
    public ManifestEntry Process(ParsedAsset asset)
    {
        var relative = MapToContentPath(asset.File.Path);
        var outputNoExt = Path.Combine(_opts.ContentRoot, relative);
        var ext = asset.IsMap ? ".umap" : ".uasset";
        var outputAsset = outputNoExt + ext;

        var entry = new ManifestEntry
        {
            VirtualPath = asset.File.Path,
            OutputPath = Path.GetRelativePath(_opts.OutputRoot, outputAsset),
            AssetType = asset.PrimaryType
        };
        Manifest.Add(entry);

        if (asset.Package is Package gpkg) CollectGameTypes(gpkg); // gather game-class refs for --emit-stubs

        if (_opts.DryRun)
        {
            // Plan only: classify + report the intended output path, touch nothing on disk.
            entry.Note = "[dry-run] would reconstruct -> " + entry.OutputPath;
            Log.Information("[dry-run] {Type,-28} {Path}", asset.PrimaryType, asset.File.Path);
            return entry;
        }

        ReconstructionResult result;
        try
        {
            result = Route(asset, outputNoExt);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reconstruction crashed for {Path}", asset.File.Path);
            result = ReconstructionResult.Failed(ex.Message);
        }

        entry.Fidelity = result.Fidelity;
        entry.Note = result.Note;
        entry.SidecarFiles.AddRange(result.SidecarFiles);

        // Always keep the JSON model sidecar (recovered data / reconstructor output).
        var model = result.Model ?? new { entry.AssetType, asset.File.Path, Placeholder = true, entry.Note };
        _writer.WriteJsonModel(outputAsset, model);

        // Emit a real editor-loadable uncooked package when the source carries no separate bulk
        // (textures/meshes/sounds need bulk inlining — handled by their reconstructors, not here yet).
        // Otherwise fall back to a structurally-valid placeholder header so the project still opens.
        var packageName = "/Game/" + relative.Replace('\\', '/');
        if (asset.IsMap)
        {
            // Maps must NOT go through UncookedPackageWriter: that re-serializes the cooked import table
            // (incl. plugin refs like /CustomMapTools/) which the editor can't resolve -> null-deref crash on load.
            // Route through the filtered PlaceActors path (requires --template + --cube); otherwise write an
            // empty-but-openable placeholder header.
            if (!TryWritePlacedMap(asset, outputAsset, packageName, entry))
                _writer.WriteUAssetHeader(outputAsset, _opts.Game, packageName);
        }
        else if (asset.PrimaryType is "StaticMesh" && TryWriteRealMesh(asset, outputAsset, packageName, entry))
        {
            // real-geometry editor mesh written (cube-clone + appended FRawMesh); skip the cooked uncooked write.
        }
        else if (!TryWriteUncooked(asset, outputAsset, entry))
            _writer.WriteUAssetHeader(outputAsset, _opts.Game, packageName);

        return entry;
    }

    /// <summary>When --cube is configured, emit a real-geometry editor StaticMesh (engine-cube clone with the
    /// source's converted FRawMesh appended + re-pointed) at the asset's /Game path, so placed maps render true
    /// geometry. Returns false if no cube is set or the mesh can't be converted (caller falls back).</summary>
    private bool TryWriteRealMesh(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_opts.CubePath)) return false;
        if (asset.Package is not Package pkg) return false;
        try
        {
            CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? sm = null;
            for (var i = 0; i < pkg.ExportMap.Length && sm is null; i++)
            {
                if (pkg.ExportMap[i].ClassName != "StaticMesh") continue;
                try { sm = pkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh; }
                catch { /* not loadable */ }
            }
            if (sm is null || !sm.TryConvert(out var cm) || cm.LODs.Count == 0) return false;

            var blob = MeshWriter.BuildFRawMesh(cm.LODs[0]);
            BlueprintGraphBuilder.CloneMesh(_opts.CubePath!, outputAsset, sm.Name, packageName, blob);
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "real-geometry editor mesh"
                                                          : entry.Note + "; real-geometry editor mesh";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Real-mesh write failed for {Path}; falling back", asset.File.Path);
            return false;
        }
    }

    /// <summary>Write a genuine uncooked .uasset by relocating the real payloads. Returns false if ineligible.</summary>
    private bool TryWriteUncooked(ParsedAsset asset, string outputAsset, ManifestEntry entry)
    {
        if (asset.Package is not Package pkg) return false;                 // IoStore not supported by writer
        if (!_opts.ForceWrite && !UncookedPackageWriter.IsPackageEligible(pkg)) return false; // [9.1] whitelist (bypassed by --force-write)

        try
        {
            var parts = _provider.SavePackage(asset.File.Path);
            var head = parts.FirstOrDefault(p => p.Key.EndsWith(".uasset") || p.Key.EndsWith(".umap")).Value
                       ?? parts.Values.First();
            var uexp = parts.FirstOrDefault(p => p.Key.EndsWith(".uexp")).Value;
            var combined = uexp is null ? head : Concat(head, uexp);

            var result = _packageWriter.Write(pkg, combined, outputAsset);
            entry.BCookedPatched = result.BCookedPatched;                   // [10.1]
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "editor-loadable uncooked package"
                                                          : entry.Note + "; editor-loadable uncooked package";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Uncooked write failed for {Path}; using placeholder header", asset.File.Path);
            return false;
        }
    }

    /// <summary>Emit an editor-loadable .umap by placing the cooked map's reliably-loadable actors (filtered to
    /// native /Script classes + cube-placeholder meshes) onto the empty editor template. Returns false when no
    /// template/cube is configured, so the caller falls back to an empty placeholder header.</summary>
    private bool TryWritePlacedMap(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        if (asset.Package is not Package) return false;
        if (string.IsNullOrWhiteSpace(_opts.MapTemplate) || string.IsNullOrWhiteSpace(_opts.CubePath))
        {
            Log.Warning("Map {Path}: --template + --cube not set; writing empty placeholder map (no actors)", asset.File.Path);
            return false;
        }
        try
        {
            var parts = _provider.SavePackage(asset.File.Path);
            var head = parts.FirstOrDefault(p => p.Key.EndsWith(".uasset") || p.Key.EndsWith(".umap")).Value
                       ?? parts.Values.First();
            var uexp = parts.FirstOrDefault(p => p.Key.EndsWith(".uexp")).Value;
            var combined = uexp is null ? head : Concat(head, uexp);

            var targetShort = Path.GetFileNameWithoutExtension(outputAsset);
            BlueprintGraphBuilder.PlaceActorsCore(combined, Path.GetFileNameWithoutExtension(asset.File.Path),
                File.ReadAllBytes(_opts.MapTemplate!), outputAsset, targetShort, packageName,
                _opts.CubePath, _opts.ContentRoot);

            entry.Note = string.IsNullOrEmpty(entry.Note) ? "placed-actor editor map"
                                                          : entry.Note + "; placed-actor editor map";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Placed-map write failed for {Path}; using empty placeholder", asset.File.Path);
            return false;
        }
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Array.Copy(a, r, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private ReconstructionResult Route(ParsedAsset asset, string outputNoExt)
    {
        // Fast path: skip the expensive texture-PNG / mesh-glb / sound exports. The real editor-loadable
        // .uasset is still written by TryWriteUncooked; only the media side-export is elided.
        if (_opts.NoMediaExport && asset.PrimaryType is
            "Texture2D" or "TextureCube" or "StaticMesh" or "SkeletalMesh" or "SoundWave")
            return new ReconstructionResult
            {
                Fidelity = Fidelity.Partial,
                Note = $"{asset.PrimaryType}: media export skipped (--no-media-export)",
                Model = new { asset.PrimaryType, asset.File.Path, MediaExportSkipped = true }
            };

        if (asset.IsMap) return _level.Reconstruct(asset, outputNoExt);

        // Any *BlueprintGeneratedClass (Anim/Widget/plain) and UBlueprint route to the BP reconstructor.
        if (asset.PrimaryType.Equals("Blueprint", StringComparison.OrdinalIgnoreCase) ||
            asset.PrimaryType.EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            return _blueprint.Reconstruct(asset, outputNoExt);

        return asset.PrimaryType switch
        {
            "Texture2D" or "TextureCube" => _texture.Reconstruct(asset, outputNoExt),
            "StaticMesh" or "SkeletalMesh" => _mesh.Reconstruct(asset, outputNoExt),
            "Material" or "MaterialInstanceConstant" => _material.Reconstruct(asset, outputNoExt),
            "World" or "Level" => _level.Reconstruct(asset, outputNoExt),
            "SoundWave" => CopyRaw(asset),
            _ => Generic(asset)
        };
    }

    /// <summary>Generic fallback: the full property model is preserved verbatim as JSON.</summary>
    private static ReconstructionResult Generic(ParsedAsset asset) => new()
    {
        Fidelity = Fidelity.Partial,
        Note = $"Generic property model preserved ({asset.Exports.Count} export(s))",
        Model = new { asset.PrimaryType, Exports = asset.Exports }
    };

    /// <summary>Sounds: per scope, preserve the raw model; bulk PCM/OGG is referenced, not transcoded.</summary>
    private static ReconstructionResult CopyRaw(ParsedAsset asset) => new()
    {
        Fidelity = Fidelity.Copied,
        Note = "SoundWave model preserved; raw audio bulk data left as-is",
        Model = new { asset.PrimaryType, Exports = asset.Exports }
    };

    /// <summary>
    /// Map a CUE4Parse virtual path ("GameName/Content/Foo/Bar.uasset") to a Content-relative
    /// path without extension ("Foo/Bar"). Falls back to a flattened path if no /Content/ segment.
    /// </summary>
    public static string MapToContentPath(string virtualPath)
    {
        var noExt = virtualPath.SubstringBeforeLast('.');
        var idx = noExt.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return noExt[(idx + "/Content/".Length)..];

        // Plugin content like "Foo/Plugins/Bar/Content/..." handled above; otherwise keep tail.
        return noExt.TrimStart('/');
    }
}

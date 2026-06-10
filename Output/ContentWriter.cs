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
    private readonly object _manifestLock = new();   // pipeline runs Process in parallel

    /// <summary>Game-module types referenced by imports, keyed "Module.Name" — for stub generation.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, GameStub> GameStubs { get; } = new();

    /// <summary>Inferred base class for a stub, keyed "Module.Name" -> engine base (e.g. "AActor", "USceneComponent").
    /// Filled from how a class is actually USED (placed as a level actor / used as a component), which is far more
    /// reliable than name-suffix heuristics — and getting the actor base right is what lets placed game actors spawn
    /// without crashing the editor.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> StubBaseHints { get; } = new();

    /// <summary>Plugin mount names discovered in the pak (e.g. "CustomMapTools"); each gets a content-only .uplugin
    /// scaffolded so the editor mounts "/&lt;Name&gt;/" and the plugin's cooked references resolve.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> Plugins { get; } = new();

    /// <summary>Write a content-only .uplugin for every discovered plugin so the editor auto-mounts its content root.
    /// No "Modules" entry — these are content-only mounts; native /Script modules are handled separately via stubs.</summary>
    public void ScaffoldPlugins()
    {
        foreach (var name in Plugins.Keys)
        {
            try
            {
                var dir = Path.Combine(_opts.OutputRoot, "Plugins", name);
                Directory.CreateDirectory(Path.Combine(dir, "Content"));
                var uplugin = Path.Combine(dir, name + ".uplugin");
                File.WriteAllText(uplugin,
                    "{\n" +
                    "\t\"FileVersion\": 3,\n" +
                    "\t\"Version\": 1,\n" +
                    "\t\"VersionName\": \"1.0\",\n" +
                    $"\t\"FriendlyName\": \"{name}\",\n" +
                    "\t\"Description\": \"Recovered content-only plugin.\",\n" +
                    "\t\"Category\": \"Recovered\",\n" +
                    "\t\"CanContainContent\": true,\n" +
                    "\t\"IsBetaVersion\": false,\n" +
                    "\t\"Installed\": false\n" +
                    "}\n");
                Log.Information("Scaffolded plugin mount /{Name}/ -> {Up}", name, uplugin);
            }
            catch (Exception ex) { Log.Warning(ex, "Failed to scaffold plugin {Name}", name); }
        }
    }

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
        var (mount, diskBase, relative) = MapToMount(asset.File.Path);
        if (mount != "/Game") Plugins.TryAdd(mount.TrimStart('/'), 0);   // remember plugins to scaffold .uplugin
        var outputNoExt = Path.Combine(_opts.OutputRoot, diskBase, relative);
        var ext = asset.IsMap ? ".umap" : ".uasset";
        var outputAsset = outputNoExt + ext;

        var entry = new ManifestEntry
        {
            VirtualPath = asset.File.Path,
            OutputPath = Path.GetRelativePath(_opts.OutputRoot, outputAsset),
            AssetType = asset.PrimaryType
        };
        lock (_manifestLock) Manifest.Add(entry);

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
        var packageName = mount + "/" + relative.Replace('\\', '/');
        if (asset.IsMap)
        {
            // Maps must NOT go through UncookedPackageWriter: that re-serializes the cooked import table
            // (incl. plugin refs like /CustomMapTools/) which the editor can't resolve -> null-deref crash on load.
            // Route through the filtered PlaceActors path (requires --template + --cube); otherwise write an
            // empty-but-openable placeholder header.
            if (!TryWritePlacedMap(asset, outputAsset, packageName, entry))
                _writer.WriteUAssetHeader(outputAsset, _opts.Game, packageName);
        }
        else if (asset.PrimaryType is "StaticMesh" && !string.IsNullOrWhiteSpace(_opts.CubePath))
        {
            // Real geometry when it converts; otherwise a loadable cube (never the cooked-mobile package,
            // which won't render in-editor). Guarantees every mesh is at least a visible placeholder.
            if (!TryWriteRealMesh(asset, outputAsset, packageName, entry))
            {
                var (mp, mn) = ResolveFirstMaterial(asset.Package as Package);
                var fallbackMats = mp != null && mn != null
                    ? new List<(string, string, string)> { (mp, mn, mn) } : null;
                BlueprintGraphBuilder.CloneMesh(_opts.CubePath!, outputAsset,
                    Path.GetFileNameWithoutExtension(outputAsset), packageName, null, fallbackMats);
                entry.Note = string.IsNullOrEmpty(entry.Note) ? "cube placeholder (mesh did not convert)"
                                                              : entry.Note + "; cube placeholder (mesh did not convert)";
            }
        }
        else if (asset.PrimaryType is "Texture2D" && TryWriteRealTexture(asset, outputAsset, packageName, entry))
        {
            // editor UTexture2D with decoded PNG source written; skip the cooked uncooked write.
        }
        else if (asset.PrimaryType is "Material" or "MaterialInstanceConstant"
                 && TryWriteMaterialAsset(asset, outputAsset, packageName, entry))
        {
            // synth unlit material sampling the asset's first texture written; skip the cooked uncooked write.
        }
        else if (!_opts.SkipBlueprints && IsBlueprintPackage(asset) && IsBlueprintReconstructable(asset)
                 && (_opts.DangerBpGraph || IsOpenSafeBlueprint(asset))
                 && TryWriteBlueprint(asset, outputAsset, packageName, entry))
        {
            // reconstructed editor UBlueprint (+EventGraph) so it's browsable/openable; skip the cooked write.
            // Default only does simple/open-safe BPs; --dangerously-dump-bpgraph forces all (may crash on open).
        }
        else if (!TryWriteUncooked(asset, outputAsset, entry))
            // No editor-loadable form for this type. Do NOT write a stub header: a half-formed .uasset reads as
            // "unrecognizable data" and CRASHES the editor when a map references it, whereas simply omitting the
            // file leaves a harmless broken reference. So skip the write entirely.
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "skipped (no editor-loadable form)"
                                                          : entry.Note + "; skipped (no editor-loadable form)";

        return entry;
    }

    /// <summary>Decode the cooked texture and write an editor-loadable UTexture2D (PNG source). Returns false if
    /// the asset has no loadable UTexture2D (caller falls back).</summary>
    private bool TryWriteRealTexture(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        if (asset.Package is not Package pkg) return false;
        try
        {
            CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D? tex = null;
            for (var i = 0; i < pkg.ExportMap.Length && tex is null; i++)
            {
                if (!pkg.ExportMap[i].ClassName.Contains("Texture2D")) continue;
                try { tex = pkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D; } catch { }
            }
            if (tex is null) return false;
            if (!Writer.TextureWriter.WriteEditorTexture(tex, outputAsset, Path.GetFileNameWithoutExtension(outputAsset), packageName))
                return false;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "editor texture (decoded PNG source)"
                                                          : entry.Note + "; editor texture (decoded PNG source)";
            return true;
        }
        catch (Exception ex) { Log.Warning(ex, "Texture write failed for {Path}", asset.File.Path); return false; }
    }

    /// <summary>Write a synth unlit material that samples the material's first /Game texture (resolved from the
    /// cooked import table — works for both UMaterial and MaterialInstanceConstant). Returns false if no texture
    /// reference is found (caller falls back).</summary>
    private bool TryWriteMaterialAsset(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        if (asset.Package is not Package pkg) return false;
        try
        {
            // Find a Texture2D import + its outer package path. Prefer /Game textures (we dump those).
            string? texPkg = null, texName = null, anyPkg = null, anyName = null;
            foreach (var imp in pkg.ImportMap)
            {
                if (imp.ClassName.Text != "Texture2D") continue;
                var oi = imp.OuterIndex?.Index ?? 0;
                if (oi >= 0) continue;
                var pkgPath = pkg.ImportMap[-oi - 1].ObjectName.Text;
                anyPkg ??= pkgPath; anyName ??= imp.ObjectName.Text;
                if (pkgPath.StartsWith("/Game/")) { texPkg = pkgPath; texName = imp.ObjectName.Text; break; }
            }
            texPkg ??= anyPkg; texName ??= anyName;
            var shortName = Path.GetFileNameWithoutExtension(outputAsset);
            if (texPkg is null || texName is null)
            {
                // No texture reference: write a valid flat material rather than letting it fall through to an
                // unparseable placeholder header (which crashes the editor when a map references it).
                if (!Writer.MaterialWriter.WriteEditorMaterialFlat(outputAsset, shortName, packageName)) return false;
                entry.Note = string.IsNullOrEmpty(entry.Note) ? "flat material (no texture ref)"
                                                              : entry.Note + "; flat material (no texture ref)";
                return true;
            }
            if (!Writer.MaterialWriter.WriteEditorMaterial(outputAsset, shortName, packageName, texPkg, texName))
                return Writer.MaterialWriter.WriteEditorMaterialFlat(outputAsset, shortName, packageName);
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"synth unlit material -> {texName}"
                                                          : entry.Note + $"; synth unlit material -> {texName}";
            return true;
        }
        catch (Exception ex) { Log.Warning(ex, "Material write failed for {Path}", asset.File.Path); return false; }
    }

    /// <summary>Resolve a mesh's first material reference from the cooked import table — the /Game package path +
    /// object name of the MaterialInstanceConstant/Material the StaticMaterials slot points at. The pipeline dumps
    /// a synth material at that path, so redirecting the cube's slot here makes the mesh render with its texture.</summary>
    private static (string? pkg, string? name) ResolveFirstMaterial(Package? pkg)
    {
        if (pkg is null) return (null, null);
        (string p, string n)? any = null;
        foreach (var imp in pkg.ImportMap)
        {
            var cls = imp.ClassName.Text;
            if (cls is not ("MaterialInstanceConstant" or "Material" or "MaterialInstanceDynamic")) continue;
            var oi = imp.OuterIndex?.Index ?? 0;
            if (oi >= 0) continue;
            var p = pkg.ImportMap[-oi - 1].ObjectName.Text;
            any ??= (p, imp.ObjectName.Text);
            if (p.StartsWith("/Game/")) return (p, imp.ObjectName.Text);   // prefer a project material (we dump those)
        }
        return any is { } a ? (a.p, a.n) : (null, null);
    }

    /// <summary>Resolve the mesh's materials in SLOT ORDER (StaticMaterials[i]) -> (packagePath, name, slotName),
    /// so multi-material meshes get one StaticMaterials slot per section. A null/unresolved slot falls back to the
    /// engine DefaultMaterial so the slot count still matches the FRawMesh FaceMaterialIndices.</summary>
    internal static List<(string pkg, string name, string slot)> ResolveMeshMaterials(
        CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh sm, Package pkg)
    {
        var list = new List<(string, string, string)>();
        var slots = sm.StaticMaterials;
        if (slots is null) return list;
        // Map material object name -> its /Game package path FROM THE IMPORT TABLE (these are the editor
        // "/Game/..." paths; ResolvedObject.Outer.Name returns the cooked "Pavlov/Content/..." which won't resolve).
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var imp in pkg.ImportMap)
        {
            if (imp.ClassName.Text is not ("MaterialInstanceConstant" or "Material" or "MaterialInstanceDynamic")) continue;
            var oi = imp.OuterIndex?.Index ?? 0;
            if (oi >= 0) continue;
            byName[imp.ObjectName.Text] = pkg.ImportMap[-oi - 1].ObjectName.Text;   // package path "/Game/.../MI_X"
        }
        foreach (var s in slots)
        {
            var name = s.MaterialInterface?.Name.Text;
            var slot = s.MaterialSlotName.Text;
            if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name!, out var p))
                list.Add((p, name!, string.IsNullOrEmpty(slot) ? name! : slot));
            else
                list.Add(("/Engine/EngineMaterials/DefaultMaterial", "DefaultMaterial", string.IsNullOrEmpty(slot) ? "Material" : slot));
        }
        return list;
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

            // Resolve materials FIRST so the FRawMesh's per-face slot indices can be clamped to the actual slot count.
            var mats = ResolveMeshMaterials(sm, pkg);
            if (mats.Count == 0)
            {
                var (mp, mn) = ResolveFirstMaterial(pkg);
                if (mp != null && mn != null) mats.Add((mp, mn, mn));
            }
            var slotCount = mats.Count > 0 ? mats.Count : 1;   // cube fallback keeps 1 slot when no materials
            var blob = MeshWriter.BuildFRawMesh(cm.LODs[0], slotCount);
            BlueprintGraphBuilder.CloneMesh(_opts.CubePath!, outputAsset, sm.Name, packageName, blob,
                mats.Count > 0 ? mats : null);
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
    /// <summary>A blueprint asset is a package whose primary export is a BlueprintGeneratedClass (cooked BPs carry
    /// the runtime class but the cooker stripped the editor UBlueprint).</summary>
    private static bool IsBlueprintPackage(ParsedAsset asset)
    {
        if (asset.Package is not Package pkg) return false;
        foreach (var e in pkg.ExportMap)
            if (e.ClassName == "BlueprintGeneratedClass") return true;
        return false;
    }

    /// <summary>A reconstructed BP only loads safely when its NATIVE dependencies resolve in the editor. The killer is
    /// a game-native module (/Script/Pavlov*): its UClass/UEnum won't exist, so the editor null-derefs on the
    /// content-browser scan (unresolvable ParentClass). Content references (/Game, /Engine, and now plugin mounts like
    /// /CustomMapTools that we scaffold) are fine even if a given asset is missing — a valid mount root only yields a
    /// broken-ref warning, not a crash. So gate purely on /Script roots: allow engine modules, reject game ones
    /// (those need --emit-stubs + a compiled project). Everything else falls back to the safe cooked write.</summary>
    private bool IsBlueprintReconstructable(ParsedAsset asset)
    {
        if (asset.Package is not Package pkg) return false;
        // When stubs are emitted, every referenced game /Script module gets a compilable stub UCLASS/UENUM/USTRUCT,
        // so the BP's ParentClass/imports resolve and the content-browser scan is safe -> reconstruct all of them.
        if (_opts.EmitStubs) return true;
        foreach (var imp in pkg.ImportMap)
        {
            if (imp.ClassName.Text != "Package") continue;             // only top-level package refs
            var p = imp.ObjectName.Text;
            if (!p.StartsWith("/Script/")) continue;                   // content root (/Game, /Engine, /<plugin>) -> ok
            if (!EngineModules.Contains(p["/Script/".Length..]))
                return false;                                          // game-native module -> would crash without a stub
        }
        return true;
    }

    /// <summary>The cooked-guts clone opens cleanly only for SIMPLE blueprints. Complex ones (lots of functions/
    /// bytecode, big graphs) and special asset types carry cooked-only internals that make the BP editor recurse and
    /// crash on open. So the default reconstructs only blueprints that are: a plain BlueprintGeneratedClass (not
    /// Widget/Anim — those need their own asset type + tree), with few function exports and a small export table
    /// (data/config/interface/simple-actor BPs). The rest stay cooked-safe unless --dangerously-dump-bpgraph is set.</summary>
    private static bool IsOpenSafeBlueprint(ParsedAsset asset)
    {
        if (asset.Package is not Package pkg) return false;
        int funcs = 0;
        foreach (var e in pkg.ExportMap)
        {
            var c = e.ClassName;
            if (c is "WidgetBlueprintGeneratedClass" or "AnimBlueprintGeneratedClass") return false;  // need WidgetBlueprint/AnimBlueprint
            if (c == "Function") funcs++;
        }
        return pkg.ExportMap.Length <= 24 && funcs <= 6;   // simple BPs only; complex bytecode graphs crash on open
    }

    /// <summary>Reconstruct an editor-openable UBlueprint (+EventGraph) from the cooked BP so it shows in the content
    /// browser. Falls back (returns false) on any failure -> plain uncooked write.</summary>
    private bool TryWriteBlueprint(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        if (asset.Package is not Package) return false;
        try
        {
            var parts = _provider.SavePackage(asset.File.Path);
            var head = parts.FirstOrDefault(p => p.Key.EndsWith(".uasset") || p.Key.EndsWith(".umap")).Value
                       ?? parts.Values.First();
            var uexp = parts.FirstOrDefault(p => p.Key.EndsWith(".uexp")).Value;
            var combined = uexp is null ? head : Concat(head, uexp);

            // _provider carries ReadScriptData (set when blueprints aren't skipped) -> enables ubergraph recovery.
            if (!BlueprintGraphBuilder.BuildCore(combined, Path.GetFileNameWithoutExtension(outputAsset), packageName, outputAsset, _provider))
                return false;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "reconstructed editor blueprint (UBlueprint+EventGraph)"
                                                          : entry.Note + "; reconstructed editor blueprint";
            return true;
        }
        catch (Exception ex) { Log.Warning(ex, "Blueprint reconstruct failed for {Path}", asset.File.Path); return false; }
    }

    private bool TryWriteUncooked(ParsedAsset asset, string outputAsset, ManifestEntry entry)
    {
        if (asset.Package is not Package pkg) return false;                 // IoStore not supported by writer
        // A blueprint that wasn't reconstructed above is NOT open-safe: a plain cooked BP (BlueprintGeneratedClass
        // with no editor UBlueprint) CRASHES the editor when opened/scanned (deep recursion in CoreUObject). Skip it
        // entirely — a missing asset is a harmless broken reference; a half-formed cooked BP is a crash.
        if (IsBlueprintPackage(asset)) return false;
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
            // Record the engine base each placed game class needs (actor->AActor, component->USceneComponent) so the
            // stub generator emits a spawnable class instead of a UObject the editor crashes trying to place.
            void OnGameClass(string scriptPkg, string cls, string baseClass)
            {
                if (!scriptPkg.StartsWith("/Script/", StringComparison.Ordinal)) return;
                var module = scriptPkg["/Script/".Length..];
                if (EngineModules.Contains(module)) return;       // real engine class, no stub needed
                StubBaseHints[$"{module}.{cls}"] = baseClass;
            }
            BlueprintGraphBuilder.PlaceActorsCore(combined, Path.GetFileNameWithoutExtension(asset.File.Path),
                File.ReadAllBytes(_opts.MapTemplate!), outputAsset, targetShort, packageName,
                _opts.CubePath, _opts.ContentRoot, OnGameClass);

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
    public static string MapToContentPath(string virtualPath) => MapToMount(virtualPath).relative;

    /// <summary>Resolve a pak virtual path to its editor mount: game content -> ("/Game", "Content", rest) but plugin
    /// content "Foo/Plugins/&lt;Name&gt;/Content/rest" -> ("/&lt;Name&gt;", "Plugins/&lt;Name&gt;/Content", rest).
    /// Preserving the plugin mount means cooked references like "/CustomMapTools/Blueprints/X" resolve in-editor
    /// (flattening them all into /Game breaks those refs and collides folders).</summary>
    public static (string mount, string diskBase, string relative) MapToMount(string virtualPath)
    {
        var noExt = virtualPath.SubstringBeforeLast('.');
        var pIdx = noExt.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase);
        if (pIdx >= 0)
        {
            var afterPlugins = noExt[(pIdx + "/Plugins/".Length)..];        // <Name>[/Sub]/Content/<rest>
            var cIdx = afterPlugins.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (cIdx >= 0)
            {
                var pluginSeg = afterPlugins[..cIdx];                        // <Name> or nested dir
                var name = pluginSeg.Contains('/') ? pluginSeg[(pluginSeg.LastIndexOf('/') + 1)..] : pluginSeg;
                var rest = afterPlugins[(cIdx + "/Content/".Length)..];
                if (!string.IsNullOrEmpty(name))
                    return ("/" + name, Path.Combine("Plugins", name, "Content"), rest);
            }
        }
        var idx = noExt.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        var relative = idx >= 0 ? noExt[(idx + "/Content/".Length)..] : noExt.TrimStart('/');
        return ("/Game", "Content", relative);
    }
}

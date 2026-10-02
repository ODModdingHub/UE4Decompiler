using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using Serilog;
using System.Text.RegularExpressions;
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
    private readonly DataTableReconstructor _dataTable = new();
    private readonly StringTableReconstructor _stringTable = new();
    private readonly CurveReconstructor _curve = new();
    private readonly AudioReconstructor _audio = new();
    private readonly InputReconstructor _input = new();
    private readonly AnimationReconstructor _anim = new();
    private readonly WidgetReconstructor _widget = new();
    private readonly PhysicsReconstructor _physics = new();
    private readonly ParticleReconstructor _particle = new();
    private readonly GameplayAbilityReconstructor _gas = new();
    private readonly FoliageReconstructor _foliage = new();
    private readonly LandscapeReconstructor _landscape = new();
    private readonly SubsurfaceReconstructor _subsurface = new();
    private readonly MediaReconstructor _media = new();
    private readonly IKRigReconstructor _ikrig = new();

    public List<ManifestEntry> Manifest { get; } = new();
    private readonly object _manifestLock = new();   // pipeline runs Process in parallel

    /// <summary>GameplayTags discovered across all packages during processing, for DefaultGameplayTags.ini scaffolding.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> DiscoveredGameplayTags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collision profiles discovered across all packages during processing, for DefaultEngine.ini scaffolding.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> DiscoveredCollisionProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom collision channels discovered across all packages, for DefaultEngine.ini scaffolding.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> DiscoveredCollisionChannels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom physical surface types discovered across all packages, for DefaultEngine.ini scaffolding.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> DiscoveredPhysicalSurfaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Game-module types referenced by imports, keyed "Module.Name" — for stub generation.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, GameStub> GameStubs { get; } = new();

    /// <summary>Inferred base class for a stub, keyed "Module.Name" -> engine base (e.g. "AActor", "USceneComponent").
    /// Filled from how a class is actually USED (placed as a level actor / used as a component), which is far more
    /// reliable than name-suffix heuristics — and getting the actor base right is what lets placed game actors spawn
    /// without crashing the editor.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> StubBaseHints { get; } = new();

    /// <summary>Recovered game-native methods referenced by BP bytecode, keyed "Module.Class:Function".
    /// These are only emitted when --emit-stub-methods is enabled; name-only functions are experimental.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> StubMethodHints { get; } = new();
    /// <summary>Graph-recovered stub signatures: "Module.Class:func" -> params + return (pin names sanitized
    /// to C++ identifiers, categories as emitted). First call-site shape wins; conflicts keep the first.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Output.Stubs.StubMethodSig> StubMethodSigs { get; } = new();

    public System.Collections.Concurrent.ConcurrentBag<BlueprintReparentRequest> BlueprintReparentRequests { get; } = new();

    /// <summary>Plugin mount names discovered in the pak (e.g. "CustomMapTools"); each gets a content-only .uplugin
    /// scaffolded so the editor mounts "/&lt;Name&gt;/" and the plugin's cooked references resolve.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> Plugins { get; } = new();

    /// <summary>Output /Game package paths already written (case-insensitive). UE registers packages by a
    /// case-insensitive PackageId, so two sources that resolve to the same package name (case-variant folders,
    /// name-munged collisions) crash with "FPackageId collision". We write each package name once.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _writtenPackages =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For every discovered plugin mount: if it's an ENGINE plugin (e.g. IKRig, ControlRig), enable it in the
    /// .uproject so the editor loads the REAL plugin (content + compiled module). Otherwise it's a game/content plugin
    /// (e.g. CustomMapTools) — write a content-only .uplugin (no Modules) so the editor mounts "/&lt;Name&gt;/".
    /// Shadowing an engine plugin with a module-less local copy is what made the editor fail with
    /// "Plugin 'IKRig' failed to load because module 'IKRig' could not be loaded".</summary>
    public void ScaffoldPlugins()
    {
        var enginePlugins = DiscoverEnginePluginNames();
        foreach (var name in Plugins.Keys)
        {
            try
            {
                if (enginePlugins.Contains(name))
                {
                    // Engine plugin (IKRig/ControlRig/etc.): do NOT create a local shadow (it lacks the real module ->
                    // editor crash) AND do NOT enable it in the .uproject — enabling a CODE plugin makes UBT try to
                    // build its module during compile, which fails ("ControlRig does not contain the module"). These
                    // plugins are default-enabled in 5.x anyway, so we just leave them: content mounts, build is clean.
                    Log.Information("Plugin mount /{Name}/ is an engine plugin -> left to the engine (no shadow, no .uproject enable)", name);
                    continue;
                }
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

    /// <summary>Names of plugins the installed engine already provides (folder names of every Engine/Plugins/**.uplugin).
    /// Shadowing one of these locally breaks module loading; instead we enable it in the .uproject. Falls back to a
    /// curated set if the engine dir can't be located.</summary>
    private HashSet<string> DiscoverEnginePluginNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Curated fallback (common content-bearing 5.x engine plugins) in case the engine dir isn't found.
            "IKRig","ControlRig","RigLogic","Water","Landmass","Niagara","ChaosCloth","ChaosClothEditor",
            "Text3D","GeometryScripting","MeshModelingToolset","Bridge","MetaHuman","AnimationData",
            "GeometryCollectionPlugin","FullBodyIK","PoseSearch","MLDeformerFramework","DeformerGraph",
        };
        try
        {
            var assoc = _opts.EngineAssociation;   // e.g. "5.1"
            var candidates = new[]
            {
                $@"C:\Program Files\Epic Games\UE_{assoc}\Engine\Plugins",
                $@"D:\Program Files\Epic Games\UE_{assoc}\Engine\Plugins",
                $@"C:\Epic Games\UE_{assoc}\Engine\Plugins",
            };
            foreach (var root in candidates)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var up in Directory.EnumerateFiles(root, "*.uplugin", SearchOption.AllDirectories))
                    set.Add(Path.GetFileNameWithoutExtension(up));
                break;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Engine-plugin discovery failed; using curated set"); }
        return set;
    }

    /// <summary>Merge {"Name":n,"Enabled":true} entries into the .uproject's Plugins array.</summary>
    private void EnableEnginePluginsInUProject(List<string> names)
    {
        try
        {
            var uproject = Directory.EnumerateFiles(_opts.OutputRoot, "*.uproject").FirstOrDefault();
            if (uproject is null) { Log.Warning("No .uproject found to enable engine plugins {N}", string.Join(",", names)); return; }
            var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(uproject));
            var arr = root["Plugins"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();
            var existing = new HashSet<string>(arr.Select(p => (string?)p["Name"] ?? "").Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                if (existing.Contains(n)) continue;
                arr.Add(new Newtonsoft.Json.Linq.JObject { ["Name"] = n, ["Enabled"] = true });
            }
            root["Plugins"] = arr;
            File.WriteAllText(uproject, root.ToString(Newtonsoft.Json.Formatting.Indented));
            Log.Information("Enabled {N} engine plugin(s) in {U}: {List}", names.Count, Path.GetFileName(uproject), string.Join(", ", names));
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to enable engine plugins in .uproject"); }
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

    // Gather game-native /Script class refs for --emit-stubs. Uniform (Zen + legacy): walk the loaded exports'
    // resolved Class (catches native /Script/<game> actors placed in maps) + BP ParentClass. The legacy import-table
    // scan only worked for Package; Zen (IoStore) has a different import format, so nothing was stubbed -> "no source".
    private void CollectGameTypes(ParsedAsset asset)
    {
        foreach (var e in asset.Exports)
        {
            AddGameStub(e.Class, InferEngineBase(e.Class));
            var parentClass = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParentClass")?.ResolvedObject;
            AddGameStub(parentClass, InferEngineBase(parentClass));  // editor UBlueprint parent
            if (e is CUE4Parse.UE4.Objects.UObject.UStruct s)
            {
                var super = s.SuperStruct.ResolvedObject;
                AddGameStub(super, InferEngineBase(super));          // cooked BGC/native parent
            }
        }
    }

    private void CollectGameplayTags(ParsedAsset asset)
    {
        foreach (var e in asset.Exports)
        {
            if (e.Properties == null) continue;
            foreach (var prop in e.Properties)
            {
                try
                {
                    var val = prop.Tag?.GenericValue;
                    if (val is CUE4Parse.UE4.Assets.Objects.FStructFallback fb)
                    {
                        var tName = fb.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("TagName").Text;
                        if (!string.IsNullOrEmpty(tName) && tName != "None")
                            DiscoveredGameplayTags.TryAdd(tName, 0);

                        var tags = fb.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName[]>("GameplayTags");
                        if (tags != null)
                        {
                            foreach (var t in tags)
                                if (!string.IsNullOrEmpty(t.Text) && t.Text != "None")
                                    DiscoveredGameplayTags.TryAdd(t.Text, 0);
                        }
                    }
                    else if (val is CUE4Parse.UE4.Objects.UObject.FName fn && prop.Name.Text.Contains("Tag", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = fn.Text;
                        if (!string.IsNullOrEmpty(t) && t != "None" && t.Contains('.'))
                            DiscoveredGameplayTags.TryAdd(t, 0);
                    }
                }
                catch { }
            }
        }
    }

    private void CollectCollisionData(ParsedAsset asset)
    {
        foreach (var e in asset.Exports)
        {
            if (e.Properties == null) continue;
            foreach (var prop in e.Properties)
            {
                try
                {
                    if (prop.Name.Text == "CollisionProfileName" && prop.Tag?.GenericValue is CUE4Parse.UE4.Objects.UObject.FName fn)
                    {
                        var profile = fn.Text;
                        if (!string.IsNullOrEmpty(profile) && profile != "None" && profile != "Custom")
                            DiscoveredCollisionProfiles.TryAdd(profile, 0);
                    }
                    else if (prop.Name.Text == "CollisionResponses" && prop.Tag?.GenericValue is CUE4Parse.UE4.Assets.Objects.FStructFallback fb)
                    {
                        var responses = fb.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ResponseToChannels");
                        if (responses != null)
                        {
                            foreach (var resp in responses)
                            {
                                var ch = resp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Channel").Text;
                                if (!string.IsNullOrEmpty(ch) && ch.StartsWith("ECC_GameTraceChannel", StringComparison.OrdinalIgnoreCase))
                                    DiscoveredCollisionChannels.TryAdd(ch, "ECR_Block");
                            }
                        }
                    }
                    else if (prop.Name.Text == "SurfaceType" && prop.Tag?.GenericValue is CUE4Parse.UE4.Objects.UObject.FName stName)
                    {
                        var st = stName.Text;
                        if (!string.IsNullOrEmpty(st) && !st.Equals("SurfaceType_Default", StringComparison.OrdinalIgnoreCase))
                            DiscoveredPhysicalSurfaces.TryAdd(st, Path.GetFileNameWithoutExtension(asset.File.Path));
                    }
                }
                catch { }
            }
        }
        foreach (var kvp in PhysicsReconstructor.DiscoveredPhysicalSurfaces)
            DiscoveredPhysicalSurfaces.TryAdd(kvp.Key, kvp.Value);
    }

    private void AddGameStub(CUE4Parse.UE4.Assets.ResolvedObject? ro, string? baseHint = null)
    {
        var path = ro?.GetPathName();
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/Script/", StringComparison.Ordinal)) return;
        var dot = path.IndexOf('.');                                  // "/Script/<Module>.<Name>"
        if (dot < 0) return;
        var module = path.Substring("/Script/".Length, dot - "/Script/".Length);
        // Skip engine AND engine-plugin modules (ControlRig, EnhancedInput, InterchangePipelines, WebBrowserWidget...)
        // — stubbing those as project modules collides with the real engine modules and breaks the build.
        if (EngineModuleNames().Contains(module)) return;
        var name = path[(dot + 1)..];
        if (!IsCppIdentifier(name) || !IsCppIdentifier(module)) return;
        var key = $"{module}.{name}";
        GameStubs.TryAdd(key, new GameStub(module, name, "Class"));
        if (!string.IsNullOrWhiteSpace(baseHint)) SetStubBaseHint(key, baseHint);
    }

    private static bool IsCppIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!(value[0] == '_' || char.IsLetter(value[0]))) return false;
        for (var i = 1; i < value.Length; i++)
            if (!(value[i] == '_' || char.IsLetterOrDigit(value[i]))) return false;
        return true;
    }

    private string? AddGameStubMethod(string scriptPkg, string cls, string func)
    {
        if (!scriptPkg.StartsWith("/Script/", StringComparison.Ordinal)) return null;
        var module = scriptPkg["/Script/".Length..];
        if (EngineModuleNames().Contains(module)) return null;
        if (IsDelegateSignatureFunction(func)) return null;
        if (IsReservedStubMethod(func)) return null;
        if (!IsCppIdentifier(module) || !IsCppIdentifier(cls) || !IsCppIdentifier(func)) return null;

        var classKey = $"{module}.{cls}";
        GameStubs.TryAdd(classKey, new GameStub(module, cls, "Class"));
        StubMethodHints.TryAdd($"{classKey}:{func}", 0);
        return classKey;
    }

    /// <summary>Engine-event names a stub must never redeclare (UHT forbids shadowing base UFUNCTIONs).</summary>
    private static bool IsReservedStubMethod(string func) =>
        func.StartsWith("ExecuteUbergraph", StringComparison.Ordinal)
        || func is "ReceiveBeginPlay" or "ReceiveTick" or "ReceiveEndPlay" or "ReceiveDestroyed"
            or "ReceivePossess" or "ReceiveUnpossess" or "UserConstructionScript"
            or "ReceiveActorBeginOverlap" or "ReceiveActorEndOverlap" or "ReceiveHit"
            or "ReceiveAnyDamage" or "ReceivePointDamage" or "ReceiveRadialDamage";

    /// <summary>Collect stub signatures from decompiled graph calls: the stub UFUNCTIONs are generated with
    /// these exact pin names/types so emitted call pins bind instead of dangling. Skips engine, self and
    /// reserved targets. Also ensures class stubs for pin sub-category objects (struct/class paths).</summary>
    private void CollectGraphStubSignatures(IEnumerable<Reconstructors.FunctionGraph> graphs, string packageName, string shortName)
    {
        int seenCalls = 0, addedSigs = 0;
        foreach (var g in graphs)
            foreach (var n in g.Nodes)
            {
                if (n.K2Class is not ("K2Node_CallFunction" or "K2Node_CallParentFunction")) continue;
                if (string.IsNullOrWhiteSpace(n.FuncPkg) || string.IsNullOrWhiteSpace(n.FuncClass)
                    || string.IsNullOrWhiteSpace(n.FuncName)) continue;
                if (n.FuncPkg == "/Script/Engine") continue;
                if (n.FuncPkg.Equals(packageName, StringComparison.OrdinalIgnoreCase)
                    && n.FuncClass.Equals(shortName + "_C", StringComparison.OrdinalIgnoreCase)) continue;
                seenCalls++;
                var classKey = AddGameStubMethod(n.FuncPkg, n.FuncClass, n.FuncName);
                if (classKey is null) { Log.Information("STUBSKIP {Pkg}.{Cls}:{Func}", n.FuncPkg, n.FuncClass, n.FuncName); continue; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ps = new List<(string Name, string Cat, byte Cont)>();
                foreach (var p in n.Inputs)
                {
                    if (p.Category == "exec" || p.Direction != 0) continue;
                    if (p.Name.Equals("self", StringComparison.OrdinalIgnoreCase)) continue;
                    var cn = SanitizeCppParam(p.Name, ps.Count, seen);
                    if (cn is null) continue;
                    ps.Add((cn, p.Category, p.Container));
                    EnsurePinTypeStub(p.SubCategoryObjPath);
                }
                (string Name, string Cat)? ret = null;
                var outs = new List<(string Name, string Cat)>();
                foreach (var o in n.Outputs.Where(p => p.Category != "exec" && p.Direction == 1))
                {
                    var on = SanitizeCppParam(o.Name, ps.Count + outs.Count, seen);
                    if (on is null) continue;
                    EnsurePinTypeStub(o.SubCategoryObjPath);
                    if (ret is null && o.Name == "ReturnValue") ret = (on, o.Category);
                    else outs.Add((on, o.Category));
                }
                bool staticCall = !n.DataLinks.ContainsKey("self");
                StubMethodSigs.TryAdd($"{classKey}:{n.FuncName}", new Output.Stubs.StubMethodSig(ps, ret, staticCall, outs));
                addedSigs++;
            }
        if (seenCalls > 0)
            Log.Information("STUBSIG {Pkg}: {Seen} game calls, {Added} sigs", packageName, seenCalls, addedSigs);
    }

    private void EnsurePinTypeStub(string? objPath)
    {
        if (string.IsNullOrWhiteSpace(objPath) || !objPath.StartsWith("/Script/", StringComparison.Ordinal)) return;
        var dot = objPath.IndexOf('.');
        if (dot < 0) return;
        var module = objPath.Substring("/Script/".Length, dot - "/Script/".Length);
        if (EngineModuleNames().Contains(module)) return;
        var name = objPath[(dot + 1)..];
        if (!IsCppIdentifier(name) || !IsCppIdentifier(module)) return;
        GameStubs.TryAdd($"{module}.{name}", new GameStub(module, name, "Class"));
    }

    private static readonly HashSet<string> CppKeywords = new(StringComparer.Ordinal)
    {
        "class", "template", "typename", "new", "delete", "operator", "namespace", "public", "private",
        "protected", "virtual", "override", "static", "const", "int", "float", "bool", "char", "void",
        "return", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue",
        "struct", "enum", "union", "typedef", "using", "this", "true", "false", "nullptr", "auto",
        "register", "extern", "inline", "friend", "explicit", "mutable", "volatile", "sizeof",
    };

    private static string? SanitizeCppParam(string name, int idx, HashSet<string> seen)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            if (c == '_' || char.IsLetterOrDigit(c)) sb.Append(c);
        var cn = sb.ToString();
        if (cn.Length == 0) cn = "P" + idx;
        else if (char.IsDigit(cn[0])) cn = "_" + cn;
        if (CppKeywords.Contains(cn)) cn += "_";
        var base2 = cn; int k = 2;
        while (!seen.Add(cn)) cn = $"{base2}_{k++}";
        return cn;
    }

    private void CollectGameStubMethods(IEnumerable<(string scriptPkg, string cls, string func)> calls)
    {
        foreach (var (scriptPkg, cls, func) in calls)
            AddGameStubMethod(scriptPkg, cls, func);
    }

    private static bool IsDelegateSignatureFunction(string name) =>
        name.EndsWith("__DelegateSignature", StringComparison.Ordinal)
        || name.EndsWith("_DelegateSignature", StringComparison.Ordinal);

    private void SetStubBaseHint(string key, string baseClass)
    {
        StubBaseHints.AddOrUpdate(key, baseClass, (_, old) => BaseHintRank(baseClass) > BaseHintRank(old) ? baseClass : old);
    }

    private static int BaseHintRank(string baseClass) => baseClass switch
    {
        "ACharacter" => 70,
        "APlayerController" or "AAIController" => 65,
        "AController" => 60,
        "APawn" => 55,
        "AGameModeBase" or "AGameStateBase" or "APlayerState" or "APlayerCameraManager" or "ALevelScriptActor" or "AHUD" => 50,
        "AActor" or "AVolume" or "ASkyLight" or "ADirectionalLight" or "APointLight" or "ASpotLight" or "ARectLight" or "AExponentialHeightFog" or "ASkyAtmosphere" or "AVolumetricCloud" or "APostProcessVolume" => 40,
        "UStaticMeshComponent" or "UPointLightComponent" or "USpotLightComponent" or "UDirectionalLightComponent" or "USkyLightComponent" or "URectLightComponent" or "UExponentialHeightFogComponent" or "USkyAtmosphereComponent" or "UVolumetricCloudComponent" or "UPostProcessComponent" => 35,
        "USceneComponent" => 30,
        "UActorComponent" => 25,
        "UUserWidget" or "UAnimInstance" or "UGameInstance" or "USaveGame" or "UDataAsset" => 20,
        "UObject" => 0,
        _ => 10
    };

    private static readonly Dictionary<string, string> EngineBaseByPath = new(StringComparer.Ordinal)
    {
        ["/Script/Engine.Actor"] = "AActor",
        ["/Script/Engine.Pawn"] = "APawn",
        ["/Script/Engine.Character"] = "ACharacter",
        ["/Script/Engine.Controller"] = "AController",
        ["/Script/Engine.PlayerController"] = "APlayerController",
        ["/Script/AIModule.AIController"] = "AAIController",
        ["/Script/Engine.PlayerState"] = "APlayerState",
        ["/Script/Engine.GameModeBase"] = "AGameModeBase",
        ["/Script/Engine.GameStateBase"] = "AGameStateBase",
        ["/Script/Engine.HUD"] = "AHUD",
        ["/Script/Engine.Volume"] = "AVolume",
        ["/Script/Engine.LevelScriptActor"] = "ALevelScriptActor",
        ["/Script/Engine.PlayerCameraManager"] = "APlayerCameraManager",
        ["/Script/Engine.ActorComponent"] = "UActorComponent",
        ["/Script/Engine.SceneComponent"] = "USceneComponent",
        ["/Script/Engine.StaticMeshComponent"] = "UStaticMeshComponent",
        ["/Script/Engine.LightComponent"] = "ULightComponent",
        ["/Script/Engine.PointLightComponent"] = "UPointLightComponent",
        ["/Script/Engine.SpotLightComponent"] = "USpotLightComponent",
        ["/Script/Engine.DirectionalLightComponent"] = "UDirectionalLightComponent",
        ["/Script/Engine.SkyLightComponent"] = "USkyLightComponent",
        ["/Script/Engine.RectLightComponent"] = "URectLightComponent",
        ["/Script/Engine.ExponentialHeightFogComponent"] = "UExponentialHeightFogComponent",
        ["/Script/Engine.SkyAtmosphereComponent"] = "USkyAtmosphereComponent",
        ["/Script/Engine.VolumetricCloudComponent"] = "UVolumetricCloudComponent",
        ["/Script/Engine.PostProcessComponent"] = "UPostProcessComponent",
        ["/Script/Engine.SkyLight"] = "ASkyLight",
        ["/Script/Engine.DirectionalLight"] = "ADirectionalLight",
        ["/Script/Engine.PointLight"] = "APointLight",
        ["/Script/Engine.SpotLight"] = "ASpotLight",
        ["/Script/Engine.RectLight"] = "ARectLight",
        ["/Script/Engine.ExponentialHeightFog"] = "AExponentialHeightFog",
        ["/Script/Engine.SkyAtmosphere"] = "ASkyAtmosphere",
        ["/Script/Engine.VolumetricCloud"] = "AVolumetricCloud",
        ["/Script/Engine.PostProcessVolume"] = "APostProcessVolume",
        ["/Script/Engine.GameInstance"] = "UGameInstance",
        ["/Script/Engine.GameUserSettings"] = "UGameUserSettings",
        ["/Script/Engine.LocalPlayer"] = "ULocalPlayer",
        ["/Script/Engine.SaveGame"] = "USaveGame",
        ["/Script/Engine.DataAsset"] = "UDataAsset",
        ["/Script/Engine.AnimInstance"] = "UAnimInstance",
        ["/Script/UMG.UserWidget"] = "UUserWidget",
        ["/Script/CoreUObject.Interface"] = "UInterface",
        ["/Script/CoreUObject.Object"] = "UObject",
    };

    private static string? InferEngineBase(CUE4Parse.UE4.Assets.ResolvedObject? ro)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = ro; cur != null && seen.Count < 64; )
        {
            var path = cur.GetPathName();
            if (!seen.Add(path)) return null;
            if (EngineBaseByPath.TryGetValue(path, out var engineBase)) return engineBase;

            if (cur.TryLoad<CUE4Parse.UE4.Objects.UObject.UStruct>(out var s))
            {
                cur = s.SuperStruct?.ResolvedObject;
                continue;
            }

            cur = cur.Super;
        }
        return null;
    }

    private HashSet<string>? _engineModuleNames;
    /// <summary>All module names the engine provides: core engine modules + every module declared by an engine plugin
    /// (scanned from Engine/Plugins/**.uplugin). Stubbing any of these as a project module collides with the engine.</summary>
    private HashSet<string> EngineModuleNames()
    {
        if (_engineModuleNames != null) return _engineModuleNames;
        var set = new HashSet<string>(EngineModules, StringComparer.OrdinalIgnoreCase);
        try
        {
            var assoc = _opts.EngineAssociation;
            var engineRoot = new[] { $@"C:\Program Files\Epic Games\UE_{assoc}\Engine",
                                     $@"D:\Program Files\Epic Games\UE_{assoc}\Engine",
                                     $@"C:\Epic Games\UE_{assoc}\Engine" }.FirstOrDefault(Directory.Exists);
            if (engineRoot != null)
            {
                // Every engine module ships a "<ModuleName>.Build.cs". Collect them ALL from both Engine/Source AND
                // Engine/Plugins — engine RUNTIME modules (NNE, ChaosSolverEngine, ...) live in Engine/Source, not just
                // plugins, so a Plugins-only/.uplugin scan misses them and we'd stub them as project modules. Creating
                // a Source/<EngineModule> shadow then breaks UBT ("engine plugin should not reference project module").
                int added = 0;
                foreach (var sub in new[] { "Source", "Plugins" })
                {
                    var dir = Path.Combine(engineRoot, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var bcs in Directory.EnumerateFiles(dir, "*.Build.cs", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(bcs);
                        if (fn.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase) && set.Add(fn[..^".Build.cs".Length]))
                            added++;
                    }
                }
                Log.Information("Engine module scan: {N} module(s) from {Root}", added, engineRoot);
            }
            else
                Log.Warning("Engine root for UE_{Assoc} not found; engine-module exclusion uses the core set only (game stubs may shadow engine modules)", assoc);
        }
        catch (Exception ex) { Log.Warning(ex, "Engine module scan failed; using core set only"); }
        _engineModuleNames = set;
        return set;
    }

    private sealed record NativeSourceClassInfo(string Module, string ScriptName, string CppName, string BaseCppName, string? EngineBase);

    private Dictionary<string, NativeSourceClassInfo>? _nativeSourceClasses;
    private Dictionary<string, NativeSourceClassInfo> NativeSourceClasses()
    {
        if (_nativeSourceClasses != null) return _nativeSourceClasses;

        var byPath = new Dictionary<string, NativeSourceClassInfo>(StringComparer.OrdinalIgnoreCase);
        var byCpp = new Dictionary<string, NativeSourceClassInfo>(StringComparer.Ordinal);
        foreach (var (module, dir) in EnumerateNativeSourceModuleDirs())
        {
            foreach (var header in Directory.EnumerateFiles(dir, "*.h", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(header); }
                catch { continue; }

                foreach (Match m in SourceClassDeclarationRegex.Matches(text))
                {
                    var cpp = m.Groups["cls"].Value;
                    var baseCpp = m.Groups["base"].Value;
                    if (string.IsNullOrWhiteSpace(cpp) || string.IsNullOrWhiteSpace(baseCpp)) continue;
                    var scriptName = ToUnrealScriptClassName(cpp);
                    if (!IsCppIdentifier(module) || !IsCppIdentifier(scriptName)) continue;

                    var info = new NativeSourceClassInfo(module, scriptName, cpp, baseCpp, null);
                    byCpp[cpp] = info;
                    byPath[$"/Script/{module}.{scriptName}"] = info;
                }
            }
        }

        foreach (var (path, info) in byPath.ToArray())
            byPath[path] = info with { EngineBase = ResolveNativeEngineBase(info.BaseCppName, byCpp) };

        if (byPath.Count > 0)
            Log.Information("Recovered native source scan: {N} UCLASS(es) available for BP reparenting", byPath.Count);
        _nativeSourceClasses = byPath;
        return byPath;
    }

    private static readonly Regex SourceClassDeclarationRegex = new(
        @"\bclass\s+(?:(?:[A-Za-z_][A-Za-z0-9_]*_API)\s+)?(?<cls>[AU][A-Za-z_][A-Za-z0-9_]*)\s*:\s*public\s+(?<base>[AU][A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private IEnumerable<(string module, string dir)> EnumerateNativeSourceModuleDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { _opts.NativeSourcePath, Path.Combine(_opts.OutputRoot, "Source") })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full)) continue;

            foreach (var item in EnumerateNativeSourceModuleDirs(full))
            {
                if (seen.Add(item.dir)) yield return item;
            }
        }
    }

    private static IEnumerable<(string module, string dir)> EnumerateNativeSourceModuleDirs(string full)
    {
        foreach (var buildCs in Directory.EnumerateFiles(full, "*.Build.cs", SearchOption.TopDirectoryOnly))
        {
            yield return (Path.GetFileName(buildCs)[..^".Build.cs".Length], full);
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            var buildCs = Directory.EnumerateFiles(dir, "*.Build.cs", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (buildCs != null) yield return (Path.GetFileName(buildCs)[..^".Build.cs".Length], dir);
        }
    }

    private static string ToUnrealScriptClassName(string cppName) =>
        cppName.Length > 1 && (cppName[0] == 'A' || cppName[0] == 'U') ? cppName[1..] : cppName;

    private static string? ResolveNativeEngineBase(string cppBase, IReadOnlyDictionary<string, NativeSourceClassInfo> byCpp, int depth = 0)
    {
        if (depth > 32 || string.IsNullOrWhiteSpace(cppBase)) return null;
        var direct = cppBase switch
        {
            "ACharacter" => "ACharacter",
            "APlayerController" => "APlayerController",
            "AAIController" => "AAIController",
            "AController" => "AController",
            "APawn" => "APawn",
            "AGameModeBase" or "AGameMode" => "AGameModeBase",
            "AGameStateBase" or "AGameState" => "AGameStateBase",
            "APlayerState" => "APlayerState",
            "APlayerCameraManager" => "APlayerCameraManager",
            "ALevelScriptActor" => "ALevelScriptActor",
            "AHUD" => "AHUD",
            "AVolume" or "ATriggerVolume" => "AVolume",
            "AActor" => "AActor",
            "UUserWidget" => "UUserWidget",
            "UAnimInstance" => "UAnimInstance",
            "USceneComponent" or "UStaticMeshComponent" or "ULightComponent" or "UPointLightComponent" or
                "USpotLightComponent" or "UDirectionalLightComponent" or "USkyLightComponent" or
                "URectLightComponent" or "UExponentialHeightFogComponent" or "USkyAtmosphereComponent" or
                "UVolumetricCloudComponent" or "UPostProcessComponent" or "UTextRenderComponent" or "UPoseableMeshComponent" => "USceneComponent",
            "UActorComponent" or "UMovementComponent" => "UActorComponent",
            "UGameInstance" => "UGameInstance",
            "USaveGame" => "USaveGame",
            "UDataAsset" or "UPrimaryDataAsset" => "UDataAsset",
            "UObject" => "UObject",
            _ => null
        };
        if (direct != null) return direct;

        if (byCpp.TryGetValue(cppBase, out var sourceBase))
            return ResolveNativeEngineBase(sourceBase.BaseCppName, byCpp, depth + 1);

        return GuessEngineBaseFromClassName(cppBase);
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

        // Skip if another source already produced this exact /Game package name (case-insensitive) — a second
        // .uasset at the same package path crashes the editor with an FPackageId collision.
        var pkgKey = (mount + "/" + relative.Replace('\\', '/'));
        if (!_writtenPackages.TryAdd(pkgKey, 0))
        {
            entry.Fidelity = Fidelity.Failed;
            entry.Note = "skipped: duplicate package name (case-insensitive collision)";
            return entry;
        }

        CollectGameTypes(asset); // gather game-class refs for --emit-stubs (uniform: Zen + legacy)
        CollectGameplayTags(asset); // gather gameplay tags for DefaultGameplayTags.ini
        CollectCollisionData(asset); // gather collision profiles and channels for DefaultEngine.ini

        if (_opts.DryRun)
        {
            // Plan only: classify + report the intended output path, touch nothing on disk.
            entry.Note = "[dry-run] would reconstruct -> " + entry.OutputPath;
            Log.Information("[dry-run] {Type,-28} {Path}", asset.PrimaryType, asset.File.Path);
            return entry;
        }

        // HLOD/premerged packages ARE the visible level geometry for cooked maps (the originals are hidden
        // behind them), so they are dumped like any mesh. Simplygon stand-ins are likewise directly
        // referenced as placed actors' StaticMesh values, so they convert too (UE4D_SKIP_STANDIN=1 restores
        // the old skip).
        if (asset.File.Path.Contains("/Simplygon/Standins/", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("UE4D_SKIP_STANDIN") == "1")
        {
            entry.Fidelity = Fidelity.Failed;
            entry.Note = "skipped: Simplygon standin package";
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

        // Inner textures (HLOD baked atlases, etc.): packages whose primary export is a mesh/material can
        // still carry UTexture2D exports that materials reference. Export each so the re-import pass can
        // rebuild them; the primary-texture path already wrote its own PNG (skipped by file check inside).
        if (_opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0 && !asset.IsMap)
        {
            try
            {
                var outDir = Path.GetDirectoryName(outputNoExt)!;
                int innerTex = 0;
                foreach (var tex in asset.Exports.OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D>())
                {
                    var r = UE4Decompiler.Reconstructors.TextureReconstructor.ExportOne(tex, outDir, tex.Name);
                    if (r.Fidelity == Fidelity.Full) innerTex++;
                    foreach (var s in r.SidecarFiles)
                        if (!entry.SidecarFiles.Contains(s)) entry.SidecarFiles.Add(s);
                }
                if (innerTex > 0)
                    entry.Note = string.IsNullOrEmpty(entry.Note) ? $"inner textures exported ({innerTex})"
                                                                  : entry.Note + $"; inner textures exported ({innerTex})";
            }
            catch (Exception ex) { Log.Debug(ex, "Inner texture export failed for {Path}", asset.File.Path); }
        }

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
            // The 4.21 placeholder header crashes a UE5 project's asset-registry scan (reads a -1 count). Only write
            // it for UE4 targets; for UE5, skip (a missing map is a harmless absence, not an editor crash).
            if (!TryWritePlacedMap(asset, outputAsset, packageName, entry) && _opts.Game < CUE4Parse.UE4.Versions.EGame.GAME_UE5_0)
                _writer.WriteUAssetHeader(outputAsset, _opts.Game, packageName);
        }
        else if ((asset.PrimaryType is "StaticMesh" || (ContainsStaticMeshExport(asset) && _opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0))
                 && !string.IsNullOrWhiteSpace(_opts.CubePath))
        {
            // UE5 mesh assets are produced by re-importing the .glb through the editor's Interchange
            // pipeline — a hand-written 4.21 mesh package is not loadable and crashes the editor's
            // startup scan. So on UE5 leave the .uasset absent and let the import step create it.
            // (Set UE4D_MESH_STOPGAP=1 to force the legacy direct-write stopgap.)
            if (_opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0
                && Environment.GetEnvironmentVariable("UE4D_MESH_STOPGAP") != "1")
            {
                entry.Note = string.IsNullOrEmpty(entry.Note) ? "mesh: .glb exported for editor re-import"
                                                              : entry.Note + "; mesh: .glb exported for editor re-import";
                // HLOD-style packages: the package ALSO carries material(s) that meshes reference (flattened
                // baked materials). Emit each MI with texture params as its own PBR asset so those references
                // resolve to textured materials (overwrites stale factory-issue files). Single-MI packages only
                // (multi-MI classification would misattribute params).
                try
                {
                    var miExports = asset.Exports.Where(e2 =>
                        e2.ExportType is "MaterialInstanceConstant" or "Material").ToList();
                    if (miExports.Count == 1)
                    {
                        var tpvs = miExports[0].GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("TextureParameterValues");
                        if (tpvs != null && tpvs.Length > 0)
                        {
                            var miName = $"{miExports[0].Name}";
                            var miDir = Path.GetDirectoryName(outputAsset)!;
                            var miPkgDir = packageName.Contains('/') ? packageName.Substring(0, packageName.LastIndexOf('/')) : packageName;
                            var miOut = Path.Combine(miDir, miName + ".uasset");
                            var miEntry = new ManifestEntry
                            {
                                VirtualPath = asset.File.Path,
                                OutputPath = miOut,
                                AssetType = entry.AssetType
                            };
                            if (TryWriteMaterialAsset(asset, miOut, miPkgDir + "/" + miName, miEntry))
                            {
                                entry.Note += "; HLOD MI material -> " + miName;
                                foreach (var s in miEntry.SidecarFiles)
                                    if (!entry.SidecarFiles.Contains(s)) entry.SidecarFiles.Add(s);
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "HLOD MI material emit failed for {Path}", asset.File.Path); }
            }
            // Real geometry when it converts; otherwise a loadable cube (never the cooked-mobile package,
            // which won't render in-editor). Guarantees every mesh is at least a visible placeholder.
            // The ContainsStaticMeshExport case covers HLOD/premerged packages whose *primary* export is a
            // material but which carry the map's merged geometry as an inner UStaticMesh export.
            else if (!TryWriteRealMesh(asset, outputAsset, packageName, entry))
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
        else if (asset.PrimaryType is "Texture2D" or "TextureCube"
                 && _opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0
                 && Environment.GetEnvironmentVariable("UE4D_TEX_STOPGAP") != "1")
        {
            // UE5: the hand-built inline-bulk UTexture2D is sheared/recolored (editor mis-lays out the raw
            // source). The .png sidecar is written by the media export and re-imported through Interchange,
            // so leave the .uasset absent here (import creates the real texture). Set UE4D_TEX_STOPGAP=1 to
            // force the legacy synthetic texture.
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "texture: .png exported for editor re-import"
                                                          : entry.Note + "; texture: .png exported for editor re-import";
        }
        else if (asset.PrimaryType is "Texture2D" or "TextureCube" && TryWriteRealTexture(asset, outputAsset, packageName, entry))
        {
            // editor UTexture2D/UTextureCube with decoded source written; skip the cooked uncooked write.
            // (Route's texture reconstructor only handles UTexture2D, so cubes arrive as Failed —
            // a written editor cube is a full recovery.)
            if (entry.Fidelity == Fidelity.Failed) entry.Fidelity = Fidelity.Full;
        }
        else if (asset.PrimaryType is "Material" or "MaterialInstanceConstant"
                 && TryWriteMaterialAsset(asset, outputAsset, packageName, entry))
        {
            // synth unlit material sampling the asset's first texture written; skip the cooked uncooked write.
        }
        else if (!_opts.SkipBlueprints && IsBlueprint(asset)
                 && TryWriteBlueprint(asset, outputAsset, packageName, entry))
        {
            // BP: reskinned from --bp-template (Zen-safe, browsable/openable) or reconstructed (legacy 4.21, open-safe).
        }
        else if (asset.PrimaryType is "MapBuildDataRegistry"
                 && TryWriteBuiltData(asset, outputAsset, packageName, entry))
        {
            // built lighting registry written
        }
        else if (asset.PrimaryType is "DataTable" or "CompositeDataTable"
                 && TryWriteDataTable(asset, outputAsset, packageName, entry))
        {
            // data table written (or CSV/JSON exported)
        }
        else if (asset.PrimaryType is "StringTable"
                 && TryWriteStringTable(asset, outputAsset, packageName, entry))
        {
            // string table written
        }
        else if (asset.PrimaryType is "CurveFloat" or "CurveVector" or "CurveLinearColor" or "CurveTable"
                 && TryWriteCurve(asset, outputAsset, packageName, entry))
        {
            // curve written
        }
        else if (asset.PrimaryType is "SoundWave" or "SoundCue" or "SoundAttenuation" or "SoundClass" or "SoundSubmix" or "SoundMix" or "SoundMixModifier"
                 && TryWriteAudio(asset, outputAsset, packageName, entry))
        {
            // sound wave / cue / attenuation / class / submix / mix written
        }
        else if (asset.PrimaryType is "InputAction" or "InputMappingContext"
                 && TryWriteInput(asset, outputAsset, packageName, entry))
        {
            // enhanced input written
        }
        else if (asset.PrimaryType is "Skeleton" or "AnimSequence" or "AnimMontage" or "BlendSpace" or "BlendSpace1D"
                 && TryWriteAnimation(asset, outputAsset, packageName, entry))
        {
            // animation / skeleton written
        }
        else if (asset.PrimaryType is "WidgetBlueprint" or "WidgetBlueprintGeneratedClass"
                 && TryWriteWidget(asset, outputAsset, packageName, entry))
        {
            // widget blueprint written
        }
        else if (asset.PrimaryType is "PhysicalMaterial" or "PhysicsAsset"
                 && TryWritePhysics(asset, outputAsset, packageName, entry))
        {
            // physical material or physics asset written
        }
        else if (asset.PrimaryType is "NiagaraSystem" or "NiagaraEmitter" or "ParticleSystem"
                 && TryWriteParticle(asset, outputAsset, packageName, entry))
        {
            // particle system written
        }
        else if (asset.PrimaryType is "AttributeSet" or "GameplayAttributeSet" or "GameplayEffect" or "GameplayAbility"
                 && TryWriteGas(asset, outputAsset, packageName, entry))
        {
            // GAS asset written
        }
        else if (asset.PrimaryType is "FoliageType" or "FoliageType_InstancedStaticMesh"
                 && TryWriteFoliage(asset, outputAsset, packageName, entry))
        {
            // foliage type written
        }
        else if (asset.PrimaryType is "Landscape" or "LandscapeProxy" or "LandscapeStreamingProxy" or "LandscapeLayerInfoObject"
                 && TryWriteLandscape(asset, outputAsset, packageName, entry))
        {
            // landscape written
        }
        else if (asset.PrimaryType.Equals("SubsurfaceProfile", StringComparison.OrdinalIgnoreCase)
                 && TryWriteSubsurface(asset, outputAsset, packageName, entry))
        {
            // subsurface profile written
        }
        else if (asset.PrimaryType is "FileMediaSource" or "StreamMediaSource" or "MediaPlayer" or "MediaTexture"
                 && TryWriteMedia(asset, outputAsset, packageName, entry))
        {
            // media written
        }
        else if (asset.PrimaryType is "IKRigDefinition" or "IKRetargeter" or "ControlRig"
                 && TryWriteIKRig(asset, outputAsset, packageName, entry))
        {
            // IK rig / retargeter written
        }
        else if (!TryWriteUncooked(asset, outputAsset, entry))
            // No editor-loadable form for this type. Do NOT write a stub header: a half-formed .uasset reads as
            // "unrecognizable data" and CRASHES the editor when a map references it, whereas simply omitting the
            // file leaves a harmless broken reference. So skip the write entirely.
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "skipped (no editor-loadable form)"
                                                          : entry.Note + "; skipped (no editor-loadable form)";

        return entry;
    }

    private bool TryWriteDataTable(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            bool uncookedWritten = false;
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
            {
                uncookedWritten = TryWriteUncooked(asset, outputAsset, entry);
            }

            var table = asset.Exports.OfType<CUE4Parse.UE4.Assets.Exports.Engine.UDataTable>().FirstOrDefault();
            if (table != null)
            {
                var rowStruct = table.RowStructName;
                if (!string.IsNullOrWhiteSpace(rowStruct))
                {
                    var structObj = table.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RowStruct")?.ResolvedObject;
                    var structPkg = structObj?.GetPathName();
                    if (!string.IsNullOrEmpty(structPkg) && structPkg.StartsWith("/Script/", StringComparison.Ordinal))
                    {
                        var dot = structPkg.IndexOf('.');
                        if (dot > 0)
                        {
                            var module = structPkg.Substring("/Script/".Length, dot - "/Script/".Length);
                            if (!EngineModuleNames().Contains(module))
                            {
                                var key = $"{module}.{rowStruct}";
                                GameStubs.TryAdd(key, new GameStub(module, rowStruct, "ScriptStruct"));
                                SetStubBaseHint(key, "FTableRowBase");
                            }
                        }
                    }
                }
            }

            entry.Fidelity = Fidelity.Full;
            var note = uncookedWritten ? "DataTable (.uasset + .csv/.json sidecars)" : "DataTable (.csv/.json sidecars for editor re-import)";
            entry.Note = string.IsNullOrEmpty(entry.Note) ? note : entry.Note + $"; {note}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DataTable processing failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteStringTable(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            bool uncookedWritten = false;
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
            {
                uncookedWritten = TryWriteUncooked(asset, outputAsset, entry);
            }

            entry.Fidelity = Fidelity.Full;
            var note = uncookedWritten ? "StringTable (.uasset + .csv/.json sidecars)" : "StringTable (.csv/.json sidecars for editor re-import)";
            entry.Note = string.IsNullOrEmpty(entry.Note) ? note : entry.Note + $"; {note}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "StringTable processing failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteCurve(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            bool uncookedWritten = false;
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
            {
                uncookedWritten = TryWriteUncooked(asset, outputAsset, entry);
            }

            entry.Fidelity = Fidelity.Full;
            var note = uncookedWritten ? $"{asset.PrimaryType} (.uasset + .csv/.json sidecars)" : $"{asset.PrimaryType} (.csv/.json sidecars for editor re-import)";
            entry.Note = string.IsNullOrEmpty(entry.Note) ? note : entry.Note + $"; {note}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Curve processing failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteAudio(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            bool uncookedWritten = false;
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
            {
                uncookedWritten = TryWriteUncooked(asset, outputAsset, entry);
            }

            if (entry.SidecarFiles.Any(s => s.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_soundcue.py", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_attenuation.py", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_soundcue.json", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_attenuation.json", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_soundclass.json", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_soundsubmix.json", StringComparison.OrdinalIgnoreCase)
                                         || s.EndsWith("_soundmix.json", StringComparison.OrdinalIgnoreCase)))
            {
                entry.Fidelity = Fidelity.Full;
                return true;
            }

            return uncookedWritten;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio processing failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteInput(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Note = string.IsNullOrEmpty(entry.Note) ? "enhanced input asset recovered"
                                                          : entry.Note + "; enhanced input asset recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteInput failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteAnimation(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} asset recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} asset recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteAnimation failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteWidget(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            var boundVars = WidgetReconstructor.ExtractBindWidgets(asset);
            var note = $"WidgetBlueprint recovered ({boundVars.Count} bound variable(s))";
            entry.Note = string.IsNullOrEmpty(entry.Note) ? note : entry.Note + $"; {note}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteWidget failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWritePhysics(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWritePhysics failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteParticle(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} particle recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} particle recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteParticle failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteGas(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} GAS asset recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} GAS asset recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteGas failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteFoliage(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "FoliageType recovered"
                                                          : entry.Note + "; FoliageType recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteFoliage failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteLandscape(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} terrain asset recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} terrain asset recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteLandscape failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteSubsurface(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "SubsurfaceProfile optical model recovered"
                                                          : entry.Note + "; SubsurfaceProfile optical model recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteSubsurface failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteMedia(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} Media Framework asset recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} Media Framework asset recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteMedia failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteIKRig(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (asset.Package is Package pkg && UncookedPackageWriter.IsPackageEligible(pkg))
                TryWriteUncooked(asset, outputAsset, entry);

            entry.Fidelity = Fidelity.Full;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"{asset.PrimaryType} skeletal rig asset recovered"
                                                          : entry.Note + $"; {asset.PrimaryType} skeletal rig asset recovered";
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryWriteIKRig failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private bool TryWriteBuiltData(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            if (!Writer.BuiltDataWriter.WriteBuiltDataPackage(asset, outputAsset, packageName, _opts.Game))
                return false;
            entry.Note = string.IsNullOrEmpty(entry.Note) ? "built lighting registry (*_BuiltData.uasset)"
                                                          : entry.Note + "; built lighting registry (*_BuiltData.uasset)";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BuiltData write failed for {Path}", asset.File.Path);
            return false;
        }
    }

    private static bool IsHlodOrSimplygonStandin(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/HLOD/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Simplygon/Standins/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decode the cooked texture and write an editor-loadable UTexture2D (PNG source). Returns false if
    /// the asset has no loadable UTexture2D (caller falls back).</summary>
    private bool TryWriteRealTexture(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        try
        {
            // Drive off the already-loaded exports (uniform for legacy Package AND Zen IoPackage) instead of the
            // legacy-only pkg.ExportMap table — so UE5 textures are found too.
            var tex = asset.Exports.OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture>().FirstOrDefault();
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
    /// <summary>Pick the most base-color-like VectorParameterValue across the material's exports and return it as BGRA.
    /// Returns score &lt; 0 when only non-base colors (emissive/spec/etc.) exist — caller treats that as "no tint".</summary>
    private static (uint bgra, int score) ResolveDominantColor(ParsedAsset asset)
    {
        uint baseColor = 0xFF808080u; int bestCol = -1;
        foreach (var exp in asset.Exports)
        {
            var vpvs = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameterValues");
            if (vpvs == null) continue;
            foreach (var s in vpvs)
            {
                var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                            ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                int sc = 0;
                if (nm.Contains("emiss") || nm.Contains("spec") || nm.Contains("subsurf") || nm.Contains("fresnel")) sc = -2;
                if (nm.Contains("multiply") || nm.Contains("mult")) sc -= 1;   // "Color Multiply" is a grey tint, prefer ColorA/ColorB
                if (nm == "color" || nm.Contains("basecolor") || nm.Contains("base color") || nm.Contains("albedo") ||
                    nm.Contains("tint") || nm.Contains("diffuse") || nm.Contains("color")) sc += 3;
                if (sc <= bestCol) continue;
                var fc = s.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("ParameterValue").ToFColor(true);
                bestCol = sc;
                baseColor = ((uint)fc.A << 24) | ((uint)fc.R << 16) | ((uint)fc.G << 8) | fc.B;
            }
        }
        return (baseColor, bestCol);
    }

    /// <summary>Read the source MI's BasePropertyOverrides: (unlit, blendMode). Enum values arrive as names
    /// ("MSM_Unlit"), FNames, or raw bytes (MSM_Unlit=0, BLEND_Opaque=0/Masked=1/Translucent=2/Additive=3/Modulate=4)
    /// depending on the property path — accept all three.</summary>
    private static (bool unlit, string blend) ResolveBaseOverrides(ParsedAsset asset)
    {
        static bool IsUnlit(object? o)
        {
            if (o == null) return false;
            var s = o.ToString() ?? "";
            if (s.Contains("Unlit", StringComparison.OrdinalIgnoreCase)) return true;
            return byte.TryParse(s, out var b) && b == 0;
        }
        static string ToBlend(object? o)
        {
            var s = o?.ToString() ?? "";
            if (s.Contains("Additive", StringComparison.OrdinalIgnoreCase)) return "BLEND_Additive";
            if (s.Contains("Translucent", StringComparison.OrdinalIgnoreCase)) return "BLEND_Translucent";
            if (s.Contains("Masked", StringComparison.OrdinalIgnoreCase)) return "BLEND_Masked";
            if (s.Contains("Modulate", StringComparison.OrdinalIgnoreCase)) return "BLEND_Modulate";
            if (byte.TryParse(s, out var b)) return b switch
            {
                1 => "BLEND_Masked", 2 => "BLEND_Translucent", 3 => "BLEND_Additive", 4 => "BLEND_Modulate",
                _ => "BLEND_Opaque",
            };
            return "BLEND_Opaque";
        }
        bool unlit = false; string blend = "BLEND_Opaque";
        bool debug = Environment.GetEnvironmentVariable("UE4D_DEBUGMAT") == "1";
        foreach (var exp in asset.Exports)
        {
            var bpo = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("BasePropertyOverrides");
            if (bpo == null) continue;
            if (bpo.GetOrDefault<bool>("bOverride_ShadingModel") && IsUnlit(bpo.GetOrDefault<object>("ShadingModel")))
                unlit = true;
            if (bpo.GetOrDefault<bool>("bOverride_BlendMode"))
            {
                var b = ToBlend(bpo.GetOrDefault<object>("BlendMode"));
                if (b != "BLEND_Opaque") blend = b;
            }
        }
        // MI-level overrides are usually absent (defaults) — the shading path lives on the PARENT master material
        // (UMaterial serializes baked BlendMode/ShadingModel). Walk Parent hops (MI→MI→M) to inherit it.
        if (!unlit || blend == "BLEND_Opaque")
        {
            foreach (var exp in asset.Exports)
            {
                object? cur = null;
                try { cur = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("Parent")?.ResolvedObject; }
                catch { continue; }
                for (int hop = 0; hop < 4 && cur != null; hop++)
                {
                    object? real = null;
                    try { real = ((dynamic)cur).Object?.Value; } catch { break; }
                    if (real == null) break;
                    var rt = real.GetType().FullName ?? "";
                    if (rt.EndsWith(".UMaterial", StringComparison.Ordinal))
                    {
                        // BlendMode inherits: only non-opaque is trusted (a default can't produce it).
                        // ShadingModel does NOT inherit: cooked UMaterial exports don't serialize it, so it
                        // always reads back the MSM_Unlit default — inheriting it unlit nearly everything.
                        try
                        {
                            var b = ToBlend(((dynamic)real).BlendMode?.ToString());
                            if (b != "BLEND_Opaque") blend = b;
                        }
                        catch { }
                        if (debug) Log.Information("  parentblend {E} blend={B}", exp.Name, blend);
                        break;
                    }
                    // Intermediate MI level: honor its overrides, then hop further.
                    try
                    {
                        if (real is CUE4Parse.UE4.Assets.Objects.FStructFallback fb)
                        {
                            var ib = fb.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("BasePropertyOverrides");
                            if (ib != null)
                            {
                                if (ib.GetOrDefault<bool>("bOverride_ShadingModel") && IsUnlit(ib.GetOrDefault<object>("ShadingModel"))) unlit = true;
                                if (ib.GetOrDefault<bool>("bOverride_BlendMode"))
                                {
                                    var b2 = ToBlend(ib.GetOrDefault<object>("BlendMode"));
                                    if (b2 != "BLEND_Opaque") blend = b2;
                                }
                            }
                            var fpar = fb.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("Parent")?.ResolvedObject;
                            if (fpar != null) { cur = fpar; continue; }
                        }
                        cur = ((dynamic)real).Parent?.ResolvedObject;
                    }
                    catch { break; }
                }
                if (unlit || blend != "BLEND_Opaque") break;
            }
        }
        return (unlit, blend);
    }

    /// <summary>Source MI's "Emissive Strength" scalar (MM_Environment masters scale Emissive Mask x
    /// Emissive Color by it; commonly 10 on trim/signage). Default 1 when absent.</summary>
    private static float ResolveEmissiveStrength(ParsedAsset asset)
    {
        foreach (var exp in asset.Exports)
        {
            var spvs = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ScalarParameterValues");
            if (spvs == null) continue;
            foreach (var s in spvs)
            {
                var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                            ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                if (nm == "emissive strength")
                {
                    var v = s.GetOrDefault<float>("ParameterValue");
                    if (float.IsFinite(v) && v >= 0f && v <= 1000f) return v;
                }
            }
        }
        return 1f;
    }

    /// <summary>Source MI's "Emissive Color"-style vector param as BGRA (for unlit tint × texture). Only trusted
    /// when a "Use Emissive"-style static switch is enabled; null otherwise.</summary>
    private static uint? ResolveEmissiveColor(ParsedAsset asset)
    {
        bool useEmissive = false;
        foreach (var exp in asset.Exports)
        {
            var sp = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
            var sw = sp?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("StaticSwitchParameters");
            if (sw == null) continue;
            foreach (var s in sw)
            {
                var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                            ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                if (nm.Contains("emissive") && s.GetOrDefault<bool>("Value")) useEmissive = true;
            }
        }
        if (!useEmissive) return null;
        foreach (var exp in asset.Exports)
        {
            var vpvs = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameterValues");
            if (vpvs == null) continue;
            foreach (var s in vpvs)
            {
                var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                            ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                if (!nm.Contains("emiss")) continue;
                var fc = s.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("ParameterValue").ToFColor(true);
                return ((uint)fc.A << 24) | ((uint)fc.R << 16) | ((uint)fc.G << 8) | fc.B;
            }
        }
        return null;
    }

    private bool TryWriteMaterialAsset(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        var shortName = Path.GetFileNameWithoutExtension(outputAsset);
        try
        {
            // Find a Texture2D ref + its outer package path. Prefer /Game textures (we dump those). The legacy import
            // table only exists for legacy Package; Zen/IoStore (UE5) carries no comparable ImportMap here, so for Zen
            // we fall through to a flat material (still appears + opens; texture binding is a follow-up).
            string? texPkg = null, texName = null, anyPkg = null, anyName = null;
            // Uniform (Zen + legacy): a MaterialInstanceConstant binds textures via TextureParameterValues; each entry's
            // ParameterValue is the Texture2D object. Resolve the first one's /Game package path so the synth material
            // samples the real texture instead of being flat grey.
            // Score candidates so we pick the BASE COLOR/diffuse texture, not the normal map — sampling a normal map
            // as base color tints the whole scene green (tangent normals are green/blue dominant).
            int bestScore = int.MinValue;
            var collected = new List<(string pname, string pkg, string name, string sampler, bool isTex)>();
            foreach (var exp in asset.Exports)
            {
                var tpvs = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("TextureParameterValues");
                if (tpvs == null) continue;
                foreach (var s in tpvs)
                {
                    var ro = s.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParameterValue")?.ResolvedObject;
                    var path = ro?.GetPathName();
                    if (string.IsNullOrEmpty(path)) continue;
                    if (ro?.Class?.Name.Text == "TextureCube") continue;   // cubes aren't a 2D base-color sample
                    // Guard: a texture param occasionally points at a non-texture (mesh, texture array!) —
                    // importing it as a Texture2D would break the material. Only trust 2D textures (or unknown).
                    var rcls = ro?.Class?.Name.Text ?? "";
                    if (!string.IsNullOrEmpty(rcls) && (!rcls.Contains("Texture", StringComparison.OrdinalIgnoreCase) || rcls.Contains("Array", StringComparison.OrdinalIgnoreCase) || rcls == "TextureCube")) continue;
                    var dot = path.LastIndexOf('.'); var slash = path.LastIndexOf('/');
                    var p = dot > slash && dot > 0 ? path.Substring(0, dot) : path;
                    // Inner textures (HLOD baked atlases live inside the cluster package) are re-imported as
                    // standalone dir/<TexName> packages — UNLESS the package exists on disk with its inners
                    // intact (uncooked fallback). Point at where the texture actually (will) land.
                    if (!p.EndsWith("/" + ro!.Name.Text, StringComparison.Ordinal))
                    {
                        var pkgFile = p.StartsWith("/Game/", StringComparison.Ordinal)
                            ? Path.Combine(_opts.ContentRoot, p.Substring("/Game/".Length) + ".uasset") : null;
                        if (pkgFile == null || !File.Exists(pkgFile))
                        {
                            var dslash = p.LastIndexOf('/');
                            if (dslash > 0) p = p.Substring(0, dslash + 1) + ro.Name.Text;
                        }
                    }
                    var info = s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo");
                    var pname = (info?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                    var key = pname + " " + ro!.Name.Text.ToLowerInvariant();
                    int score = 0;
                    if (key.Contains("normal") || key.EndsWith("_n") || key.Contains("_orm") || key.Contains("rough") ||
                        key.Contains("metal") || key.Contains("mask") || key.Contains("packed") || key.Contains("_ao") ||
                        key.Contains("emiss") || key.Contains("_rma") || key.Contains("height")) score = -2;
                    if (key.Contains("diff") || key.Contains("albedo") || key.Contains("basecolor") || key.Contains("base_color") ||
                        key.Contains("color") || key.Contains("_d") || key.Contains("_bc") || key.Contains("diffuse")) score += 3;
                    if (p.StartsWith("/Game/")) score += 1;
                    anyPkg ??= p; anyName ??= ro.Name.Text;
                    // Sampler type must match the texture's own sRGB/normal-map settings or the material fails to compile.
                    string sampler = "SAMPLERTYPE_Color";
                    try
                    {
                        if (ro.TryLoad<CUE4Parse.UE4.Assets.Exports.Texture.UTexture>(out var texObj))
                        {
                            var comp = texObj.CompressionSettings.ToString();
                            sampler = texObj.IsNormalMap ? "SAMPLERTYPE_Normal"
                                    : comp.Equals("TC_Masks", StringComparison.OrdinalIgnoreCase) ? "SAMPLERTYPE_Masks"
                                    : (texObj.SRGB ? "SAMPLERTYPE_Color" : "SAMPLERTYPE_LinearColor");
                        }
                    }
                    catch { }
                    collected.Add((pname, p, ro.Name.Text, sampler, true));
                    if (score > bestScore) { bestScore = score; texPkg = p; texName = ro.Name.Text; }
                }
            }
            // If the best candidate is a non-color map (normal/packed only), don't bind it — flat is better than green.
            if (bestScore < 0) { texPkg = null; texName = null; }
            if (texPkg is null && asset.Package is Package pkg)
            {
                // Legacy import-table scan (when not resolvable via parsed properties).
                foreach (var imp in pkg.ImportMap)
                {
                    if (imp.ClassName.Text != "Texture2D") continue;
                    var oi = imp.OuterIndex?.Index ?? 0;
                    if (oi >= 0) continue;
                    var pkgPath = pkg.ImportMap[-oi - 1].ObjectName.Text;
                    anyPkg ??= pkgPath; anyName ??= imp.ObjectName.Text;
                    if (pkgPath.StartsWith("/Game/")) { texPkg = pkgPath; texName = imp.ObjectName.Text; break; }
                }
            }
            // Only use the any-texture fallback when we didn't deliberately reject a non-color map (bestScore < 0).
            if (bestScore >= 0) { texPkg ??= anyPkg; texName ??= anyName; }
            // Recover UV tiling from scalar params ("U Tiling"/"V Tiling", or a single "Tiling"/"UVScale") so tiled
            // surfaces (floors/walls/trims) repeat correctly instead of stretching one texel across the face.
            float uTiling = 1f, vTiling = 1f;
            foreach (var exp in asset.Exports)
            {
                var spvs = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ScalarParameterValues");
                if (spvs == null) continue;
                foreach (var s in spvs)
                {
                    var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                    var val = s.GetOrDefault<float>("ParameterValue");
                    if (nm.Contains("u tiling") || nm == "utiling") uTiling = val;
                    else if (nm.Contains("v tiling") || nm == "vtiling") vTiling = val;
                    else if (nm == "tiling" || nm == "uvscale" || nm == "uv scale" || nm.Contains("texturescale")) { uTiling = val; vTiling = val; }
                }
            }
            // Classify by the PARAMETER NAME first (the game's master materials use clear names: BaseColor,
            // Normal, ORM/RMA, Emissive Mask, Opacity Mask); fall back to the texture name only when the param
            // name is empty/opaque.
            bool IsNormal(string p, string n) { var ln = n.ToLowerInvariant(); return p.Contains("normal") || (p.Length == 0 && (ln.Contains("_n") || ln.Contains("_nrm") || ln.Contains("_nm") || ln.Contains("normal"))); }
            bool IsEm(string p, string n) { var ln = n.ToLowerInvariant(); return p.Contains("emiss") || p.Contains("glow") || (p.Length == 0 && (ln.Contains("emiss") || ln.Contains("_emis"))); }
            bool IsOp(string p, string n) { var ln = n.ToLowerInvariant(); return p.Contains("opacity") || p.Contains("alpha") || p.Contains("fade") || (p.Length == 0 && (ln.Contains("opacity") || ln.Contains("_op") || ln.Contains("_alpha"))); }
            bool IsOrm(string p, string n)
            {
                if (IsNormal(p, n) || IsEm(p, n) || IsOp(p, n)) return false;
                var ln = n.ToLowerInvariant();
                return p.Contains("orm") || p.Contains("rma") || p.Contains("rough") || p.Contains("metal") ||
                       p.Contains("packed") || p.Contains("mask") || p.Contains("_ao") || p.Contains("occlusion") ||
                       (p.Length == 0 && (ln.Contains("_orm") || ln.Contains("_rma") || ln.Contains("_roughness") || ln.Contains("_metallic")));
            }
            bool IsBc(string p, string n) { var ln = n.ToLowerInvariant(); return p.Contains("basecolor") || p.Contains("base_color") || p.Contains("albedo") || p.Contains("diffuse") || p.Contains("color") || p.Contains("diff") || p == "texture" || p == "base texture" || p == "diffuse texture" || (p.Length == 0 && (ln.Contains("_alb") || ln.Contains("_bc") || ln.Contains("_diff") || ln.Contains("_col"))); }
            Writer.MaterialWriter.MatSlot? Slot(Func<string, string, bool> match)
            {
                foreach (var (pname, p, name, st, _) in collected)
                    if (match(pname, name)) return new Writer.MaterialWriter.MatSlot(p, name, st);
                return null;
            }
            var nSlot = Slot(IsNormal);
            if (Environment.GetEnvironmentVariable("UE4D_DEBUGMAT") == "1")
                foreach (var (pname, p, name, st, _) in collected)
                    Log.Information("  matparam [{P}] -> {N} ({S})", pname, name, st);
            var ormSlot = Slot(IsOrm);
            var emSlot = Slot(IsEm);
            var opSlot = Slot(IsOp);
            var bcSlot = Slot((p, n) => IsBc(p, n) && !IsNormal(p, n) && !IsOrm(p, n) && !IsEm(p, n) && !IsOp(p, n));
            // Source shading path: BasePropertyOverrides (ShadingModel MSM_Unlit, BlendMode Additive/Translucent)
            // must survive — signage/message/holo MIs flattened to lit-opaque render as dark colored planes
            // (e.g. the fracture shop's red additive boards).
            var (srcUnlit, srcBlend) = ResolveBaseOverrides(asset);
            // Bare-"Texture" base slot: masters like M_ShopMessage bind albedo/emissive through a single param
            // literally named "Texture". With no Normal/ORM beside it, that texture is the emissive source
            // (unlit master), not a lit albedo.
            bool bcIsBareTexture = false;
            if (bcSlot != null)
                foreach (var (pname, p, name, st, _) in collected)
                    if (name == bcSlot.Name && p == bcSlot.Pkg &&
                        (pname == "texture" || pname == "base texture" || pname == "diffuse texture"))
                    { bcIsBareTexture = true; break; }
            bool emissiveOnly = emSlot != null || bcIsBareTexture;
            // Unlit master with a texture to emit: unlit emissive writer. The emissive source is the
            // emissive-classified texture; for genuinely unlit MIs without one (Fully Emissive character
            // masters whose glow is Emissive Color x BaseColor), the base texture IS the emissive source.
            // Unlit with NO texture at all falls through to the flat path (constant color on unlit shell).
            bool wantUnlit = srcUnlit || ((bcSlot == null || bcIsBareTexture) && nSlot == null && ormSlot == null && emissiveOnly);
            if (wantUnlit)
            {
                var emTex = emSlot ?? bcSlot ?? Slot((p, n) => !IsNormal(p, n) && !IsOrm(p, n));
                if (emTex != null)
                {
                    var tint = ResolveEmissiveColor(asset) ?? 0xFFFFFFFFu;
                    var estr = ResolveEmissiveStrength(asset);
                    if (Writer.MaterialWriter.WriteEditorMaterialUnlit(outputAsset, shortName, packageName,
                            emTex, tint, srcBlend, uTiling, vTiling, estr))
                    {
                        entry.Note = string.IsNullOrEmpty(entry.Note)
                            ? $"unlit emissive material ({srcBlend} em={emTex?.Name ?? "-"})"
                            : entry.Note + $"; unlit emissive material ({srcBlend})";
                        return true;
                    }
                }
            }
            if (bcSlot != null || nSlot != null || ormSlot != null || emSlot != null)
            {
                var pbrTint = ResolveEmissiveColor(asset) ?? 0xFFFFFFFFu;
                var pbrStrength = ResolveEmissiveStrength(asset);
                if (Writer.MaterialWriter.WriteEditorMaterialPbr(outputAsset, shortName, packageName,
                        bcSlot, nSlot, ormSlot, emSlot, opSlot, uTiling, vTiling,
                        null, srcBlend != "BLEND_Opaque" ? srcBlend : null, pbrTint, pbrStrength))
                {
                    entry.Note = string.IsNullOrEmpty(entry.Note)
                        ? $"PBR material (bc={bcSlot?.Name ?? "-"} n={nSlot?.Name ?? "-"} orm={ormSlot?.Name ?? "-"})"
                        : entry.Note + "; PBR material";
                    return true;
                }
            }
            if (texPkg != null && texName != null)
            {
                // Tint the base texture by the recovered dominant Vector color (team/accent color) so textured surfaces
                // get their real color back (e.g. MI_TeamArena_Crowns: arena pattern × Crowns blue) instead of grey.
                var (tintBgra, tintScore) = ResolveDominantColor(asset);
                bool ok = tintScore >= 0
                    ? Writer.MaterialWriter.WriteEditorMaterialTinted(outputAsset, shortName, packageName, texPkg, texName, uTiling, vTiling, tintBgra)
                    : Writer.MaterialWriter.WriteEditorMaterial(outputAsset, shortName, packageName, texPkg, texName, uTiling, vTiling);
                if (ok)
                {
                    var note = tintScore >= 0 ? $"synth material -> {texName} × tint" : $"synth material -> {texName}";
                    entry.Note = string.IsNullOrEmpty(entry.Note) ? note : entry.Note + "; " + note;
                    return true;
                }
            }
            // No base-color texture: recover the material's base color from a VectorParameterValue (prototype/solid
            // materials like MM_BasicColor store color as a "Color" param). Bake it as the constant BaseColor.
            var (baseColor, bestCol) = ResolveDominantColor(asset);
            if (bestCol < 0) baseColor = 0xFF808080u;   // only emissive/spec colors -> keep neutral grey

            // Recover emissive: only if a "Use Emissive"-style static switch is enabled, then take the
            // "Emissive Color" vector param -> constant EmissiveColor input so emissive surfaces glow.
            uint? emissive = null;
            bool useEmissive = false;
            foreach (var exp in asset.Exports)
            {
                var sp = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("StaticParametersRuntime");
                var sw = sp?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("StaticSwitchParameters");
                if (sw == null) continue;
                foreach (var s in sw)
                {
                    var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                    if (nm.Contains("emissive") && s.GetOrDefault<bool>("Value")) useEmissive = true;
                }
            }
            if (useEmissive)
                foreach (var exp in asset.Exports)
                {
                    var vpvs = exp.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameterValues");
                    if (vpvs == null) continue;
                    foreach (var s in vpvs)
                    {
                        var nm = (s.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback>("ParameterInfo")
                                    ?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("Name").Text ?? "").ToLowerInvariant();
                        if (!nm.Contains("emiss")) continue;
                        var fc = s.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("ParameterValue").ToFColor(true);
                        emissive = ((uint)fc.A << 24) | ((uint)fc.R << 16) | ((uint)fc.G << 8) | fc.B;
                    }
                }

            // Fallback flat material so it shows + opens (vs an unparseable placeholder that crashes referencing maps).
            // Texture-less Use-Emissive instances (prototype signage swatches) are unlit masters: shell unlit.
            bool flatUnlit = srcUnlit || (useEmissive && emissive != null);
            if (!Writer.MaterialWriter.WriteEditorMaterialFlat(outputAsset, shortName, packageName, baseColor, emissive,
                    flatUnlit ? "MSM_Unlit" : null, srcBlend != "BLEND_Opaque" ? srcBlend : null)) return false;
            entry.Note = string.IsNullOrEmpty(entry.Note)
                ? (bestCol >= 0 ? "recovered color material" : "flat material") + (emissive != null ? "+emissive" : "")
                : entry.Note + (bestCol >= 0 ? "; recovered color material" : "; flat material") + (emissive != null ? "+emissive" : "");
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
    public static List<(string pkg, string name, string slot)> ResolveMeshMaterials(
        CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh sm, IPackage pkg)
    {
        var list = new List<(string, string, string)>();
        var slots = sm.StaticMaterials;
        if (slots is null) return list;
        // Legacy 4.21 cooked paks: the import table's package-import ObjectName is the editor "/Game/..." path
        // (ResolvedObject.Outer is the cooked "Pavlov/Content/..." mount, so we must use the import table).
        Dictionary<string, string>? legacyByName = null;
        if (pkg is Package lpkg)
        {
            legacyByName = new(StringComparer.Ordinal);
            foreach (var imp in lpkg.ImportMap)
            {
                if (imp.ClassName.Text is not ("MaterialInstanceConstant" or "Material" or "MaterialInstanceDynamic")) continue;
                var oi = imp.OuterIndex?.Index ?? 0;
                if (oi >= 0) continue;
                legacyByName[imp.ObjectName.Text] = lpkg.ImportMap[-oi - 1].ObjectName.Text;
            }
        }
        foreach (var s in slots)
        {
            var name = s.MaterialInterface?.Name.Text;
            var slot = s.MaterialSlotName.Text;
            string? p = null;
            if (!string.IsNullOrEmpty(name))
            {
                if (legacyByName != null) legacyByName.TryGetValue(name!, out p);
                else p = PackagePathOf(s.MaterialInterface);   // Zen/UE5: use the resolved object's /Game path
            }
            if (p != null)
                list.Add((p, name!, string.IsNullOrEmpty(slot) ? name! : slot));
            else
                list.Add(("/Engine/EngineMaterials/DefaultMaterial", "DefaultMaterial", string.IsNullOrEmpty(slot) ? "Material" : slot));
        }
        return list;
    }

    /// <summary>Package path ("/Game/.../MI_X") of a resolved object via GetPathName (the full object path is
    /// "/Game/.../MI_X.MI_X"; strip the object-name suffix after the last '.'). Returns null if unresolved.</summary>
    private static string? PackagePathOf(CUE4Parse.UE4.Assets.ResolvedObject? obj)
    {
        if (obj is null) return null;
        var full = obj.GetPathName();                       // e.g. /Game/Foo/MI_X.MI_X  (or just /Game/Foo/MI_X)
        if (string.IsNullOrEmpty(full)) return null;
        var lastSlash = full.LastIndexOf('/');
        var lastDot = full.LastIndexOf('.');
        var pkgPath = lastDot > lastSlash ? full[..lastDot] : full;
        return pkgPath.StartsWith("/") ? pkgPath : null;
    }

    /// <summary>When --cube is configured, emit a real-geometry editor StaticMesh (engine-cube clone with the
    /// source's converted FRawMesh appended + re-pointed) at the asset's /Game path, so placed maps render true
    /// geometry. Returns false if no cube is set or the mesh can't be converted (caller falls back).</summary>
    private bool TryWriteRealMesh(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_opts.CubePath)) return false;
        try
        {
            // Uniform over legacy Package + Zen IoPackage: find the StaticMesh in the already-loaded exports.
            var meshes = asset.Exports.OfType<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>().ToList();
            if (meshes.Count == 0) return false;
            if (meshes.Count > 1)
            {
                var specs = new List<BlueprintGraphBuilder.MeshCloneSpec>(meshes.Count);
                var converted = 0;
                foreach (var smx in meshes)
                {
                    byte[]? smxBlob = null;
                    MeshWriter.MeshBounds? smxBounds = null;
                    IReadOnlyList<(string pkg, string name, string slot)>? smxMats = null;
                    CStaticMesh? cmx;
                    try { smx.TryConvert(out cmx); }
                    catch { cmx = null; }
                    if (cmx is not null && cmx.LODs.Count > 0)
                    {
                        smxMats = ResolveMeshMaterials(smx, asset.Package);
                        var smxSlotCount = smxMats.Count > 0 ? smxMats.Count : 1;
                        smxBlob = MeshWriter.BuildFRawMesh(cmx.LODs[0], smxSlotCount);
                        smxBounds = MeshWriter.CalculateBounds(cmx.LODs[0]);
                        converted++;
                    }
                    specs.Add(new BlueprintGraphBuilder.MeshCloneSpec(smx.Name, smxBlob, smxMats, smxBounds));
                }

                if (!BlueprintGraphBuilder.CloneMeshPackage(_opts.CubePath!, outputAsset, packageName, specs))
                    return false;
                entry.Note = string.IsNullOrEmpty(entry.Note)
                    ? $"editor mesh package ({converted}/{meshes.Count} real geometry)"
                    : entry.Note + $"; editor mesh package ({converted}/{meshes.Count} real geometry)";
                return true;
            }

            var sm = meshes[0];
            CStaticMesh? cm;
            try { sm.TryConvert(out cm); }
            catch { cm = null; }
            if (cm is null || cm.LODs.Count == 0) return false;

            // Resolve materials FIRST so the FRawMesh's per-face slot indices can be clamped to the actual slot count.
            // Resolve via the mesh's StaticMaterials ResolvedObjects (works for both package types).
            var mats = ResolveMeshMaterials(sm, asset.Package);
            // A material living in THIS package (HLOD/premerged: mesh + flattened MI share one package) must be
            // dropped: importing a package into itself produces an invalid dependency graph and a corrupt asset.
            mats = mats.Where(m => !string.Equals(m.pkg, packageName, StringComparison.OrdinalIgnoreCase)).ToList();
            var slotCount = mats.Count > 0 ? mats.Count : 1;   // cube fallback keeps 1 slot when no materials
            var blob = MeshWriter.BuildFRawMesh(cm.LODs[0], slotCount);
            var bounds = MeshWriter.CalculateBounds(cm.LODs[0]);
            BlueprintGraphBuilder.CloneMesh(_opts.CubePath!, outputAsset, sm.Name, packageName, blob,
                mats.Count > 0 ? mats : null, bounds);
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

    /// <summary>True when the package carries a UStaticMesh export even though it isn't the primary asset
    /// (HLOD/premerged packages: merged geometry + its flattened material live in one package).</summary>
    private static bool ContainsStaticMeshExport(ParsedAsset asset)
    {
        foreach (var e in asset.Exports)
            if (e is CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh) return true;
        return false;
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
    /// <summary>Recover the cooked BP's SimpleConstructionScript components (engine class, variable name, mesh,
    /// transform) so they can be grafted into the reskinned BP. Reads SCS_Node exports uniformly (Zen + legacy).
    /// Non-engine component classes are substituted with SceneComponent so the BP can't crash on a game class.</summary>
    private static List<BlueprintGraphBuilder.ScsComp> RecoverScsComps(ParsedAsset asset)
    {
        var list = new List<BlueprintGraphBuilder.ScsComp>();
        foreach (var exp in asset.Exports)
        {
            if (exp.ExportType != "SCS_Node") continue;
            var ccRO = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ComponentClass")?.ResolvedObject;
            var compClass = ccRO?.Name.Text ?? "SceneComponent";
            if (!(ccRO?.GetPathName() ?? "").StartsWith("/Script/Engine.", StringComparison.Ordinal)) compClass = "SceneComponent";
            var varName = exp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("InternalVariableName").Text;
            if (string.IsNullOrEmpty(varName) || varName == "None") varName = compClass;
            if (varName == "DefaultSceneRoot") continue;   // the template already provides the root; skip the duplicate
            string? meshPkg = null, meshName = null;
            float[] loc = { 0, 0, 0 }, rot = { 0, 0, 0 }, scl = { 1, 1, 1 };
            var tmpl = exp.GetOrDefault<CUE4Parse.UE4.Assets.Exports.UObject>("ComponentTemplate");
            if (tmpl != null)
            {
                if (compClass.Contains("StaticMesh"))
                {
                    var mi = tmpl.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh")?.ResolvedObject;
                    if (mi != null)
                    {
                        meshName = mi.Name.Text; var mp = mi.GetPathName();
                        var dot = mp.LastIndexOf('.'); var sl = mp.LastIndexOf('/');
                        meshPkg = dot > sl && dot > 0 ? mp.Substring(0, dot) : mp;
                    }
                }
                var l = tmpl.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeLocation");
                loc = new[] { (float)l.X, (float)l.Y, (float)l.Z };
                var r = tmpl.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FRotator>("RelativeRotation");
                rot = new[] { (float)r.Pitch, (float)r.Yaw, (float)r.Roll };
                var s = tmpl.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("RelativeScale3D");
                if (s.X != 0 || s.Y != 0 || s.Z != 0) scl = new[] { (float)s.X, (float)s.Y, (float)s.Z };
            }
            compClass = BlueprintGraphBuilder.NormalizeSynthComponentClass(compClass, meshName != null);
            list.Add(new BlueprintGraphBuilder.ScsComp(compClass, varName, meshPkg, meshName, loc, rot, scl));
        }
        return list;
    }

    /// <summary>A Blueprint asset, detected uniformly (works for Zen/IoStore where ExportMap isn't a legacy table).</summary>
    private static bool IsBlueprint(ParsedAsset asset) =>
        asset.PrimaryType.EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase)
        || asset.PrimaryType.Equals("Blueprint", StringComparison.OrdinalIgnoreCase);

    /// <summary>True only for /Script paths whose module is an engine (or engine-plugin) module — i.e. a class
    /// guaranteed to exist at editor load. Reparenting a cloned BP's BGC SuperIndex to a NON-engine (game/stub)
    /// class is the crash vector: if that class isn't loaded, the super chain is null and CDO serialization
    /// derefs -1 (EXCEPTION_ACCESS_VIOLATION reading 0xffffffffffffffff in CoreUObject).</summary>
    private bool IsEngineModuleClassPath(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !scriptPath.StartsWith("/Script/", StringComparison.Ordinal)) return false;
        var dot = scriptPath.IndexOf('.', "/Script/".Length);
        if (dot <= "/Script/".Length) return false;
        return EngineModuleNames().Contains(scriptPath["/Script/".Length..dot]);
    }

    private bool IsNativeSourceModuleClassPath(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !scriptPath.StartsWith("/Script/", StringComparison.Ordinal)) return false;
        return NativeSourceClasses().ContainsKey(scriptPath);
    }

    private string? GetNativeSourceEngineBaseForClassPath(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !scriptPath.StartsWith("/Script/", StringComparison.Ordinal)) return null;
        return NativeSourceClasses().TryGetValue(scriptPath, out var info) ? info.EngineBase : null;
    }

    private static string? GetBlueprintParentClassPath(ParsedAsset asset)
    {
        foreach (var e in asset.Exports)
        {
            if (e is CUE4Parse.UE4.Objects.UObject.UStruct s
                && AssetParser.ClassName(e).EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            {
                var path = s.SuperStruct.ResolvedObject?.GetPathName();
                if (!string.IsNullOrWhiteSpace(path) && path != "None"
                    && (path.StartsWith("/Script/", StringComparison.Ordinal) || path.StartsWith("/Game/", StringComparison.Ordinal)))
                    return path;
            }
            var parent = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParentClass")?.ResolvedObject?.GetPathName();
            if (!string.IsNullOrWhiteSpace(parent) && parent != "None"
                && (parent.StartsWith("/Script/", StringComparison.Ordinal) || parent.StartsWith("/Game/", StringComparison.Ordinal)))
                return parent;
        }
        return null;
    }

    private static string? GetBlueprintParentEngineBase(ParsedAsset asset)
    {
        foreach (var e in asset.Exports)
        {
            if (e is CUE4Parse.UE4.Objects.UObject.UStruct s
                && AssetParser.ClassName(e).EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            {
                var engineBase = InferEngineBase(s.SuperStruct.ResolvedObject);
                if (!string.IsNullOrWhiteSpace(engineBase)) return engineBase;
            }

            var parent = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ParentClass")?.ResolvedObject;
            var parentBase = InferEngineBase(parent);
            if (!string.IsNullOrWhiteSpace(parentBase)) return parentBase;
        }
        return null;
    }

    private string? GetStubBaseHintForClassPath(string? classPath)
    {
        if (string.IsNullOrWhiteSpace(classPath) || !classPath.StartsWith("/Script/", StringComparison.Ordinal)) return null;
        if (EngineBaseByPath.TryGetValue(classPath, out var engineBase)) return engineBase;
        var dot = classPath.LastIndexOf('.');
        if (dot <= "/Script/".Length || dot + 1 >= classPath.Length) return null;
        var module = classPath["/Script/".Length..dot];
        var cls = classPath[(dot + 1)..];
        return StubBaseHints.TryGetValue($"{module}.{cls}", out var baseClass) ? baseClass : GuessEngineBaseFromClassName(cls);
    }

    private static string? GuessEngineBaseFromClassName(string cls)
    {
        if (cls.EndsWith("Character", StringComparison.Ordinal)) return "ACharacter";
        if (cls.EndsWith("PlayerController", StringComparison.Ordinal)) return "APlayerController";
        if (cls.EndsWith("AIController", StringComparison.Ordinal)) return "AAIController";
        if (cls.EndsWith("Controller", StringComparison.Ordinal)) return "AController";
        if (cls.EndsWith("Pawn", StringComparison.Ordinal)) return "APawn";
        if (cls.EndsWith("GameModeBase", StringComparison.Ordinal) || cls.EndsWith("GameMode", StringComparison.Ordinal)) return "AGameModeBase";
        if (cls.EndsWith("GameStateBase", StringComparison.Ordinal) || cls.EndsWith("GameState", StringComparison.Ordinal)) return "AGameStateBase";
        if (cls.EndsWith("PlayerState", StringComparison.Ordinal)) return "APlayerState";
        if (cls.EndsWith("CameraManager", StringComparison.Ordinal) || cls.EndsWith("CameraManagerPawn", StringComparison.Ordinal)) return "APawn";
        if (cls.EndsWith("HUD", StringComparison.Ordinal)) return "AHUD";
        if (cls.EndsWith("Volume", StringComparison.Ordinal)) return "AVolume";
        if (cls.EndsWith("UserWidget", StringComparison.Ordinal) || cls.EndsWith("Widget", StringComparison.Ordinal)) return "UUserWidget";
        if (cls.EndsWith("AnimInstance", StringComparison.Ordinal)) return "UAnimInstance";
        if (cls.EndsWith("SceneComponent", StringComparison.Ordinal)) return "USceneComponent";
        if (cls.EndsWith("Component", StringComparison.Ordinal)) return "UActorComponent";
        if (cls.EndsWith("DataAsset", StringComparison.Ordinal)) return "UDataAsset";
        if (cls.EndsWith("Actor", StringComparison.Ordinal) || cls.Contains("Actor", StringComparison.Ordinal)) return "AActor";
        return null;
    }

    private static bool IsActorTemplateCompatible(string? engineBase) => engineBase is null or
        "AActor" or "APawn" or "ACharacter" or "AController" or "APlayerController" or "AAIController" or
        "AGameModeBase" or "AGameStateBase" or "APlayerState" or "APlayerCameraManager" or "ALevelScriptActor" or
        "AHUD" or "AVolume";

    private static bool IsReparentTemplateSupported(string? engineBase) => engineBase is
        "AActor" or "APawn" or "ACharacter" or "AController" or "APlayerController" or "AAIController" or
        "AGameModeBase" or "AGameStateBase" or "APlayerState" or "APlayerCameraManager" or "ALevelScriptActor" or
        "AHUD" or "AVolume" or "UUserWidget" or "UAnimInstance" or "USceneComponent" or "UActorComponent";

    private static string? ResolveBlueprintTemplate(string configuredTemplate, string? engineBase)
    {
        var dir = Directory.Exists(configuredTemplate)
            ? configuredTemplate
            : Path.GetDirectoryName(configuredTemplate);
        string? Pick(params string[] names)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        var familyTemplate = engineBase switch
        {
            "ACharacter" => Pick("character.uasset", "bp_character.uasset"),
            "APawn" => Pick("pawn.uasset", "bp_pawn.uasset"),
            "AGameModeBase" => Pick("gamemodebase.uasset", "bp_gamemodebase.uasset"),
            "UUserWidget" => Pick("UserWidget.uasset", "userwidget.uasset", "bp_widget.uasset"),
            "UAnimInstance" => Pick("bp_animbp.uasset", "animbp.uasset"),
            "USceneComponent" => Pick("scenecomponent.uasset", "bp_scenecomponent.uasset"),
            "UActorComponent" => Pick("actorcomponent.uasset", "bp_actorcomponent.uasset"),
            "AActor" or "AController" or "APlayerController" or "AAIController" or "AGameStateBase" or
                "APlayerState" or "APlayerCameraManager" or "ALevelScriptActor" or "AHUD" or "AVolume"
                => Pick("actor.uasset", "ahctor.uasset", "bp_actor.uasset"),
            _ => Pick("actor.uasset", "ahctor.uasset", "bp_actor.uasset")
        };
        if (!string.IsNullOrWhiteSpace(familyTemplate)) return familyTemplate;
        return File.Exists(configuredTemplate) && IsActorTemplateCompatible(engineBase) ? configuredTemplate : null;
    }

    /// <summary>Strict structural validation of freshly-written BP bytes: every K2 node export's tag
    /// stream and pin section must walk cleanly with our ground-truth reader (the same reader that parses
    /// real editor output). CUE4Parse's lazy loader swallows these errors and returns partial objects, so
    /// it cannot serve as the oracle. Returns failure descriptions (empty = valid).
    /// ThreadStatic walker state is saved/restored: the dump pipeline is parallel.</summary>
    private static List<string> ValidateWrittenBlueprint(byte[] data)
    {
        var failures = new List<string>();
        try
        {
            if (data.Length < 32) return new List<string> { "SUMMARY:too-small" };
            int legacy = BitConverter.ToInt32(data, 4);
            int off = 8;
            if (legacy != -4) off += 4;
            if (off + 12 > data.Length) return new List<string> { "SUMMARY:truncated-versions" };
            off += 4; // FileVersionUE4
            int fv5 = 0;
            if (legacy <= -8) { fv5 = BitConverter.ToInt32(data, off); off += 4; }
            bool ue5 = legacy <= -8 && fv5 >= 1000;
            var game = ue5 ? CUE4Parse.UE4.Versions.EGame.GAME_UE5_5 : CUE4Parse.UE4.Versions.EGame.GAME_UE4_21;
            CUE4Parse.UE4.Assets.Package pkg;
            try
            {
                var ar = new CUE4Parse.UE4.Readers.FByteArchive("validate",
                    data, new CUE4Parse.UE4.Versions.VersionContainer(game));
                pkg = new CUE4Parse.UE4.Assets.Package(ar,
                    (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                    (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
            }
            catch (Exception ex) { return new List<string> { "SUMMARY:" + ex.GetType().Name + ":" + ex.Message.Split('\n')[0] }; }
            // Save/restore thread-static walker state (parallel pipeline).
            var saveUe5 = NodePayloadWalker.IsUe5; var saveNc = NodePayloadWalker.NameCount;
            var saveSp = NodePayloadWalker.StructPropertyIdx; var saveBp = NodePayloadWalker.BoolPropertyIdx;
            var saveByp = NodePayloadWalker.BytePropertyIdx; var saveEp = NodePayloadWalker.EnumPropertyIdx;
            var saveAp = NodePayloadWalker.ArrayPropertyIdx; var saveSep = NodePayloadWalker.SetPropertyIdx;
            var saveMp = NodePayloadWalker.MapPropertyIdx;
            try
            {
                NodePayloadWalker.IsUe5 = ue5;
                NodePayloadWalker.NameCount = pkg.NameMap.Length;
                int NIdx(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
                NodePayloadWalker.StructPropertyIdx = NIdx("StructProperty");
                NodePayloadWalker.BoolPropertyIdx = NIdx("BoolProperty");
                NodePayloadWalker.BytePropertyIdx = NIdx("ByteProperty");
                NodePayloadWalker.EnumPropertyIdx = NIdx("EnumProperty");
                NodePayloadWalker.ArrayPropertyIdx = NIdx("ArrayProperty");
                NodePayloadWalker.SetPropertyIdx = NIdx("SetProperty");
                NodePayloadWalker.MapPropertyIdx = NIdx("MapProperty");
                int noneIdx = NIdx("None");
                for (var i = 0; i < pkg.ExportMap.Length; i++)
                {
                    var ex = pkg.ExportMap[i];
                    if (!ex.ClassName.StartsWith("K2Node_", StringComparison.Ordinal)) continue;
                    try
                    {
                        var start = (int)ex.SerialOffset; var size = (int)ex.SerialSize;
                        if (start < 0 || size <= 0 || (long)start + size > data.Length) { failures.Add($"export[{i}]:bounds"); continue; }
                        var payload = new byte[size];
                        Array.Copy(data, start, payload, 0, size);
                        int ps = NodePayloadWalker.FindPayloadStart(payload, pkg.NameMap.Length);
                        int pe = NodePayloadWalker.SkipTaggedProperties(payload, ps, noneIdx);
                        var pins = NodePayloadWalker.WalkPins(payload, pe);
                        if (pins.Count < 0 || pins.Count > 256) failures.Add($"export[{i}]:pincount={pins.Count}");
                    }
                    catch (Exception ex2) { failures.Add($"export[{i}]:{ex2.GetType().Name}:{ex2.Message.Split('\n')[0]}"); }
                }
            }
            finally
            {
                NodePayloadWalker.IsUe5 = saveUe5; NodePayloadWalker.NameCount = saveNc;
                NodePayloadWalker.StructPropertyIdx = saveSp; NodePayloadWalker.BoolPropertyIdx = saveBp;
                NodePayloadWalker.BytePropertyIdx = saveByp; NodePayloadWalker.EnumPropertyIdx = saveEp;
                NodePayloadWalker.ArrayPropertyIdx = saveAp; NodePayloadWalker.SetPropertyIdx = saveSep;
                NodePayloadWalker.MapPropertyIdx = saveMp;
            }
        }
        catch (Exception ex) { failures.Add("VALIDATOR:" + ex.GetType().Name + ":" + ex.Message.Split('\n')[0]); }
        return failures;
    }

    private bool TryWriteBlueprint(ParsedAsset asset, string outputAsset, string packageName, ManifestEntry entry)
    {
        // Preferred: reskin a known-good 4.21 BP template (valid UBlueprint+BGC+SCS) renamed to this BP. Works for
        // UE5/Zen (we only need the target name) and is crash-safe, so cooked BPs show + open in the content browser.
        if (!string.IsNullOrWhiteSpace(_opts.BpTemplate))
        {
            // Clone at 4.21 (the proven SynthPackageWriter format; 5.4 auto-upgrades it). The template MUST be a 4.21
            // BP. Inject the cooked BP's recovered SCS components (mesh/transform) so they show in the Components panel.
            var scs = RecoverScsComps(asset);
            var recoveredCalls = (_opts.BpRecoverCalls || _opts.EmitStubMethods)
                ? BlueprintGraphBuilder.ExtractCallsFromExports(asset.Exports)
                : new List<(string scriptPkg, string cls, string func)>();
            if (_opts.EmitStubMethods) CollectGameStubMethods(recoveredCalls);
            var graphCalls = _opts.BpRecoverCalls
                ? recoveredCalls
                : new List<(string scriptPkg, string cls, string func)>();
            // Full graph recovery: decompile every function into exec+data wired K2 graphs (events,
            // calls with pins, variable get/set, branches). Supersedes the flat exec-chain above.
            List<FunctionGraph>? graphs = null;
            if (_opts.BpRecoverCalls)
            {
                try { graphs = KismetGraphDecompiler.DecompileFunctions(asset.Exports.OfType<CUE4Parse.UE4.Objects.UObject.UFunction>(), asset.Exports); }
                catch (Exception ex) { Log.Warning(ex, "Graph decompile failed for {Path}; falling back to flat call chain", asset.File.Path); graphs = null; }
                if (graphs != null)
                {
                    KismetGraphDecompiler.RetargetUbergraphDispatch(graphs, Path.GetFileNameWithoutExtension(outputAsset));
                    CollectGraphStubSignatures(graphs, packageName, Path.GetFileNameWithoutExtension(outputAsset));
                }
            }
            // Grafted member variables (referenced names the template lacks): guids minted here feed both
            // the NewVariables injection and the emitted node member refs.
            List<BlueprintGraphBuilder.GraftedVar>? grafted = null;
            try
            {
                if (graphs != null && graphs.Count > 0)
                    grafted = BlueprintGraphBuilder.CollectGraftedVars(graphs,
                        KismetGraphDecompiler.BgcMemberTypes(asset.Exports));
            }
            catch (Exception ex) { Log.Debug(ex, "Grafted var collection failed for {Path}", asset.File.Path); }
            var parentClass = GetBlueprintParentClassPath(asset);
            // Pick the closest template family from the recovered parent, but do not byte-patch the
            // BP's ParentClass/BGC SuperIndex by default. The cloned CDO/SCS payload is still shaped
            // like the template BP; pointing it at another native class makes worlds that instantiate
            // the BP deserialize the actor with the wrong native layout.
            var parentEngineBase = GetBlueprintParentEngineBase(asset)
                ?? GetNativeSourceEngineBaseForClassPath(parentClass)
                ?? GetStubBaseHintForClassPath(parentClass);
            var allowNativeReparent = Environment.GetEnvironmentVariable("UE4D_ALLOW_NATIVE_BP_REPARENT") == "1";
            if (!string.IsNullOrWhiteSpace(parentClass) && IsNativeSourceModuleClassPath(parentClass))
                BlueprintReparentRequests.Add(new BlueprintReparentRequest(packageName, parentClass, outputAsset, parentEngineBase));
            string? reparentClass = allowNativeReparent && IsNativeSourceModuleClassPath(parentClass) && IsReparentTemplateSupported(parentEngineBase)
                ? parentClass
                : null;
            var templatePath = ResolveBlueprintTemplate(_opts.BpTemplate!, parentEngineBase);
            if (string.IsNullOrWhiteSpace(templatePath))
            {
                Log.Warning("Blueprint {Path}: no compatible BP template for parent base {Base} in/near {Template}; skipping BP clone until a matching template is supplied",
                    asset.File.Path, parentEngineBase ?? "unknown", _opts.BpTemplate);
                return false;
            }
            if (!BlueprintGraphBuilder.CloneBlueprintTemplate(templatePath, outputAsset,
                    Path.GetFileNameWithoutExtension(outputAsset), packageName, _opts.Game, scs, graphCalls, reparentClass, graphs,
                    null, false, false, packageName, Path.GetFileNameWithoutExtension(outputAsset) + "_C", grafted))
                return false;
            // Crash gate: graph recovery occasionally emits a structurally corrupt node payload (e.g.
            // delegate-temp user pins whose bodies desync every reader) that takes the editor down at load
            // (Invalid boolean / Linker 135). Strict-walk what we just wrote; on ANY failure discard the
            // recovery and keep the plain clone (safe, opens, compiles).
            if (graphs != null && graphs.Count > 0)
            {
                try
                {
                    var recFailures = ValidateWrittenBlueprint(File.ReadAllBytes(outputAsset));
                    if (Environment.GetEnvironmentVariable("UE4D_BPVALIDATE_LOG") == "1" && recFailures.Count > 0)
                        Log.Information("BPVALIDATE {P}: {N} finding(s): {F}", packageName, recFailures.Count, string.Join(" | ", recFailures.Take(4)));
                    if (recFailures.Count > 0)
                    {
                        if (BlueprintGraphBuilder.CloneBlueprintTemplate(templatePath, outputAsset,
                                Path.GetFileNameWithoutExtension(outputAsset), packageName, _opts.Game, scs,
                                new List<(string scriptPkg, string cls, string func)>(), reparentClass, null,
                                null, false, false, packageName, Path.GetFileNameWithoutExtension(outputAsset) + "_C", null))
                        {
                            var plainFailures = ValidateWrittenBlueprint(File.ReadAllBytes(outputAsset));
                            if (plainFailures.Count == 0)
                            {
                                Log.Warning("Blueprint {Path}: recovered graph failed validation ({R}); kept plain clone",
                                    asset.File.Path, string.Join(" | ", recFailures.Take(3)));
                                entry.Note = (string.IsNullOrEmpty(entry.Note) ? "" : entry.Note + "; ")
                                    + $"blueprint (plain clone: recovered graph failed validation, {graphs.Sum(g => g.Nodes.Count)} node(s) dropped)";
                                return true;
                            }
                            Log.Error("Blueprint {Path}: BOTH recovery and plain clone failed validation (rec=[{R}] plain=[{P}])",
                                asset.File.Path, string.Join(" | ", recFailures.Take(3)), string.Join(" | ", plainFailures.Take(3)));
                            entry.Note = (string.IsNullOrEmpty(entry.Note) ? "" : entry.Note + "; ")
                                + "blueprint (plain clone kept; validation failed on both variants)";
                            return true;
                        }
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "BP validation gate failed for {Path}; kept recovery output", asset.File.Path); }
            }
            var parentNote = string.IsNullOrWhiteSpace(parentClass) ? "" : $", parent={parentClass}";
            var reparentNote = string.IsNullOrWhiteSpace(reparentClass) ? "" : ", reparented";
            var callsNote = _opts.BpRecoverCalls ? $", {graphCalls.Count} call node(s)" : "";
            var graphNote = graphs != null && graphs.Count > 0
                ? $", graph={graphs.Sum(g => g.Nodes.Count)} node(s)/{graphs.Count} fn(s)" : "";
            var methodsNote = _opts.EmitStubMethods ? $", {recoveredCalls.Count} recovered call ref(s)" : "";
            var baseNote = string.IsNullOrWhiteSpace(parentEngineBase) ? "" : $", base={parentEngineBase}";
            var templateNote = $", template={Path.GetFileName(templatePath)}";
            entry.Note = string.IsNullOrEmpty(entry.Note) ? $"blueprint (template + {scs.Count} SCS comp(s){callsNote}{graphNote}{methodsNote}{parentNote}{reparentNote}{baseNote}{templateNote})"
                                                           : entry.Note + $"; blueprint (template + {scs.Count} SCS comp(s){callsNote}{graphNote}{methodsNote}{parentNote}{reparentNote}{baseNote}{templateNote})";
            return true;
        }
        // Legacy byte-based reconstruction: 4.21 (legacy Package) only, gated to crash-safe BPs.
        if (asset.Package is not Package) return false;
        if (!IsBlueprintReconstructable(asset) || !(_opts.DangerBpGraph || IsOpenSafeBlueprint(asset))) return false;
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
        var targetShort = Path.GetFileNameWithoutExtension(outputAsset);
        // UE5: cooked-actor placement onto the editor map template (version-following tags/shell/tails).
        // Falls back to an empty reskin only when placement itself fails (it never bails: empty place
        // lists still emit the template map so no stale .umap is left behind).
        if (_opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0)
        {
            if (string.IsNullOrWhiteSpace(_opts.MapTemplate))
            {
                Log.Warning("Map {Path}: --template not set; skipping (UE5 writes no placeholder header)", asset.File.Path);
                return false;
            }
            try
            {
                // Record the engine base each placed game class needs (actor->AActor, component->USceneComponent) so the
                // stub generator emits a spawnable class instead of a UObject the editor crashes trying to place.
                void OnGameClassUe5(string scriptPkg, string cls, string baseClass)
                {
                    if (!scriptPkg.StartsWith("/Script/", StringComparison.Ordinal)) return;
                    var module = scriptPkg["/Script/".Length..];
                    if (EngineModules.Contains(module)) return;       // real engine class, no stub needed
                    StubBaseHints[$"{module}.{cls}"] = baseClass;
                }
                BlueprintGraphBuilder.PlaceActorsCore(asset.Package,
                    File.ReadAllBytes(_opts.MapTemplate!), outputAsset, targetShort, packageName,
                    _opts.CubePath, _opts.ContentRoot, OnGameClassUe5, true, _provider);
                entry.Note = string.IsNullOrEmpty(entry.Note) ? "placed-actor editor map (UE5)"
                                                              : entry.Note + "; placed-actor editor map (UE5)";
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UE5 placed-map write failed for {Path}", asset.File.Path);
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(_opts.MapTemplate) || string.IsNullOrWhiteSpace(_opts.CubePath))
        {
            Log.Warning("Map {Path}: --template + --cube not set; writing empty placeholder map (no actors)", asset.File.Path);
            return false;
        }
        try
        {
            // Record the engine base each placed game class needs (actor->AActor, component->USceneComponent) so the
            // stub generator emits a spawnable class instead of a UObject the editor crashes trying to place.
            void OnGameClass(string scriptPkg, string cls, string baseClass)
            {
                if (!scriptPkg.StartsWith("/Script/", StringComparison.Ordinal)) return;
                var module = scriptPkg["/Script/".Length..];
                if (EngineModules.Contains(module)) return;       // real engine class, no stub needed
                StubBaseHints[$"{module}.{cls}"] = baseClass;
            }
            // Drive placement off the already-LOADED package (uniform for legacy Package AND Zen IoPackage).
            BlueprintGraphBuilder.PlaceActorsCore(asset.Package,
                File.ReadAllBytes(_opts.MapTemplate!), outputAsset, targetShort, packageName,
                _opts.CubePath, _opts.ContentRoot, OnGameClass, _opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0, _provider);

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
        // Fast path: skip the expensive texture-PNG / mesh-glb exports. The real editor-loadable
        // .uasset is still written by TryWriteUncooked; only the media side-export is elided.
        if (_opts.NoMediaExport && asset.PrimaryType is
            "Texture2D" or "TextureCube" or "StaticMesh" or "SkeletalMesh")
            return new ReconstructionResult
            {
                Fidelity = Fidelity.Partial,
                Note = $"{asset.PrimaryType}: media export skipped (--no-media-export)",
                Model = new { asset.PrimaryType, asset.File.Path, MediaExportSkipped = true }
            };

        if (asset.IsMap) return _level.Reconstruct(asset, outputNoExt);

        // HLOD/premerged packages carry the visible merged geometry as an inner UStaticMesh export while
        // their *primary* export is a flattened material. Always yield the .glb for any mesh-bearing package
        // so the editor-import step can rebuild it.
        if (_opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0 && ContainsStaticMeshExport(asset)
            && asset.PrimaryType is not ("StaticMesh" or "SkeletalMesh"))
            return _mesh.Reconstruct(asset, outputNoExt);

        // Widget blueprints route to the dedicated widget reconstructor
        if (asset.PrimaryType.Equals("WidgetBlueprint", StringComparison.OrdinalIgnoreCase) ||
            asset.PrimaryType.Equals("WidgetBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            return _widget.Reconstruct(asset, outputNoExt);

        // Any *BlueprintGeneratedClass (Anim/plain) and UBlueprint route to the BP reconstructor.
        if (asset.PrimaryType.Equals("Blueprint", StringComparison.OrdinalIgnoreCase) ||
            asset.PrimaryType.EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            return _blueprint.Reconstruct(asset, outputNoExt);

        return asset.PrimaryType switch
        {
            "Texture2D" or "TextureCube" => _texture.Reconstruct(asset, outputNoExt),
            "StaticMesh" or "SkeletalMesh" => _mesh.Reconstruct(asset, outputNoExt),
            "Material" or "MaterialInstanceConstant" => _material.Reconstruct(asset, outputNoExt),
            "World" or "Level" => _level.Reconstruct(asset, outputNoExt),
            "SoundWave" or "SoundCue" or "SoundAttenuation" or "SoundClass" or "SoundSubmix" or "SoundMix" or "SoundMixModifier" => _audio.Reconstruct(asset, outputNoExt, _opts.NoMediaExport),
            "DataTable" or "CompositeDataTable" => _dataTable.Reconstruct(asset, outputNoExt),
            "StringTable" => _stringTable.Reconstruct(asset, outputNoExt),
            "CurveFloat" or "CurveVector" or "CurveLinearColor" or "CurveTable" => _curve.Reconstruct(asset, outputNoExt),
            "InputAction" or "InputMappingContext" => _input.Reconstruct(asset, outputNoExt),
            "Skeleton" or "AnimSequence" or "AnimMontage" or "BlendSpace" or "BlendSpace1D" => _anim.Reconstruct(asset, outputNoExt),
            "PhysicalMaterial" or "PhysicsAsset" => _physics.Reconstruct(asset, outputNoExt),
            "NiagaraSystem" or "NiagaraEmitter" or "ParticleSystem" => _particle.Reconstruct(asset, outputNoExt),
            "AttributeSet" or "GameplayAttributeSet" or "GameplayEffect" or "GameplayAbility" => _gas.Reconstruct(asset, outputNoExt),
            "FoliageType" or "FoliageType_InstancedStaticMesh" => _foliage.Reconstruct(asset, outputNoExt),
            "Landscape" or "LandscapeProxy" or "LandscapeStreamingProxy" or "LandscapeLayerInfoObject" => _landscape.Reconstruct(asset, outputNoExt),
            "SubsurfaceProfile" => _subsurface.Reconstruct(asset, outputNoExt),
            "FileMediaSource" or "StreamMediaSource" or "MediaPlayer" or "MediaTexture" => _media.Reconstruct(asset, outputNoExt),
            "IKRigDefinition" or "IKRetargeter" or "ControlRig" => _ikrig.Reconstruct(asset, outputNoExt),
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

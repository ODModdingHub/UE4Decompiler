using System.Text;
using Newtonsoft.Json.Linq;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Output.Stubs;

/// <summary>Graph-recovered stub signature: UFUNCTION params + optional return, with pin names sanitized
/// to C++ identifiers and categories as emitted — so emitted call pins bind to the stub.
/// StaticCall (call-site self unwired) emits a static inline method; otherwise an
/// ImplementableEvent (no C++ body needed, links clean). Extra outputs become UPARAM(ref) out-pins.</summary>
public sealed record StubMethodSig(List<(string Name, string Cat, byte Cont)> Params, (string Name, string Cat)? Ret, bool StaticCall,
    List<(string Name, string Cat)>? Outs = null);

/// <summary>
/// Generates a compilable C++ stub module per game <c>/Script/&lt;Module&gt;</c> package referenced by the
/// extracted assets, so the editor can resolve game-native classes (e.g. <c>/Script/Pavlov.PavlovLevelScriptActor</c>)
/// and the assets parented to them will load. Stubs are minimal: a UCLASS with the inferred engine base
/// (tagged properties bind by name, so empty stubs still let assets load), USTRUCT/UENUM shells.
///
/// Class A/U prefix and base are inferred from name/suffix heuristics, refined by the SDK dump's
/// "// CLASS:" list when provided (which records the real prefixed names). Bases are flattened to ENGINE
/// classes so modules only depend on the engine (no inter-game-module / circular dependencies).
///
/// The user compiles the generated module against their installed 4.21 (a game-module build, not an
/// engine build). Wrong base guesses for exotic classes can be corrected in the emitted header.
/// </summary>
public sealed class StubModuleGenerator
{
    private readonly Dictionary<string, char> _sdkPrefix; // bareName -> 'A'/'U'/'F'/'E'
    private IReadOnlyDictionary<string, string> _baseHints = new Dictionary<string, string>(); // "Module.Name" -> engine base
    private Dictionary<string, SortedSet<string>> _methodHints = new(StringComparer.Ordinal); // "Module.Name" -> methods
    private IReadOnlyDictionary<string, StubMethodSig> _methodSigs = new Dictionary<string, StubMethodSig>();

    public StubModuleGenerator(string? sdkDumpDir)
    {
        _sdkPrefix = sdkDumpDir is not null ? LoadSdkPrefixes(sdkDumpDir) : new();
    }

    /// <summary>Delete previous runs' generated shells (marked dirs) so they neither linger nor get
    /// mistaken for user-copied real modules. Call BEFORE discovering real modules.</summary>
    public static void CleanMarkedStubs(string outputRoot)
    {
        var sourceDir = Path.Combine(outputRoot, "Source");
        if (!Directory.Exists(sourceDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            if (File.Exists(Path.Combine(dir, ".ue4d_stub")))
                try { Directory.Delete(dir, recursive: true); } catch (Exception ex) { Log.Warning(ex, "Stale stub cleanup failed for {Dir}", dir); }
    }

    public void Generate(string outputRoot, IReadOnlyCollection<GameStub> stubs,
        IReadOnlyDictionary<string, string>? baseHints = null,
        IReadOnlyCollection<string>? methodHints = null,
        IReadOnlyCollection<string>? existingModules = null,
        IReadOnlyDictionary<string, StubMethodSig>? methodSigs = null)
    {
        _baseHints = baseHints ?? new Dictionary<string, string>();
        _methodHints = ParseMethodHints(methodHints);
        _methodSigs = methodSigs ?? new Dictionary<string, StubMethodSig>();
        var existing = existingModules?.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();
        if (stubs.Count == 0 && existing.Count == 0)
        {
            Log.Information("--emit-stubs: no game-module types referenced; nothing to generate.");
            return;
        }

        var uproject = Directory.EnumerateFiles(outputRoot, "*.uproject").FirstOrDefault();
        var projectName = uproject is not null ? Path.GetFileNameWithoutExtension(uproject) : "Game";

        var byModule = stubs.GroupBy(s => s.Module).ToDictionary(g => g.Key, g => g.ToList());
        var moduleNames = existing.Concat(byModule.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m).ToList();
        // Primary game module: prefer one matching the project name, else the first.
        var primary = moduleNames.FirstOrDefault(m => string.Equals(m, projectName, StringComparison.OrdinalIgnoreCase))
                      ?? moduleNames.First();

        // Belt-and-braces with CleanMarkedStubs (Program calls it before real-module discovery):
        // drop any marked dirs that appeared since (direct API use).
        CleanMarkedStubs(outputRoot);

        var report = new StringBuilder();
        report.AppendLine($"# Stub generation report — {stubs.Count} types across {moduleNames.Count} module(s)");
        report.AppendLine($"Primary game module: {primary}").AppendLine();

        foreach (var module in byModule.Keys.OrderBy(m => m))
            WriteModule(outputRoot, module, byModule[module], module == primary, report);

        WriteTargetFiles(outputRoot, projectName, moduleNames);
        // Do NOT register the modules in the .uproject: uncompiled C++ modules make the editor
        // refuse to open the project ("game module could not be loaded"). The project stays
        // Blueprint-only and opens everywhere; owners with a toolchain compile Source/ themselves
        // (see STUBS_REPORT.md) and re-add the built modules to Modules afterwards.
        report.AppendLine("Modules are NOT registered in the .uproject (it stays Blueprint-only so it opens");
        report.AppendLine("without a compiler). After building Source/ in Visual Studio, re-add the module");
        report.AppendLine("names to the .uproject \"Modules\" list (Type Runtime, LoadingPhase Default).").AppendLine();

        File.WriteAllText(Path.Combine(outputRoot, "STUBS_REPORT.md"), report.ToString());
        if (stubs.Count == 0)
            Log.Information("--emit-stubs: registered {N} recovered native module(s); no stub types to generate. See STUBS_REPORT.md",
                moduleNames.Count);
        else
            Log.Information("--emit-stubs: generated {N} stub module(s) ({Types} types). Generate VS project files and build. See STUBS_REPORT.md",
                byModule.Count, stubs.Count);
    }

    private void WriteModule(string outputRoot, string module, List<GameStub> types, bool isPrimary, StringBuilder report)
    {
        var dir = Path.Combine(outputRoot, "Source", module);
        Directory.CreateDirectory(Path.Combine(dir, "Public"));
        Directory.CreateDirectory(Path.Combine(dir, "Private"));
        File.WriteAllText(Path.Combine(dir, ".ue4d_stub"), "generated by UE4Decompiler StubModuleGenerator; safe to delete/regenerate");

        // ── Build.cs ──
        File.WriteAllText(Path.Combine(dir, $"{module}.Build.cs"),
            $$"""
            using UnrealBuildTool;

            public class {{module}} : ModuleRules
            {
                public {{module}}(ReadOnlyTargetRules Target) : base(Target)
                {
                    PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
                    PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine", "UMG", "AIModule" });
                }
            }
            """);

        // ── Public header ──
        var h = new StringBuilder();
        h.AppendLine("#pragma once").AppendLine();
        h.AppendLine("#include \"CoreMinimal.h\"");
        foreach (var inc in new[]
        {
            "GameFramework/Actor.h","GameFramework/Pawn.h","GameFramework/Character.h","GameFramework/Controller.h",
            "GameFramework/PlayerController.h","GameFramework/PlayerState.h","GameFramework/GameModeBase.h",
            "GameFramework/GameStateBase.h","GameFramework/GameUserSettings.h","GameFramework/HUD.h",
            "GameFramework/SaveGame.h","GameFramework/Volume.h","Engine/GameInstance.h","Engine/LocalPlayer.h",
            "Engine/LevelScriptActor.h","Engine/DataAsset.h","Animation/AnimInstance.h","Components/ActorComponent.h",
            "Components/SceneComponent.h","Components/StaticMeshComponent.h","Components/LightComponent.h",
            "Components/PointLightComponent.h","Components/SpotLightComponent.h","Components/DirectionalLightComponent.h",
            "Camera/PlayerCameraManager.h","AIController.h","Blueprint/UserWidget.h",
        }) h.AppendLine($"#include \"{inc}\"");
        h.AppendLine($"#include \"{module}.generated.h\"").AppendLine();

        // Unknown-struct shell: once per module header, project-unique name (UHT rejects same-named
        // types even across modules).
        string stubStruct = $"F{module}StubStruct";
        bool moduleNeedsStubStruct = types.Any(t => t.Kind == "Class"
            && _methodSigs.Where(kv => kv.Key.StartsWith($"{module}.{t.Name}:", StringComparison.Ordinal))
                .Any(kv => kv.Value.Params.Any(p => p.Cat == "struct")
                    || (kv.Value.Ret.HasValue && kv.Value.Ret.Value.Cat == "struct")));
        if (moduleNeedsStubStruct)
            h.AppendLine("USTRUCT(BlueprintType)").AppendLine($"struct {stubStruct} {{ GENERATED_BODY() }};").AppendLine();

        var classCount = 0; var structCount = 0; var enumCount = 0;
        foreach (var t in types.OrderBy(t => t.Name))
        {
            switch (t.Kind)
            {
                case "Enum":
                    h.AppendLine($"UENUM(BlueprintType)").AppendLine($"enum class {SanitizeEnum(t.Name)} : uint8 {{ Stub = 0 }};").AppendLine();
                    enumCount++; break;
                case "ScriptStruct":
                    h.AppendLine($"USTRUCT(BlueprintType)").AppendLine($"struct {StructCpp(t.Name)} {{ GENERATED_BODY() }};").AppendLine();
                    structCount++; break;
                default: // Class
                    string cpp, baseClass;
                    if (_baseHints.TryGetValue($"{module}.{t.Name}", out var hintBase))
                        (cpp, baseClass) = (hintBase[0] + t.Name, hintBase);   // base inferred from actual usage
                    else
                        (cpp, baseClass) = ResolveClass(t.Name);               // fall back to name-suffix heuristic
                    var key = $"{module}.{t.Name}";
                    // Typed stubs for this class (keyed "Module.Class:func").
                    var sigs = _methodSigs.Where(kv => kv.Key.StartsWith(key + ":", StringComparison.Ordinal))
                        .OrderBy(kv => kv.Key).ToList();
                    h.AppendLine("UCLASS(Blueprintable)");
                    h.AppendLine($"class {cpp} : public {baseClass}");
                    h.AppendLine("{");
                    h.AppendLine("    GENERATED_BODY()");
                    if (sigs.Count > 0)
                    {
                        // Typed stubs: signatures mirror the emitted call pins so they bind.
                        h.AppendLine("public:");
                        foreach (var kv in sigs)
                        {
                            var s = kv.Value;
                            var mname = kv.Key[(kv.Key.LastIndexOf(':') + 1)..];
                            var ret = s.Ret.HasValue ? CppType(s.Ret.Value.Cat, 0, stubStruct) : "void";
                            var parms = new List<string>(s.Params.Select(p => $"{CppType(p.Cat, p.Cont, stubStruct)} {p.Name}"));
                            if (s.Outs != null)
                                foreach (var o in s.Outs)
                                    parms.Add(s.StaticCall
                                        ? $"{CppType(o.Cat, 0, stubStruct)}& {o.Name}"
                                        : $"UPARAM(ref) {CppType(o.Cat, 0, stubStruct)}& {o.Name}");
                            var parmStr = string.Join(", ", parms);
                            if (s.StaticCall)
                            {
                                h.AppendLine("    UFUNCTION(BlueprintCallable, Category=\"Recovered\")");
                                // Pointers can't value-initialize with T() — return nullptr for those.
                                var defRet = ret.EndsWith("*") ? "nullptr" : $"{ret}()";
                                var initOuts = s.Outs != null
                                    ? string.Concat(s.Outs.Select(o =>
                                    {
                                        var ot = CppType(o.Cat, 0, stubStruct);
                                        return $" {o.Name} = {(ot.EndsWith("*") ? "nullptr" : $"{ot}()")};";
                                    }))
                                    : "";
                                h.AppendLine($"    static {ret} {mname}({parmStr}) {{{initOuts} {(s.Ret.HasValue ? $"return {defRet};" : "")} }}");
                            }
                            else
                            {
                                h.AppendLine("    UFUNCTION(BlueprintCallable, BlueprintImplementableEvent, Category=\"Recovered\")");
                                h.AppendLine($"    {ret} {mname}({parmStr});");
                            }
                        }
                        report.AppendLine($"  [{module}] class {cpp} : {baseClass} ({sigs.Count} sig(s))");
                    }
                    else if (_methodHints.TryGetValue(key, out var methods) && methods.Count > 0)
                    {
                        h.AppendLine("public:");
                        foreach (var method in methods)
                        {
                            h.AppendLine("    UFUNCTION(BlueprintCallable, BlueprintImplementableEvent, Category=\"Recovered\")");
                            h.AppendLine($"    void {method}();");
                        }
                    }
                    h.AppendLine("};").AppendLine();
                    if (sigs.Count == 0)
                        report.AppendLine($"  [{module}] class {cpp} : {baseClass}" +
                                          (_methodHints.TryGetValue(key, out var reportMethods) && reportMethods.Count > 0
                                              ? $" ({reportMethods.Count} method stub(s))" : ""));
                    classCount++; break;
            }
        }
        File.WriteAllText(Path.Combine(dir, "Public", $"{module}.h"), h.ToString());

        // ── Private cpp (module impl) ──
        var moduleImpl = isPrimary
            ? $"IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, {module}, \"{module}\");"
            : $"IMPLEMENT_MODULE(FDefaultModuleImpl, {module});";
        File.WriteAllText(Path.Combine(dir, "Private", $"{module}.cpp"),
            $"#include \"{module}.h\"\n#include \"Modules/ModuleManager.h\"\n\n{moduleImpl}\n");

        Log.Information("  stub module {Module}: {C} classes, {S} structs, {E} enums", module, classCount, structCount, enumCount);
    }

    private void WriteTargetFiles(string outputRoot, string projectName, List<string> modules)
    {
        var sourceDir = Path.Combine(outputRoot, "Source");
        Directory.CreateDirectory(sourceDir);
        var list = string.Join(", ", modules.Select(m => $"\"{m}\""));

        File.WriteAllText(Path.Combine(sourceDir, $"{projectName}.Target.cs"),
            $$"""
            using UnrealBuildTool;
            using System.Collections.Generic;

            public class {{projectName}}Target : TargetRules
            {
                public {{projectName}}Target(TargetInfo Target) : base(Target)
                {
                    Type = TargetType.Game;
                    DefaultBuildSettings = BuildSettingsVersion.Latest;
                    IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
                    CppStandard = CppStandardVersion.Cpp20;
                    // Stub project: the engine source may emit warnings under a newer toolchain (e.g. VS 2026 hits
                    // C4668 __has_feature in engine headers). Don't fail the build on those — we only need the stub
                    // classes to link so the editor opens.
                    bWarningsAsErrors = false;
                    ExtraModuleNames.AddRange(new string[] { {{list}} });
                }
            }
            """);

        File.WriteAllText(Path.Combine(sourceDir, $"{projectName}Editor.Target.cs"),
            $$"""
            using UnrealBuildTool;
            using System.Collections.Generic;

            public class {{projectName}}EditorTarget : TargetRules
            {
                public {{projectName}}EditorTarget(TargetInfo Target) : base(Target)
                {
                    Type = TargetType.Editor;
                    DefaultBuildSettings = BuildSettingsVersion.Latest;
                    IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
                    CppStandard = CppStandardVersion.Cpp20;
                    bWarningsAsErrors = false;   // tolerate engine-header warnings under newer toolchains (VS 2026 C4668)
                    ExtraModuleNames.AddRange(new string[] { {{list}} });
                }
            }
            """);
    }

    /// <summary>Pin category (+container) to compilable UFUNCTION C++ type. Unknown structs share one
    /// empty per-module shell (pins drop, node still compiles); everything else binds by category.
    /// Containers emit const-ref (UHT requires TArray/TSet/TMap params by reference).</summary>
    private static string CppType(string cat, byte cont, string stubStruct, bool isOut = false)
    {
        string inner = cat switch
        {
            "bool" => "bool",
            "int" => "int32",
            "float" => "float",
            "string" => "FString",
            "name" => "FName",
            "text" => "FText",
            "byte" => "uint8",
            "object" => "UObject*",
            // NOTE: FScriptDelegate is not a UHT-visible UFUNCTION param type (build error); untyped
            // delegate pins ride UObject* (wire usually drops, node still compiles).
            "delegate" => "UObject*",
            "struct" => stubStruct,
            _ => "int32",   // wildcard + unknown
        };
        if (cont == 1) return isOut ? $"TArray<{inner}>&" : $"const TArray<{inner}>&";
        if (cont == 2) return isOut ? $"TSet<{inner}>&" : $"const TSet<{inner}>&";
        if (cont == 3) return isOut ? "TMap<FString, FString>&" : "const TMap<FString, FString>&";
        return inner;
    }

    // ── Inference ─────────────────────────────────────────────────────────────────────────────────
    // Suffix -> (engine base, header). Order matters (most specific first). Prefix derives from base.
    private static readonly (string suffix, string baseClass)[] BaseBySuffix =
    {
        ("AnimInstance","UAnimInstance"), ("GameModeBase","AGameModeBase"), ("GameMode","AGameModeBase"),
        ("GameStateBase","AGameStateBase"), ("GameState","AGameStateBase"), ("GameInstance","UGameInstance"),
        ("GameUserSettings","UGameUserSettings"), ("PlayerCameraManager","APlayerCameraManager"),
        ("PlayerController","APlayerController"), ("PlayerState","APlayerState"), ("LocalPlayer","ULocalPlayer"),
        ("LevelScriptActor","ALevelScriptActor"), ("AIController","AAIController"), ("Character","ACharacter"),
        ("Controller","AController"), ("Pawn","APawn"), ("HUD","AHUD"), ("SaveGame","USaveGame"),
        ("DataAsset","UDataAsset"), ("UserWidget","UUserWidget"), ("SceneComponent","USceneComponent"),
        ("Component","UActorComponent"), ("Volume","AVolume"), ("Actor","AActor"),
    };

    private (string cpp, string baseClass) ResolveClass(string name)
    {
        foreach (var (suffix, baseClass) in BaseBySuffix)
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return (baseClass[0] + name, baseClass);

        // No suffix match: use the SDK-recorded prefix if known, else default to UObject.
        if (_sdkPrefix.TryGetValue(name, out var p))
            return p == 'A' ? ("A" + name, "AActor") : ("U" + name, "UObject");
        return ("U" + name, "UObject");
    }

    private static Dictionary<string, SortedSet<string>> ParseMethodHints(IReadOnlyCollection<string>? hints)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (hints is null) return map;
        foreach (var hint in hints)
        {
            var colon = hint.LastIndexOf(':');
            if (colon <= 0 || colon + 1 >= hint.Length) continue;
            var key = hint[..colon];
            var method = hint[(colon + 1)..];
            if (IsDelegateSignatureFunction(method)) continue;
            if (!IsCppIdentifier(method)) continue;
            if (!map.TryGetValue(key, out var methods))
                map[key] = methods = new SortedSet<string>(StringComparer.Ordinal);
            methods.Add(method);
        }
        return map;
    }

    private static bool IsDelegateSignatureFunction(string name) =>
        name.EndsWith("__DelegateSignature", StringComparison.Ordinal)
        || name.EndsWith("_DelegateSignature", StringComparison.Ordinal);

    private static bool IsCppIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!(value[0] == '_' || char.IsLetter(value[0]))) return false;
        for (var i = 1; i < value.Length; i++)
            if (!(value[i] == '_' || char.IsLetterOrDigit(value[i]))) return false;
        return true;
    }

    private static string StructCpp(string name) =>
        name.Length > 1 && name[0] == 'F' && char.IsUpper(name[1]) ? name : "F" + name;

    private static string SanitizeEnum(string name) =>
        name.Length > 1 && name[0] == 'E' && char.IsUpper(name[1]) ? name : "E" + name;

    /// <summary>Parse a Dumper SDK's "// CLASS: &lt;Prefixed&gt;" lines into bareName -> prefix char.</summary>
    private static Dictionary<string, char> LoadSdkPrefixes(string sdkDir)
    {
        var map = new Dictionary<string, char>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(sdkDir, "*.hpp", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.StartsWith("// CLASS:", StringComparison.Ordinal)) continue;
                var ident = line["// CLASS:".Length..].Trim();
                if (ident.Length < 2 || ident.Contains('<')) continue;      // skip templates
                var prefix = ident[0];
                if (prefix is not ('A' or 'U' or 'F' or 'E')) continue;
                var bare = ident[1..];
                if (bare.Length > 0 && char.IsUpper(bare[0])) map[bare] = prefix;
            }
        }
        Log.Information("--emit-stubs: loaded {N} class prefixes from SDK dump", map.Count);
        return map;
    }
}

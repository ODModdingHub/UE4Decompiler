using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.Utils;
using Newtonsoft.Json;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Output;

/// <summary>
/// Sets up the project root. Shipped paks almost always contain the REAL <c>.uproject</c> and
/// <c>Config/Default*.ini</c> (with the true plugin/module list), so we extract those verbatim and
/// only fall back to generated stand-ins for anything genuinely missing.
/// </summary>
public sealed class ProjectScaffold
{
    private readonly DecompileOptions _opts;
    private readonly AbstractFileProvider _provider;

    public ProjectScaffold(DecompileOptions opts, AbstractFileProvider provider)
    {
        _opts = opts;
        _provider = provider;
    }

    public void Generate()
    {
        Directory.CreateDirectory(_opts.OutputRoot);
        Directory.CreateDirectory(Path.Combine(_opts.OutputRoot, "Config"));
        Directory.CreateDirectory(_opts.ContentRoot);
        Directory.CreateDirectory(Path.Combine(_opts.OutputRoot, "Scripts"));

        var realUproject = ExtractUProject();
        var realConfigs = ExtractConfigs();

        if (!realUproject) WriteUProject();
        if (!realConfigs)
        {
            WriteDefaultEngine();
            WriteDefaultGame();
            WriteDefaultEditor();
            WriteDefaultInput();
        }
        WriteMasterPythonScript();
        Log.Information("Scaffolded project {Name} (engine {Assoc}) at {Root} [uproject={U}, configs={C}]",
            _opts.ProjectName, _opts.EngineAssociation, _opts.OutputRoot,
            realUproject ? "real" : "generated", realConfigs ? "real" : "generated");
    }

    /// <summary>
    /// Extract the real .uproject and patch it for opening in a stock binary editor:
    /// EngineAssociation -> version string, strip native game Modules, and drop project/code Plugins
    /// (detected by their content shipping under a non-Engine /Plugins/&lt;Name&gt;/ path) that won't
    /// exist in a vanilla engine. Engine plugins are kept.
    /// </summary>
    private bool ExtractUProject()
    {
        var key = _provider.Files.Keys.FirstOrDefault(k => k.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase));
        if (key is null) return false;
        try
        {
            var json = Encoding.UTF8.GetString(_provider.SaveAsset(key));
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);

            root["EngineAssociation"] = _opts.EngineAssociation; // GUID/source-build -> "4.21"

            var strippedModules = (root["Modules"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
            root["Modules"] = new Newtonsoft.Json.Linq.JArray(); // native game modules: no DLLs in stock engine

            // Clear the Plugins list entirely. UnrealBuildTool hard-fails on ANY Enabled+missing plugin
            // (e.g. code-only ones like HapticsManager that ship no /Plugins/ content), and we can't
            // reliably tell which are stock-engine. The editor still enables its built-in defaults, so an
            // empty list keeps the project compilable/openable; re-add a specific engine plugin if needed.
            var droppedPlugins = (root["Plugins"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
            root["Plugins"] = new Newtonsoft.Json.Linq.JArray();

            var dest = Path.Combine(_opts.OutputRoot, key.SubstringAfterLast('/'));
            File.WriteAllText(dest, root.ToString(Newtonsoft.Json.Formatting.Indented));
            Log.Information("Extracted + patched .uproject {Name}: assoc->{Assoc}, stripped {M} module(s), cleared {P} plugin entr(ies)",
                Path.GetFileName(dest), _opts.EngineAssociation, strippedModules, droppedPlugins);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to extract/patch real .uproject {Key}; will generate one", key);
            return false;
        }
    }

    /// <summary>Plugin names whose content ships under a non-Engine "/Plugins/&lt;Name&gt;/" path (project/code plugins).</summary>
    private HashSet<string> DetectProjectPlugins()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string marker = "/Plugins/";
        foreach (var k in _provider.Files.Keys)
        {
            if (k.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)) continue;
            var idx = k.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var rest = k[(idx + marker.Length)..];
            var name = rest.SubstringBefore('/');
            if (!string.IsNullOrEmpty(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>Extract the game's real Config/*.ini files (not Engine/Config) preserving sub-structure.</summary>
    private bool ExtractConfigs()
    {
        var keys = _provider.Files.Keys
            .Where(k => k.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                        && k.Contains("/Config/", StringComparison.OrdinalIgnoreCase)
                        && !k.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keys.Count == 0) return false;

        var written = 0;
        foreach (var key in keys)
        {
            try
            {
                var sub = key[(key.IndexOf("/Config/", StringComparison.OrdinalIgnoreCase) + "/Config/".Length)..];
                var dest = Path.Combine(_opts.OutputRoot, "Config", sub.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var bytes = _provider.SaveAsset(key);
                // DefaultEngine.ini overrides core engine classes to game natives (LocalPlayerClassName=
                // /Script/Pavlov.PavlovLocalPlayer, GlobalDefaultGameMode=..., GameInstanceClass=...). These load at
                // editor startup before anything can fix them -> instant crash. Reset them to engine defaults.
                if (Path.GetFileName(dest).Equals("DefaultEngine.ini", StringComparison.OrdinalIgnoreCase))
                    bytes = Encoding.UTF8.GetBytes(SanitizeEngineIni(Encoding.UTF8.GetString(bytes)));
                File.WriteAllBytes(dest, bytes);
                written++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to extract config {Key}", key);
            }
        }
        Log.Information("Extracted {N} real config .ini file(s)", written);
        return written > 0;
    }

    /// <summary>Reset DefaultEngine.ini's core-class overrides (which point at game-native /Script classes) back to
    /// engine defaults so the editor doesn't crash at startup loading classes that don't exist in a stock engine.</summary>
    private static readonly (string key, string engineDefault)[] EngineClassDefaults =
    {
        ("LocalPlayerClassName", "/Script/Engine.LocalPlayer"),
        ("GameUserSettingsClassName", "/Script/Engine.GameUserSettings"),
        ("PhysicsCollisionHandlerClassName", "/Script/Engine.PhysicsCollisionHandler"),
        ("LevelScriptActorClassName", "/Script/Engine.LevelScriptActor"),
        ("GameViewportClientClassName", "/Script/Engine.GameViewportClient"),
        ("GameInstanceClass", "/Script/Engine.GameInstance"),
        ("GlobalDefaultGameMode", "/Script/Engine.GameModeBase"),
        ("GlobalDefaultServerGameMode", "/Script/Engine.GameModeBase"),
    };

    internal static string SanitizeEngineIni(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            foreach (var (k, def) in EngineClassDefaults)
            {
                if (!key.Equals(k, StringComparison.OrdinalIgnoreCase)) continue;
                var val = line[(eq + 1)..].Trim();
                if (!val.StartsWith("/Script/Engine.", StringComparison.OrdinalIgnoreCase))   // already engine? leave
                    lines[i] = $"{k}={def}";
                break;
            }
        }
        return string.Join("\n", lines);
    }

    private void WriteUProject()
    {
        var uproject = new
        {
            FileVersion = 3,
            EngineAssociation = _opts.EngineAssociation,
            Category = "",
            Description = "Decompiled by UE4Decompiler",
            Modules = Array.Empty<object>(),
            Plugins = Array.Empty<object>()
        };
        var path = Path.Combine(_opts.OutputRoot, _opts.ProjectName + ".uproject");
        File.WriteAllText(path, JsonConvert.SerializeObject(uproject, Formatting.Indented));
    }

    private void WriteDefaultEngine()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[/Script/Engine.Engine]");
        sb.AppendLine("+ActiveGameNameRedirects=(OldGameName=\"/Script/UE4Game\", NewGameName=\"/Script/" + _opts.ProjectName + "\")");
        sb.AppendLine();
        sb.AppendLine("[/Script/EngineSettings.GameMapsSettings]");
        sb.AppendLine("; Set your default maps once recovered .umap assets are validated");
        sb.AppendLine();
        sb.AppendLine("[Core.System]");
        sb.AppendLine("Paths=../../../Engine/Content");
        sb.AppendLine("Paths=%GAMEDIR%Content");
        if (_opts.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0)
        {
            sb.AppendLine();
            sb.AppendLine("[/Script/Engine.RendererSettings]");
            sb.AppendLine("r.DynamicGlobalIlluminationMethod=1");
            sb.AppendLine("r.ReflectionMethod=1");
            sb.AppendLine("r.Shadow.Virtual.Enable=1");
            sb.AppendLine("r.Nanite=1");
            sb.AppendLine("r.Lumen.DiffuseIndirect.Allow=1");
        }
        sb.AppendLine();
        sb.AppendLine("[Plugins]");
        sb.AppendLine("+EnabledPlugins=\"EnhancedInput\"");
        sb.AppendLine("+EnabledPlugins=\"PythonScriptPlugin\"");
        sb.AppendLine("+EnabledPlugins=\"EditorScriptingUtilities\"");
        Write("DefaultEngine.ini", sb.ToString());
    }

    private void WriteDefaultInput()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[/Script/Engine.InputSettings]");
        sb.AppendLine("bUseMouseForTouch=False");
        sb.AppendLine("bEnableMouseSmoothing=True");
        sb.AppendLine("+ActionMappings=(ActionName=\"Jump\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=SpaceBar)");
        sb.AppendLine("+ActionMappings=(ActionName=\"Fire\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=LeftMouseButton)");
        sb.AppendLine("+ActionMappings=(ActionName=\"Interact\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=E)");
        sb.AppendLine("+AxisMappings=(AxisName=\"MoveForward\",Scale=1.000000,Key=W)");
        sb.AppendLine("+AxisMappings=(AxisName=\"MoveForward\",Scale=-1.000000,Key=S)");
        sb.AppendLine("+AxisMappings=(AxisName=\"MoveRight\",Scale=1.000000,Key=D)");
        sb.AppendLine("+AxisMappings=(AxisName=\"MoveRight\",Scale=-1.000000,Key=A)");
        sb.AppendLine("+AxisMappings=(AxisName=\"Turn\",Scale=1.000000,Key=MouseX)");
        sb.AppendLine("+AxisMappings=(AxisName=\"LookUp\",Scale=-1.000000,Key=MouseY)");
        Write("DefaultInput.ini", sb.ToString());
    }

    private void WriteDefaultGame()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[/Script/EngineSettings.GeneralProjectSettings]");
        sb.AppendLine("ProjectID=" + Guid.NewGuid().ToString("B").ToUpperInvariant());
        sb.AppendLine("ProjectName=" + _opts.ProjectName);
        sb.AppendLine("Description=Decompiled by UE4Decompiler");
        sb.AppendLine("CompanyName=");
        Write("DefaultGame.ini", sb.ToString());
    }

    private void WriteDefaultEditor()
    {
        // Empty but present sections keep the editor from warning about missing config.
        var sb = new StringBuilder();
        sb.AppendLine("[/Script/UnrealEd.EditorEngine]");
        sb.AppendLine();
        sb.AppendLine("[/Script/UnrealEd.EditorLoadingSavingSettings]");
        Write("DefaultEditor.ini", sb.ToString());
    }

    public void WriteDefaultGameplayTags(IEnumerable<string> tags)
    {
        var tagList = tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        if (tagList.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("[/Script/GameplayTags.GameplayTagsSettings]");
        sb.AppendLine("ImportTagsFromConfig=True");
        sb.AppendLine("WarnOnInvalidTags=True");
        sb.AppendLine("FastReplication=True");
        foreach (var tag in tagList)
        {
            sb.AppendLine($"+GameplayTagList=(Tag=\"{tag}\",DevComment=\"\")");
        }
        Write("DefaultGameplayTags.ini", sb.ToString());
        Log.Information("Scaffolded DefaultGameplayTags.ini with {Count} discovered gameplay tag(s)", tagList.Count);
    }

    public void WriteDefaultCollision(IEnumerable<string> profiles, IEnumerable<string> channels)
    {
        var profileList = profiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        var channelList = channels.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        if (profileList.Count == 0 && channelList.Count == 0) return;

        var defaultEnginePath = Path.Combine(_opts.OutputRoot, "Config", "DefaultEngine.ini");
        var sb = new StringBuilder();
        if (File.Exists(defaultEnginePath))
            sb.Append(File.ReadAllText(defaultEnginePath));
        else
            sb.AppendLine("[/Script/Engine.CollisionProfile]");

        sb.AppendLine();
        sb.AppendLine("[/Script/Engine.CollisionProfile]");
        foreach (var ch in channelList)
        {
            sb.AppendLine($"+DefaultChannelResponses=(Channel={ch},DefaultResponse=ECR_Block,bTraceType=False,bStaticObject=False)");
        }
        foreach (var prof in profileList)
        {
            sb.AppendLine($"+Profiles=(Name=\"{prof}\",CollisionEnabled=ECollisionEnabled::QueryAndPhysics,ObjectTypeName=\"WorldStatic\",Description=\"Recovered collision profile {prof}\")");
        }
        File.WriteAllText(defaultEnginePath, sb.ToString());
        Log.Information("Appended {Profiles} collision profile(s) and {Channels} channel(s) to DefaultEngine.ini", profileList.Count, channelList.Count);
    }

    private void WriteMasterPythonScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("# UE4Decompiler Master Batch Reconstruction Runner");
        sb.AppendLine("# Executes all level, skeletal socket, material instance, sound cue, and");
        sb.AppendLine("# curve reconstruction scripts in batch.");
        sb.AppendLine("# Run via Unreal Editor Output Log (Python):");
        sb.AppendLine("#     py \"Scripts/ReconstructAllLevels.py\"");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal, os");
        sb.AppendLine();
        sb.AppendLine("def run_all():");
        sb.AppendLine("    proj_dir = unreal.SystemLibrary.get_project_directory()");
        sb.AppendLine("    content_dir = os.path.join(proj_dir, 'Content')");
        sb.AppendLine("    unreal.log(f'>>> [UE4Decompiler] Scanning {content_dir} for reconstruction scripts...')");
        sb.AppendLine("    count = 0");
        sb.AppendLine("    suffixes = ('_reconstruct.py', '_sockets.py', '_mic_setup.py', '_soundcue.py', '_attenuation.py', '_setup.py')");
        sb.AppendLine("    for root, dirs, files in os.walk(content_dir):");
        sb.AppendLine("        for f in files:");
        sb.AppendLine("            if f.endswith(suffixes):");
        sb.AppendLine("                path = os.path.join(root, f)");
        sb.AppendLine("                unreal.log(f'>>> [UE4Decompiler] Running script: {f}...')");
        sb.AppendLine("                try:");
        sb.AppendLine("                    with open(path, 'r', encoding='utf-8') as sfile:");
        sb.AppendLine("                        exec(sfile.read(), globals())");
        sb.AppendLine("                    count += 1");
        sb.AppendLine("                except Exception as ex:");
        sb.AppendLine("                    unreal.log_warning(f'Error executing {f}: {ex}')");
        sb.AppendLine("    unreal.log(f'>>> [UE4Decompiler] Batch reconstruction complete! Ran {count} script(s).')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    run_all()");

        var scriptDir = Path.Combine(_opts.OutputRoot, "Scripts");
        if (!Directory.Exists(scriptDir)) Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, "ReconstructAllLevels.py");
        File.WriteAllText(scriptPath, sb.ToString(), Encoding.UTF8);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_opts.OutputRoot, "Config", name), content);
}

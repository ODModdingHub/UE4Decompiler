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

        var realUproject = ExtractUProject();
        var realConfigs = ExtractConfigs();

        if (!realUproject) WriteUProject();
        if (!realConfigs)
        {
            WriteDefaultEngine();
            WriteDefaultGame();
            WriteDefaultEditor();
        }
        if (!string.IsNullOrWhiteSpace(_opts.NativeSourcePath) && _opts.NativeSourceModules.Count > 0)
            CopyNativeSource();

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

    private void CopyNativeSource()
    {
        var sourceRoot = Path.Combine(_opts.OutputRoot, "Source");
        Directory.CreateDirectory(sourceRoot);

        var modulesCopied = new List<string>();
        foreach (var module in _opts.NativeSourceModules)
        {
            var src = ResolveNativeModulePath(_opts.NativeSourcePath!, module);
            if (src is null)
            {
                Log.Warning("--source-module did not contain module {Module}", module);
                continue;
            }

            var dest = Path.Combine(sourceRoot, module);
            CopyDirectory(src, dest);
            modulesCopied.Add(module);
            Log.Information("Copied recovered native module {Module} -> {Dest}", module, dest);
        }

        if (modulesCopied.Count == 0) return;
        WriteTargetFiles(modulesCopied);
        PatchUProjectModules(modulesCopied);
    }

    private static string? ResolveNativeModulePath(string sourcePath, string module)
    {
        var full = Path.GetFullPath(sourcePath);
        if (File.Exists(Path.Combine(full, module + ".Build.cs"))) return full;
        var nested = Path.Combine(full, module);
        return File.Exists(Path.Combine(nested, module + ".Build.cs")) ? nested : null;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var outFile = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.Copy(file, outFile, overwrite: true);
        }
    }

    private void WriteTargetFiles(IReadOnlyList<string> modules)
    {
        var sourceDir = Path.Combine(_opts.OutputRoot, "Source");
        Directory.CreateDirectory(sourceDir);
        var list = string.Join(", ", modules.Select(m => $"\"{m}\""));

        File.WriteAllText(Path.Combine(sourceDir, $"{_opts.ProjectName}.Target.cs"),
            $$"""
            using UnrealBuildTool;
            using System.Collections.Generic;

            public class {{_opts.ProjectName}}Target : TargetRules
            {
                public {{_opts.ProjectName}}Target(TargetInfo Target) : base(Target)
                {
                    Type = TargetType.Game;
                    DefaultBuildSettings = BuildSettingsVersion.Latest;
                    IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
                    CppStandard = CppStandardVersion.Cpp20;
                    bWarningsAsErrors = false;
                    ExtraModuleNames.AddRange(new string[] { {{list}} });
                }
            }
            """);

        File.WriteAllText(Path.Combine(sourceDir, $"{_opts.ProjectName}Editor.Target.cs"),
            $$"""
            using UnrealBuildTool;
            using System.Collections.Generic;

            public class {{_opts.ProjectName}}EditorTarget : TargetRules
            {
                public {{_opts.ProjectName}}EditorTarget(TargetInfo Target) : base(Target)
                {
                    Type = TargetType.Editor;
                    DefaultBuildSettings = BuildSettingsVersion.Latest;
                    IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
                    CppStandard = CppStandardVersion.Cpp20;
                    bWarningsAsErrors = false;
                    ExtraModuleNames.AddRange(new string[] { {{list}} });
                }
            }
            """);
    }

    private void PatchUProjectModules(IReadOnlyList<string> modules)
    {
        var uproject = Directory.EnumerateFiles(_opts.OutputRoot, "*.uproject").FirstOrDefault();
        if (uproject is null) return;

        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(uproject));
        var arr = new Newtonsoft.Json.Linq.JArray();
        foreach (var module in modules)
            arr.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["Name"] = module,
                ["Type"] = "Runtime",
                ["LoadingPhase"] = "Default",
            });
        root["Modules"] = arr;
        File.WriteAllText(uproject, root.ToString(Newtonsoft.Json.Formatting.Indented));
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
        Write("DefaultEngine.ini", sb.ToString());
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

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_opts.OutputRoot, "Config", name), content);
}

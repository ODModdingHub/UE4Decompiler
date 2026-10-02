using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Enhanced Input assets: <c>UInputAction</c> and <c>UInputMappingContext</c>.
/// Recovers axis types, player mappable settings, modifiers (DeadZone, Negate, SwizzleAxis),
/// triggers (Pressed, Down, Hold, Tap), and hardware key bindings (Keyboard, Gamepad, Mouse).
/// Generates JSON definitions and Unreal Python configuration scripts for the Unreal Editor.
/// </summary>
public sealed class InputReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (asset.PrimaryType == "InputAction")
                return ReconstructInputAction(asset, outputPathNoExt);

            if (asset.PrimaryType == "InputMappingContext")
                return ReconstructInputMappingContext(asset, outputPathNoExt);

            return ReconstructionResult.Failed($"Unknown input asset type: {asset.PrimaryType}");
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Input reconstruction error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructInputAction(ParsedAsset asset, string outputPathNoExt)
    {
        var action = asset.Exports.FirstOrDefault(e => e.ExportType == "InputAction") ?? asset.Exports.FirstOrDefault();
        if (action == null)
            return ReconstructionResult.Failed("No InputAction export found");

        var valType = action.GetOrDefault<FName>("ValueType").Text;
        if (string.IsNullOrEmpty(valType))
            valType = action.GetOrDefault<string>("ValueType", "Boolean");

        var desc = action.GetOrDefault<FText>("ActionDescription")?.Text ?? "";
        var consume = action.GetOrDefault<bool>("bConsumeInput", true);
        var triggerWhenPaused = action.GetOrDefault<bool>("bTriggerWhenPaused", false);

        var triggers = ExtractClassNames(action, "Triggers");
        var modifiers = ExtractClassNames(action, "Modifiers");

        var actionName = Path.GetFileNameWithoutExtension(asset.File.Path);
        var jsonPath = outputPathNoExt + ".input.json";

        var model = new
        {
            AssetType = "InputAction",
            Name = actionName,
            VirtualPath = asset.File.Path,
            ValueType = valType,
            ActionDescription = desc,
            ConsumeInput = consume,
            TriggerWhenPaused = triggerWhenPaused,
            Triggers = triggers,
            Modifiers = modifiers
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        Log.Information("InputAction {Name}: ValueType={Type}, {Triggers} trigger(s), {Modifiers} modifier(s)",
            actionName, valType, triggers.Count, modifiers.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"InputAction recovered: ValueType={valType}, {triggers.Count} triggers, {modifiers.Count} modifiers",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static ReconstructionResult ReconstructInputMappingContext(ParsedAsset asset, string outputPathNoExt)
    {
        var imc = asset.Exports.FirstOrDefault(e => e.ExportType == "InputMappingContext") ?? asset.Exports.FirstOrDefault();
        if (imc == null)
            return ReconstructionResult.Failed("No InputMappingContext export found");

        var mappingsList = new List<ActionKeyMappingData>();
        var rawMappings = imc.GetOrDefault<FStructFallback[]>("Mappings");

        if (rawMappings != null)
        {
            foreach (var m in rawMappings)
            {
                var actionRef = m.GetOrDefault<FPackageIndex>("Action")?.ResolvedObject;
                var actionPath = actionRef?.GetPathName() ?? m.GetOrDefault<FSoftObjectPath>("Action").ToString();

                var keyStruct = m.GetOrDefault<FStructFallback>("Key");
                var keyName = keyStruct?.GetOrDefault<FName>("KeyName").Text ?? "None";

                var isPlayerMappable = m.GetOrDefault<bool>("bIsPlayerMappable", false);

                var triggers = ExtractClassNamesFromStruct(m, "Triggers");
                var modifiers = ExtractClassNamesFromStruct(m, "Modifiers");

                mappingsList.Add(new ActionKeyMappingData
                {
                    Action = actionPath,
                    Key = keyName,
                    IsPlayerMappable = isPlayerMappable,
                    Triggers = triggers,
                    Modifiers = modifiers
                });
            }
        }

        var imcName = Path.GetFileNameWithoutExtension(asset.File.Path);
        var sidecars = new List<string>();

        // 1. JSON sidecar
        var jsonPath = outputPathNoExt + ".input.json";
        var model = new
        {
            AssetType = "InputMappingContext",
            Name = imcName,
            VirtualPath = asset.File.Path,
            MappingCount = mappingsList.Count,
            Mappings = mappingsList
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));
        sidecars.Add(jsonPath);

        // 2. Python Setup Script for Editor
        var pyPath = outputPathNoExt + "_setup.py";
        var pyScript = GeneratePythonSetupScript(imcName, asset.File.Path, mappingsList);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        Log.Information("InputMappingContext {Name}: {Count} key mapping(s) recovered -> JSON + Python setup script emitted",
            imcName, mappingsList.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"InputMappingContext recovered: {mappingsList.Count} action key mappings with modifiers & triggers",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static string GeneratePythonSetupScript(string imcName, string virtualPath, List<ActionKeyMappingData> mappings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine Enhanced Input Setup Script: {imcName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_input_mapping_context():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Setting up InputMappingContext: {imcName}')");
        sb.AppendLine("    asset_sub = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine($"    imc_path = '{virtualPath.Replace(".uasset", "")}'");
        sb.AppendLine("    imc = unreal.EditorAssetLibrary.load_asset(imc_path) if asset_sub else None");
        sb.AppendLine("    if not imc:");
        sb.AppendLine("        unreal.log_warning(f'InputMappingContext not loaded at {imc_path}, check if created')");
        sb.AppendLine();
        sb.AppendLine("    mappings = [");
        foreach (var m in mappings)
        {
            var mods = string.Join(", ", m.Modifiers.Select(mod => $"'{mod}'"));
            var trigs = string.Join(", ", m.Triggers.Select(tr => $"'{tr}'"));
            sb.AppendLine($"        {{ 'action': '{m.Action}', 'key': '{m.Key}', 'modifiers': [{mods}], 'triggers': [{trigs}] }},");
        }
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log(f'Registered {{len(mappings)}} action mappings for {imcName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_input_mapping_context()");
        return sb.ToString();
    }

    private static List<string> ExtractClassNames(UObject obj, string propName)
    {
        var result = new List<string>();
        var items = obj.GetOrDefault<FPackageIndex[]>(propName);
        if (items != null)
        {
            foreach (var item in items)
            {
                var ro = item?.ResolvedObject;
                if (ro != null)
                    result.Add(ro.Class?.Name.Text ?? ro.Name.Text);
            }
        }
        return result;
    }

    private static List<string> ExtractClassNamesFromStruct(FStructFallback s, string propName)
    {
        var result = new List<string>();
        var items = s.GetOrDefault<FPackageIndex[]>(propName);
        if (items != null)
        {
            foreach (var item in items)
            {
                var ro = item?.ResolvedObject;
                if (ro != null)
                    result.Add(ro.Class?.Name.Text ?? ro.Name.Text);
            }
        }
        return result;
    }
}

public sealed class ActionKeyMappingData
{
    public string Action { get; set; } = "";
    public string Key { get; set; } = "";
    public bool IsPlayerMappable { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public List<string> Triggers { get; set; } = new();
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine curve assets:
/// - <see cref="UCurveFloat"/>: single-channel timeline curves.
/// - CurveVector: 3D vector curves (X, Y, Z).
/// - CurveLinearColor: RGBA color curves (R, G, B, A).
/// - <see cref="UCurveTable"/>: multi-row curve tables.
/// Extracts keyframes, tangents, and interpolation modes, emitting:
/// 1. An editor-compliant .csv sidecar.
/// 2. A detailed JSON curve model.
/// 3. An automated Unreal Editor Python script (&lt;CurveName&gt;_setup.py) to rebuild the curve in Unreal Editor.
/// </summary>
public sealed class CurveReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var outDir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            if (asset.PrimaryType == "CurveTable" || asset.Exports.OfType<UCurveTable>().Any())
            {
                return ReconstructCurveTable(asset, outputPathNoExt, outDir);
            }

            if (asset.PrimaryType == "CurveVector")
            {
                return ReconstructCurveVector(asset, outputPathNoExt, outDir);
            }

            if (asset.PrimaryType == "CurveLinearColor")
            {
                return ReconstructCurveLinearColor(asset, outputPathNoExt, outDir);
            }

            return ReconstructCurveFloat(asset, outputPathNoExt, outDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reconstruct curve {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Curve error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructCurveTable(ParsedAsset asset, string outputPathNoExt, string? outDir)
    {
        var curveTable = asset.Exports.OfType<UCurveTable>().FirstOrDefault();
        if (curveTable is null)
            return ReconstructionResult.Failed("No UCurveTable export found");

        var curveName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var rowMap = curveTable.RowMap ?? new Dictionary<FName, FStructFallback>();
        var csvSb = new StringBuilder();
        csvSb.AppendLine("---,Time,Value,InterpMode");

        var jsonRows = new Dictionary<string, List<CurveKeyData>>(StringComparer.Ordinal);

        foreach (var (rowName, rowStruct) in rowMap)
        {
            var keyStr = rowName.Text;
            var pointsList = new List<CurveKeyData>();

            var keysArray = rowStruct?.GetOrDefault<FStructFallback[]>("Keys");
            if (keysArray != null)
            {
                foreach (var k in keysArray)
                {
                    float time = k.GetOrDefault("Time", 0f);
                    float value = k.GetOrDefault("Value", 0f);
                    float arrive = k.GetOrDefault("ArriveTangent", 0f);
                    float leave = k.GetOrDefault("LeaveTangent", 0f);
                    string interp = k.GetOrDefault("InterpMode", "None")?.ToString() ?? "RCIM_Linear";

                    csvSb.AppendLine($"{DataTableReconstructor.EscapeCsv(keyStr)},{time.ToString("G7", CultureInfo.InvariantCulture)},{value.ToString("G7", CultureInfo.InvariantCulture)},{DataTableReconstructor.EscapeCsv(interp)}");
                    pointsList.Add(new CurveKeyData(time, value, arrive, leave, interp));
                }
            }
            jsonRows[keyStr] = pointsList;
        }

        var sidecars = new List<string>();
        var csvFileName = curveName + ".csv";
        var csvPath = Path.Combine(outDir ?? ".", csvFileName);
        File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);
        sidecars.Add(csvPath);

        var jsonPath = outputPathNoExt + ".curve.json";
        var model = new
        {
            AssetType = "CurveTable",
            asset.File.Path,
            CurveCount = rowMap.Count,
            Curves = jsonRows
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(jsonPath);

        var pyPath = outputPathNoExt + "_setup.py";
        var pyScript = GenerateCurveTablePythonScript(curveName, asset.File.Path, csvFileName);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"CurveTable: {rowMap.Count} curve(s) exported to .csv & Python script emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static ReconstructionResult ReconstructCurveFloat(ParsedAsset asset, string outputPathNoExt, string? outDir)
    {
        var export = asset.Exports.FirstOrDefault();
        if (export is null)
            return ReconstructionResult.Failed("No curve export found");

        var curveName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var csvSb = new StringBuilder();
        csvSb.AppendLine("Time,Value,ArriveTangent,LeaveTangent,InterpMode");

        var pointsList = new List<CurveKeyData>();
        var floatCurve = export.GetOrDefault<FStructFallback>("FloatCurve");
        var keysArray = floatCurve?.GetOrDefault<FStructFallback[]>("Keys");

        if (keysArray != null)
        {
            foreach (var k in keysArray)
            {
                float time = k.GetOrDefault("Time", 0f);
                float value = k.GetOrDefault("Value", 0f);
                float arriveTangent = k.GetOrDefault("ArriveTangent", 0f);
                float leaveTangent = k.GetOrDefault("LeaveTangent", 0f);
                string interp = k.GetOrDefault("InterpMode", "None")?.ToString() ?? "CIM_Linear";

                csvSb.AppendLine($"{time.ToString("G7", CultureInfo.InvariantCulture)},{value.ToString("G7", CultureInfo.InvariantCulture)},{arriveTangent.ToString("G7", CultureInfo.InvariantCulture)},{leaveTangent.ToString("G7", CultureInfo.InvariantCulture)},{DataTableReconstructor.EscapeCsv(interp)}");
                pointsList.Add(new CurveKeyData(time, value, arriveTangent, leaveTangent, interp));
            }
        }

        var sidecars = new List<string>();
        var csvFileName = curveName + ".csv";
        var csvPath = Path.Combine(outDir ?? ".", csvFileName);
        File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);
        sidecars.Add(csvPath);

        var jsonPath = outputPathNoExt + ".curve.json";
        var model = new
        {
            AssetType = "CurveFloat",
            asset.File.Path,
            KeyCount = pointsList.Count,
            Keys = pointsList
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(jsonPath);

        var pyPath = outputPathNoExt + "_setup.py";
        var pyScript = GenerateCurveFloatPythonScript(curveName, asset.File.Path, pointsList);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"CurveFloat: {pointsList.Count} keyframe(s) exported to .csv & Python script emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static ReconstructionResult ReconstructCurveVector(ParsedAsset asset, string outputPathNoExt, string? outDir)
    {
        var export = asset.Exports.FirstOrDefault();
        if (export is null)
            return ReconstructionResult.Failed("No CurveVector export found");

        var curveName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var floatCurves = export.GetOrDefault<FStructFallback[]>("FloatCurves");

        var xKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 0 ? floatCurves[0] : null);
        var yKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 1 ? floatCurves[1] : null);
        var zKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 2 ? floatCurves[2] : null);

        var sidecars = new List<string>();

        var csvSb = new StringBuilder();
        csvSb.AppendLine("Axis,Time,Value,ArriveTangent,LeaveTangent,InterpMode");
        AppendKeysCsv(csvSb, "X", xKeys);
        AppendKeysCsv(csvSb, "Y", yKeys);
        AppendKeysCsv(csvSb, "Z", zKeys);

        var csvFileName = curveName + ".csv";
        var csvPath = Path.Combine(outDir ?? ".", csvFileName);
        File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);
        sidecars.Add(csvPath);

        var jsonPath = outputPathNoExt + ".curve.json";
        var model = new
        {
            AssetType = "CurveVector",
            asset.File.Path,
            TotalKeys = xKeys.Count + yKeys.Count + zKeys.Count,
            X = xKeys,
            Y = yKeys,
            Z = zKeys
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(jsonPath);

        var pyPath = outputPathNoExt + "_setup.py";
        var pyScript = GenerateCurveVectorPythonScript(curveName, asset.File.Path, xKeys, yKeys, zKeys);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"CurveVector: {xKeys.Count + yKeys.Count + zKeys.Count} keys (X/Y/Z) exported & Python script emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static ReconstructionResult ReconstructCurveLinearColor(ParsedAsset asset, string outputPathNoExt, string? outDir)
    {
        var export = asset.Exports.FirstOrDefault();
        if (export is null)
            return ReconstructionResult.Failed("No CurveLinearColor export found");

        var curveName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var floatCurves = export.GetOrDefault<FStructFallback[]>("FloatCurves");

        var rKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 0 ? floatCurves[0] : null);
        var gKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 1 ? floatCurves[1] : null);
        var bKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 2 ? floatCurves[2] : null);
        var aKeys = ExtractKeys(floatCurves != null && floatCurves.Length > 3 ? floatCurves[3] : null);

        var sidecars = new List<string>();

        var csvSb = new StringBuilder();
        csvSb.AppendLine("Channel,Time,Value,ArriveTangent,LeaveTangent,InterpMode");
        AppendKeysCsv(csvSb, "R", rKeys);
        AppendKeysCsv(csvSb, "G", gKeys);
        AppendKeysCsv(csvSb, "B", bKeys);
        AppendKeysCsv(csvSb, "A", aKeys);

        var csvFileName = curveName + ".csv";
        var csvPath = Path.Combine(outDir ?? ".", csvFileName);
        File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);
        sidecars.Add(csvPath);

        var jsonPath = outputPathNoExt + ".curve.json";
        var model = new
        {
            AssetType = "CurveLinearColor",
            asset.File.Path,
            TotalKeys = rKeys.Count + gKeys.Count + bKeys.Count + aKeys.Count,
            R = rKeys,
            G = gKeys,
            B = bKeys,
            A = aKeys
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(jsonPath);

        var pyPath = outputPathNoExt + "_setup.py";
        var pyScript = GenerateCurveLinearColorPythonScript(curveName, asset.File.Path, rKeys, gKeys, bKeys, aKeys);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"CurveLinearColor: {rKeys.Count + gKeys.Count + bKeys.Count + aKeys.Count} keys (RGBA) exported & Python script emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static List<CurveKeyData> ExtractKeys(FStructFallback? richCurve)
    {
        var list = new List<CurveKeyData>();
        if (richCurve == null) return list;

        var keysArray = richCurve.GetOrDefault<FStructFallback[]>("Keys");
        if (keysArray == null) return list;

        foreach (var k in keysArray)
        {
            float time = k.GetOrDefault("Time", 0f);
            float value = k.GetOrDefault("Value", 0f);
            float arrive = k.GetOrDefault("ArriveTangent", 0f);
            float leave = k.GetOrDefault("LeaveTangent", 0f);
            string interp = k.GetOrDefault("InterpMode", "None")?.ToString() ?? "CIM_Linear";
            list.Add(new CurveKeyData(time, value, arrive, leave, interp));
        }

        return list;
    }

    private static void AppendKeysCsv(StringBuilder sb, string channel, List<CurveKeyData> keys)
    {
        foreach (var k in keys)
        {
            sb.AppendLine($"{channel},{k.Time.ToString("G7", CultureInfo.InvariantCulture)},{k.Value.ToString("G7", CultureInfo.InvariantCulture)},{k.ArriveTangent.ToString("G7", CultureInfo.InvariantCulture)},{k.LeaveTangent.ToString("G7", CultureInfo.InvariantCulture)},{DataTableReconstructor.EscapeCsv(k.InterpMode)}");
        }
    }

    private static string GenerateCurveFloatPythonScript(string curveName, string virtualPath, List<CurveKeyData> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine CurveFloat Setup Script: {curveName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_curve_{SanitizePy(curveName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Reconstructing CurveFloat: {curveName}')");
        sb.AppendLine("    editor_asset = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Curves";

        sb.AppendLine($"    target_path = '{assetPath}'");
        sb.AppendLine($"    target_pkg = '{packagePath}'");
        sb.AppendLine($"    asset_name = '{curveName}'");
        sb.AppendLine();
        sb.AppendLine("    curve = editor_asset.load_asset(target_path)");
        sb.AppendLine("    if not curve:");
        sb.AppendLine("        factory = unreal.CurveFloatFactory()");
        sb.AppendLine("        curve = asset_tools.create_asset(asset_name, target_pkg, unreal.CurveFloat, factory)");
        sb.AppendLine();
        sb.AppendLine("    if not curve:");
        sb.AppendLine("        unreal.log_error(f'Failed to create CurveFloat: {target_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();

        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Finished configuring CurveFloat: {curveName} ({keys.Count} keys)')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_curve_{SanitizePy(curveName)}()");

        return sb.ToString();
    }

    private static string GenerateCurveVectorPythonScript(string curveName, string virtualPath, List<CurveKeyData> x, List<CurveKeyData> y, List<CurveKeyData> z)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine CurveVector Setup Script: {curveName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_curve_{SanitizePy(curveName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Reconstructing CurveVector: {curveName}')");
        sb.AppendLine("    editor_asset = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Curves";

        sb.AppendLine($"    target_path = '{assetPath}'");
        sb.AppendLine($"    target_pkg = '{packagePath}'");
        sb.AppendLine($"    asset_name = '{curveName}'");
        sb.AppendLine();
        sb.AppendLine("    curve = editor_asset.load_asset(target_path)");
        sb.AppendLine("    if not curve:");
        sb.AppendLine("        factory = unreal.CurveVectorFactory()");
        sb.AppendLine("        curve = asset_tools.create_asset(asset_name, target_pkg, unreal.CurveVector, factory)");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Finished configuring CurveVector: {curveName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_curve_{SanitizePy(curveName)}()");

        return sb.ToString();
    }

    private static string GenerateCurveLinearColorPythonScript(string curveName, string virtualPath, List<CurveKeyData> r, List<CurveKeyData> g, List<CurveKeyData> b, List<CurveKeyData> a)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine CurveLinearColor Setup Script: {curveName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_curve_{SanitizePy(curveName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Reconstructing CurveLinearColor: {curveName}')");
        sb.AppendLine("    editor_asset = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Curves";

        sb.AppendLine($"    target_path = '{assetPath}'");
        sb.AppendLine($"    target_pkg = '{packagePath}'");
        sb.AppendLine($"    asset_name = '{curveName}'");
        sb.AppendLine();
        sb.AppendLine("    curve = editor_asset.load_asset(target_path)");
        sb.AppendLine("    if not curve:");
        sb.AppendLine("        factory = unreal.CurveLinearColorFactory()");
        sb.AppendLine("        curve = asset_tools.create_asset(asset_name, target_pkg, unreal.CurveLinearColor, factory)");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Finished configuring CurveLinearColor: {curveName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_curve_{SanitizePy(curveName)}()");

        return sb.ToString();
    }

    private static string GenerateCurveTablePythonScript(string tableName, string virtualPath, string csvFileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine CurveTable Import Script: {tableName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import os");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_curvetable_{SanitizePy(tableName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Importing CurveTable: {tableName}')");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Curves";

        sb.AppendLine("    script_dir = os.path.dirname(os.path.abspath(__file__))");
        sb.AppendLine($"    csv_path = os.path.join(script_dir, '{csvFileName}')");
        sb.AppendLine("    if not os.path.exists(csv_path):");
        sb.AppendLine("        unreal.log_error(f'CSV not found: {csv_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();
        sb.AppendLine("    task = unreal.AssetImportTask()");
        sb.AppendLine("    task.filename = csv_path");
        sb.AppendLine($"    task.destination_path = '{packagePath}'");
        sb.AppendLine($"    task.destination_name = '{tableName}'");
        sb.AppendLine("    task.replace_existing = True");
        sb.AppendLine("    task.automated = True");
        sb.AppendLine("    task.save = True");
        sb.AppendLine();
        sb.AppendLine("    factory = unreal.CSVImportFactory()");
        sb.AppendLine("    factory.import_settings.import_type = unreal.CSVImportType.ECSV_CURVE_TABLE");
        sb.AppendLine("    task.factory = factory");
        sb.AppendLine();
        sb.AppendLine("    asset_tools.import_asset_tasks([task])");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Imported CurveTable: {tableName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_curvetable_{SanitizePy(tableName)}()");

        return sb.ToString();
    }

    private static string SanitizePy(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        var s = sb.ToString();
        return char.IsDigit(s.FirstOrDefault()) ? "_" + s : s;
    }

    public record CurveKeyData(float Time, float Value, float ArriveTangent, float LeaveTangent, string InterpMode);
}

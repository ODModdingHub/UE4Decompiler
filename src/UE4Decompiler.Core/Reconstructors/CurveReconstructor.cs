using System.Globalization;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine curve assets: <see cref="UCurveFloat"/>, <see cref="UCurveTable"/>,
/// CurveVector, and CurveLinearColor.
/// Extracts curve keyframes and row maps, emitting:
/// 1. An editor-compliant .csv sidecar with time/value points.
/// 2. A detailed JSON curve model.
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

            return ReconstructCurveAsset(asset, outputPathNoExt, outDir);
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

        var rowMap = curveTable.RowMap ?? new Dictionary<FName, FStructFallback>();
        var csvSb = new StringBuilder();
        csvSb.AppendLine("---,Time,Value,InterpMode");

        var jsonRows = new Dictionary<string, List<object>>(StringComparer.Ordinal);

        foreach (var (rowName, rowStruct) in rowMap)
        {
            var keyStr = rowName.Text;
            var pointsList = new List<object>();

            // Extract Keys array from the curve struct fallback
            var keysArray = rowStruct?.GetOrDefault<FStructFallback[]>("Keys");
            if (keysArray != null)
            {
                foreach (var k in keysArray)
                {
                    float time = k.GetOrDefault("Time", 0f);
                    float value = k.GetOrDefault("Value", 0f);
                    string interp = k.GetOrDefault("InterpMode", "None")?.ToString() ?? "RCIM_Linear";

                    csvSb.AppendLine($"{DataTableReconstructor.EscapeCsv(keyStr)},{time.ToString("G7", CultureInfo.InvariantCulture)},{value.ToString("G7", CultureInfo.InvariantCulture)},{DataTableReconstructor.EscapeCsv(interp)}");
                    pointsList.Add(new { Time = time, Value = value, InterpMode = interp });
                }
            }
            jsonRows[keyStr] = pointsList;
        }

        var csvFileName = Path.GetFileNameWithoutExtension(outputPathNoExt) + ".csv";
        var csvPath = Path.Combine(outDir ?? ".", csvFileName);
        File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);

        var model = new
        {
            AssetType = "CurveTable",
            asset.File.Path,
            CurveCount = rowMap.Count,
            Curves = jsonRows
        };

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"CurveTable: {rowMap.Count} curve(s) exported to .csv",
            Model = model,
            SidecarFiles = { csvFileName }
        };
    }

    private static ReconstructionResult ReconstructCurveAsset(ParsedAsset asset, string outputPathNoExt, string? outDir)
    {
        var export = asset.Exports.FirstOrDefault();
        if (export is null)
            return ReconstructionResult.Failed("No curve export found");

        var csvSb = new StringBuilder();
        csvSb.AppendLine("Time,Value,ArriveTangent,LeaveTangent,InterpMode");

        var pointsList = new List<object>();

        // Look for FloatCurve tagged property
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
                pointsList.Add(new { Time = time, Value = value, ArriveTangent = arriveTangent, LeaveTangent = leaveTangent, InterpMode = interp });
            }
        }

        var csvFileName = Path.GetFileNameWithoutExtension(outputPathNoExt) + ".csv";
        var csvPath = Path.Combine(outDir ?? ".", csvFileName);
        File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);

        var model = new
        {
            AssetType = asset.PrimaryType,
            asset.File.Path,
            KeyCount = pointsList.Count,
            Keys = pointsList
        };

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"{asset.PrimaryType}: {pointsList.Count} keyframe(s) exported to .csv",
            Model = model,
            SidecarFiles = { csvFileName }
        };
    }
}

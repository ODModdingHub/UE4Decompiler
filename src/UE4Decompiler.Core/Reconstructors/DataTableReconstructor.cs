using System.Globalization;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs <see cref="UDataTable"/> assets across both legacy UE4 packages and UE5 IoStore packages.
/// Recovers the row schema, extracts all row data from RowMap, and outputs:
/// 1. An Unreal Engine editor-compliant .csv sidecar (importable via Editor or automation).
/// 2. A structured JSON model with typed property representations.
/// 3. Row struct metadata for C++ stub generation (FTableRowBase).
/// </summary>
public sealed class DataTableReconstructor
{
    public sealed record RowStructMetadata(string StructName, Dictionary<string, string> Fields);

    /// <summary>Discovered row structs during reconstruction, keyed by struct name.</summary>
    public Dictionary<string, RowStructMetadata> RecoveredRowStructs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var table = asset.Exports.OfType<UDataTable>().FirstOrDefault();
            if (table is null)
                return ReconstructionResult.Failed("No UDataTable export found");

            var rowStructName = ResolveRowStructName(table);
            var rowMap = table.RowMap ?? new Dictionary<FName, FStructFallback>();

            var outDir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // Collect all unique column names and their inferred types across all rows
            var columns = new List<string>();
            var columnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columnTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in rowMap)
            {
                var row = kvp.Value;
                if (row?.Properties == null) continue;

                foreach (var prop in row.Properties)
                {
                    var propName = prop.Name.Text;
                    if (columnSet.Add(propName))
                    {
                        columns.Add(propName);
                    }

                    if (!columnTypes.ContainsKey(propName))
                    {
                        columnTypes[propName] = prop.PropertyType.Text;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(rowStructName) && columns.Count > 0)
            {
                RecoveredRowStructs[rowStructName] = new RowStructMetadata(rowStructName, new Dictionary<string, string>(columnTypes));
            }

            // Generate Unreal Engine editor-compatible CSV
            var csvSb = new StringBuilder();
            // Header row: Unreal Engine expects '---' or 'Name' as the first column for RowName
            csvSb.Append("---");
            foreach (var col in columns)
            {
                csvSb.Append(',');
                csvSb.Append(EscapeCsv(col));
            }
            csvSb.AppendLine();

            var jsonRows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

            foreach (var (rowName, rowData) in rowMap)
            {
                var keyStr = rowName.Text;
                csvSb.Append(EscapeCsv(keyStr));

                var rowDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                var rowProps = rowData?.Properties?.ToDictionary(p => p.Name.Text, p => p, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, FPropertyTag>(StringComparer.OrdinalIgnoreCase);

                foreach (var col in columns)
                {
                    csvSb.Append(',');
                    if (rowProps.TryGetValue(col, out var tag))
                    {
                        csvSb.Append(FormatCsvProperty(tag));
                        rowDict[col] = ExtractJsonValue(tag);
                    }
                    else
                    {
                        rowDict[col] = null;
                    }
                }
                csvSb.AppendLine();
                jsonRows[keyStr] = rowDict;
            }

            var csvFileName = Path.GetFileNameWithoutExtension(outputPathNoExt) + ".csv";
            var csvPath = Path.Combine(outDir ?? ".", csvFileName);
            File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);

            var model = new
            {
                AssetType = "DataTable",
                asset.File.Path,
                RowStruct = rowStructName,
                RowCount = rowMap.Count,
                ColumnCount = columns.Count,
                Columns = columns,
                ColumnTypes = columnTypes,
                Rows = jsonRows
            };

            var sidecars = new List<string> { csvFileName };

            Log.Information("DataTable {Name}: {Rows} row(s), {Cols} col(s), struct: {Struct} -> {Csv}",
                Path.GetFileName(outputPathNoExt), rowMap.Count, columns.Count, rowStructName, csvFileName);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"DataTable: {rowMap.Count} row(s) ({columns.Count} col(s), struct: {rowStructName})",
                Model = model,
                SidecarFiles = { csvFileName }
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reconstruct DataTable {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"DataTable error: {ex.Message}");
        }
    }

    private static string ResolveRowStructName(UDataTable table)
    {
        if (!string.IsNullOrWhiteSpace(table.RowStructName))
            return table.RowStructName;

        var structTag = table.GetOrDefault<FPackageIndex>("RowStruct")?.ResolvedObject;
        if (structTag != null && !string.IsNullOrWhiteSpace(structTag.Name.Text))
            return structTag.Name.Text;

        return "TableRowBase";
    }

    public static string EscapeCsv(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        bool mustQuote = val.Contains(',') || val.Contains('"') || val.Contains('\n') || val.Contains('\r') || val.StartsWith(' ') || val.EndsWith(' ');
        if (!mustQuote) return val;
        return "\"" + val.Replace("\"", "\"\"") + "\"";
    }

    private static string FormatCsvProperty(FPropertyTag tag)
    {
        if (tag.Tag == null) return "";
        return FormatTagValueForCsv(tag.Tag);
    }

    private static string FormatTagValueForCsv(FPropertyTagType tag)
    {
        object? gv = null;
        try { gv = tag.GenericValue; } catch { }

        if (gv is null) return "";

        switch (gv)
        {
            case bool b:
                return b ? "True" : "False";
            case byte by:
                return by.ToString(CultureInfo.InvariantCulture);
            case sbyte sby:
                return sby.ToString(CultureInfo.InvariantCulture);
            case short s:
                return s.ToString(CultureInfo.InvariantCulture);
            case ushort us:
                return us.ToString(CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case uint ui:
                return ui.ToString(CultureInfo.InvariantCulture);
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case ulong ul:
                return ul.ToString(CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("G7", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("G15", CultureInfo.InvariantCulture);
            case string str:
                return EscapeCsv(str);
            case FName fn:
                return EscapeCsv(fn.Text);
            case FText txt:
                return EscapeCsv(txt.Text ?? "");
            case FSoftObjectPath sop:
                return EscapeCsv(sop.AssetPathName.Text);
            case FPackageIndex pi:
                return EscapeCsv(pi.ResolvedObject?.GetPathName() ?? "");
            case FVector vec:
                return EscapeCsv($"(X={vec.X.ToString("F3", CultureInfo.InvariantCulture)},Y={vec.Y.ToString("F3", CultureInfo.InvariantCulture)},Z={vec.Z.ToString("F3", CultureInfo.InvariantCulture)})");
            case FVector2D vec2:
                return EscapeCsv($"(X={vec2.X.ToString("F3", CultureInfo.InvariantCulture)},Y={vec2.Y.ToString("F3", CultureInfo.InvariantCulture)})");
            case FRotator rot:
                return EscapeCsv($"(Pitch={rot.Pitch.ToString("F3", CultureInfo.InvariantCulture)},Yaw={rot.Yaw.ToString("F3", CultureInfo.InvariantCulture)},Roll={rot.Roll.ToString("F3", CultureInfo.InvariantCulture)})");
            case FLinearColor lc:
                return EscapeCsv($"(R={lc.R.ToString("F3", CultureInfo.InvariantCulture)},G={lc.G.ToString("F3", CultureInfo.InvariantCulture)},B={lc.B.ToString("F3", CultureInfo.InvariantCulture)},A={lc.A.ToString("F3", CultureInfo.InvariantCulture)})");
            case FColor c:
                return EscapeCsv($"(R={c.R},G={c.G},B={c.B},A={c.A})");
            case FGuid guid:
                return EscapeCsv(guid.ToString());
            case FScriptStruct ss when ss.StructType is FStructFallback sf:
                return EscapeCsv(FormatStructFallback(sf));
            case FStructFallback sf:
                return EscapeCsv(FormatStructFallback(sf));
            case UScriptArray arr:
                return EscapeCsv(FormatArray(arr));
            default:
                return EscapeCsv(tag.ToString() ?? "");
        }
    }

    private static string FormatStructFallback(FStructFallback sf)
    {
        var sb = new StringBuilder("(");
        bool first = true;
        foreach (var p in sf.Properties)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(p.Name.Text).Append('=').Append(FormatTagValueForCsv(p.Tag));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string FormatArray(UScriptArray arr)
    {
        var sb = new StringBuilder("(");
        for (int i = 0; i < arr.Properties.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(FormatTagValueForCsv(arr.Properties[i]));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static object? ExtractJsonValue(FPropertyTag tag)
    {
        if (tag.Tag == null) return null;
        object? gv = null;
        try { gv = tag.Tag.GenericValue; } catch { }
        if (gv is null) return null;

        return gv switch
        {
            bool b => b,
            byte by => by,
            sbyte sby => sby,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => ul,
            float f => f,
            double d => d,
            string str => str,
            FName fn => fn.Text,
            FText txt => txt.Text,
            FSoftObjectPath sop => sop.AssetPathName.Text,
            FPackageIndex pi => pi.ResolvedObject?.GetPathName(),
            FVector vec => new { vec.X, vec.Y, vec.Z },
            FVector2D vec2 => new { vec2.X, vec2.Y },
            FRotator rot => new { rot.Pitch, rot.Yaw, rot.Roll },
            FLinearColor lc => new { lc.R, lc.G, lc.B, lc.A },
            FColor c => new { c.R, c.G, c.B, c.A },
            FGuid g => g.ToString(),
            FScriptStruct ss when ss.StructType is FStructFallback sf => ExtractStructFallbackJson(sf),
            FStructFallback sf => ExtractStructFallbackJson(sf),
            UScriptArray arr => arr.Properties.Select(ExtractTagJson).ToList(),
            _ => gv.ToString()
        };
    }

    private static object? ExtractTagJson(FPropertyTagType tag)
    {
        object? gv = null;
        try { gv = tag.GenericValue; } catch { }
        if (gv is null) return null;
        if (gv is FName fn) return fn.Text;
        if (gv is FText txt) return txt.Text;
        if (gv is FStructFallback sf) return ExtractStructFallbackJson(sf);
        return gv;
    }

    private static Dictionary<string, object?> ExtractStructFallbackJson(FStructFallback sf)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in sf.Properties)
        {
            dict[p.Name.Text] = ExtractJsonValue(p);
        }
        return dict;
    }
}

using System.Text;
using CUE4Parse.UE4.Assets.Exports.Internationalization;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs <see cref="UStringTable"/> localization assets.
/// Extracts string table keys, localized strings, namespaces, and metadata, producing:
/// 1. An Unreal Engine editor-compliant .csv sidecar (Key,SourceString).
/// 2. A structured JSON model with complete namespace and metadata mapping.
/// </summary>
public sealed class StringTableReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var stringTableExport = asset.Exports.OfType<UStringTable>().FirstOrDefault();
            if (stringTableExport is null)
                return ReconstructionResult.Failed("No UStringTable export found");

            var st = stringTableExport.StringTable;
            var ns = st?.TableNamespace ?? "";
            var entries = st?.KeysToEntries ?? new Dictionary<string, string>();
            var metadata = st?.KeysToMetaData;

            var outDir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // Unreal Engine StringTable CSV format:
            // "Key","SourceString"
            var csvSb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(ns))
            {
                csvSb.AppendLine($"# StringTable: \"{ns}\"");
            }
            csvSb.AppendLine("\"Key\",\"SourceString\"");

            var jsonEntries = new Dictionary<string, string>(StringComparer.Ordinal);
            var jsonMeta = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            foreach (var (key, sourceStr) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                csvSb.Append(DataTableReconstructor.EscapeCsv(key));
                csvSb.Append(',');
                csvSb.Append(DataTableReconstructor.EscapeCsv(sourceStr));
                csvSb.AppendLine();

                jsonEntries[key] = sourceStr;
            }

            if (metadata != null)
            {
                foreach (var (k, metaDict) in metadata)
                {
                    var md = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (metaKey, metaVal) in metaDict)
                    {
                        md[metaKey.Text] = metaVal;
                    }
                    jsonMeta[k] = md;
                }
            }

            var csvFileName = Path.GetFileNameWithoutExtension(outputPathNoExt) + ".csv";
            var csvPath = Path.Combine(outDir ?? ".", csvFileName);
            File.WriteAllText(csvPath, csvSb.ToString(), Encoding.UTF8);

            var model = new
            {
                AssetType = "StringTable",
                asset.File.Path,
                TableNamespace = ns,
                EntryCount = entries.Count,
                Entries = jsonEntries,
                Metadata = jsonMeta
            };

            Log.Information("StringTable {Name}: {Count} entries, namespace: '{Ns}' -> {Csv}",
                Path.GetFileName(outputPathNoExt), entries.Count, ns, csvFileName);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"StringTable: {entries.Count} entry(ies), namespace: '{ns}'",
                Model = model,
                SidecarFiles = { csvFileName }
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reconstruct StringTable {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"StringTable error: {ex.Message}");
        }
    }
}

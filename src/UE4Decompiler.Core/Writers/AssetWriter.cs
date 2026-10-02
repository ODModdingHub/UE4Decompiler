using System.Text;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace UE4Decompiler.Core;

/// <summary>
/// Writes recovered assets to disk in two complementary forms:
///
///  1. A JSON model sidecar (<c>.json</c>) — the full, faithful export/property graph as
///     CUE4Parse deserializes it. This is the source of truth the reconstructors enrich and
///     what a re-import tool / editor plugin would consume.
///
///  2. A binary <c>.uasset</c> with a structurally-valid <c>FPackageFileSummary</c> header
///     (magic tag + version + empty name/import/export tables). This makes the Content tree
///     enumerable without missing-file crashes.
///
/// NOTE: Producing a byte-perfect *uncooked* package that the editor re-cooks losslessly is a
/// research-grade problem and is explicitly out of scope here (CUE4Parse is read-only). The JSON
/// model carries the recovered data; the binary header is a placeholder, not an editor-loadable asset.
/// </summary>
public sealed class AssetWriter
{
    private const uint PackageFileTag = 0x9E2A83C1; // PACKAGE_FILE_TAG

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        MaxDepth = 256
    };

    /// <summary>Serialize an arbitrary recovered model (exports list, reconstructor output, etc.) as JSON.</summary>
    public void WriteJsonModel(string outputUassetPath, object model)
    {
        var jsonPath = Path.ChangeExtension(outputUassetPath, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        var json = JsonConvert.SerializeObject(model, JsonSettings);
        File.WriteAllText(jsonPath, json);
    }

    /// <summary>
    /// Write a minimal but structurally-valid uncooked .uasset header. <paramref name="packageName"/>
    /// is the /Game-rooted package path used to derive the folder-name field.
    /// </summary>
    public void WriteUAssetHeader(string outputUassetPath, EGame game, string packageName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputUassetPath)!);
        using var fs = File.Create(outputUassetPath);
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        var ver = game.GetVersion();

        w.Write(PackageFileTag);                 // Tag
        w.Write(-8);                             // LegacyFileVersion (modern packages use -8/-7)
        w.Write(-1);                             // LegacyUE3Version (unused)
        w.Write(ver.FileVersionUE4);             // FileVersionUE4
        if (ver.FileVersionUE5 > 0) w.Write(ver.FileVersionUE5); // FileVersionUE5 (present for -8)
        w.Write(0);                              // FileVersionLicenseeUE4
        w.Write(0);                              // CustomVersions count (CustomVersionContainer)

        var headerSizePos = fs.Position;
        w.Write(0);                              // TotalHeaderSize (patched below)
        WriteFString(w, packageName);            // FolderName
        w.Write((uint)0x00000000);               // PackageFlags (uncooked: no FilterEditorOnly/Cooked)

        w.Write(0); w.Write(0);                  // NameCount, NameOffset
        WriteFString(w, string.Empty);           // LocalizationId
        w.Write(0); w.Write(0);                  // GatherableTextDataCount, Offset
        w.Write(0); w.Write(0);                  // ExportCount, ExportOffset
        w.Write(0); w.Write(0);                  // ImportCount, ImportOffset
        w.Write(0);                              // DependsOffset
        w.Write(0); w.Write(0);                  // SoftPackageReferencesCount, Offset
        w.Write(0);                              // SearchableNamesOffset
        w.Write(0);                              // ThumbnailTableOffset
        WriteGuid(w);                            // Guid (random)
        w.Write(0);                              // Generations count
        WriteEngineVersion(w);                   // SavedByEngineVersion
        WriteEngineVersion(w);                   // CompatibleWithEngineVersion
        w.Write((uint)0);                        // CompressionFlags
        w.Write((uint)0);                        // PackageSource
        w.Write(0);                              // NumAdditionalPackagesToCook (TArray<FString> count)
        w.Write(-1);                             // AssetRegistryDataOffset (none)
        w.Write((long)0);                        // BulkDataStartOffset
        w.Write(0);                              // WorldTileInfoDataOffset
        w.Write(0);                              // ChunkIDs count
        w.Write(0); w.Write(-1);                 // PreloadDependencyCount, Offset

        var total = (int)fs.Position;
        w.Flush();
        fs.Seek(headerSizePos, SeekOrigin.Begin);
        w.Write(total);                          // patch TotalHeaderSize
        w.Flush();
    }

    /// <summary>Convenience: write both JSON model and binary header for one asset.</summary>
    public void Write(string outputUassetPath, EGame game, string packageName, object model)
    {
        WriteJsonModel(outputUassetPath, model);
        try
        {
            WriteUAssetHeader(outputUassetPath, game, packageName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not write binary header for {Path}; JSON model still written", outputUassetPath);
        }
    }

    private static void WriteFString(BinaryWriter w, string s)
    {
        if (string.IsNullOrEmpty(s)) { w.Write(0); return; }
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write(bytes.Length + 1);   // positive length => ANSI, includes null terminator
        w.Write(bytes);
        w.Write((byte)0);
    }

    private static void WriteGuid(BinaryWriter w)
    {
        var g = Guid.NewGuid().ToByteArray();
        for (var i = 0; i < 16; i += 4) // 4x uint32, matching FGuid layout
            w.Write(BitConverter.ToUInt32(g, i));
    }

    private static void WriteEngineVersion(BinaryWriter w)
    {
        w.Write((ushort)0); // Major
        w.Write((ushort)0); // Minor
        w.Write((ushort)0); // Patch
        w.Write((uint)0);   // Changelist
        WriteFString(w, string.Empty); // Branch
    }
}

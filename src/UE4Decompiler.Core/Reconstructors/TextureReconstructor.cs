using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Decompresses the largest available mip of a <see cref="UTexture2D"/> (BC1-7 / ASTC / etc.,
/// handled by CUE4Parse's decoder) and writes it as an uncompressed source PNG so Unreal can
/// re-import + re-cook it. Preserves SRGB / compression / filter settings on the recovered model.
/// </summary>
public sealed class TextureReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        var texture = asset.Exports.OfType<UTexture2D>().FirstOrDefault();
        if (texture is null)
            return ReconstructionResult.Failed("No UTexture2D export found");

        return ExportOne(texture, Path.GetDirectoryName(outputPathNoExt)!,
            Path.GetFileNameWithoutExtension(outputPathNoExt));
    }

    /// <summary>Export one texture to dir/name.png + dir/name.json (settings sidecar for the re-import
    /// + texfix passes). Skips work when both files already exist (incremental dumps stay fast).</summary>
    public static ReconstructionResult ExportOne(UTexture2D texture, string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var pngPath = Path.Combine(dir, name + ".png");
        var jsonPath = Path.Combine(dir, name + ".json");
        if (File.Exists(pngPath) && File.Exists(jsonPath) && new FileInfo(jsonPath).Length > 0)
        {
            // Return the existing sidecar as the model so the caller (Newtonsoft-based WriteJsonModel)
            // doesn't overwrite good metadata with a stub (incremental dumps must not clobber
            // SRGB/compression settings). Stub sidecars (from before this guard) fall through to full
            // re-extraction below.
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(jsonPath));
                if (jo["SRGB"] != null && jo["CompressionSettings"] != null)
                {
                    var skipped = new ReconstructionResult { Fidelity = Fidelity.Full, Model = jo };
                    skipped.SidecarFiles.Add(Path.GetFileName(pngPath));
                    return skipped;
                }
            }
            catch { }
        }
        try
        {
            var decoded = TextureDecoder.Decode(texture, ETexturePlatform.DesktopMobile); // largest valid mip
            if (decoded is null)
                return new ReconstructionResult { Fidelity = Fidelity.Stub, Note = "Mip data missing/streamed-out; settings preserved" };

            using var bitmap = TextureEncoder.ToSkBitmap(decoded);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            using (var stream = File.Create(pngPath))
                data.SaveTo(stream);

            var model = new
            {
                AssetType = "Texture2D",
                Source = Path.GetFileName(pngPath),
                bitmap.Width,
                bitmap.Height,
                texture.SRGB,
                CompressionSettings = texture.CompressionSettings.ToString(),
                Filter = texture.Filter.ToString(),
                texture.IsNormalMap,
                IsHDR = texture.IsHDR,
                // NOTE: texture.Properties is deliberately excluded — CUE4Parse property graphs are not
                // System.Text.Json-serializable (throws, leaving a 0KB sidecar that breaks the import filter).
            };

            Log.Information("Texture {Name}: wrote {W}x{H} PNG  [fmt={Fmt} srgb={SRGB} comp={Comp} normal={NM} colortype={CT}]",
                texture.Name, bitmap.Width, bitmap.Height, texture.Format, texture.SRGB, texture.CompressionSettings, texture.IsNormalMap, bitmap.ColorType);
            File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(model,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var result = new ReconstructionResult { Fidelity = Fidelity.Full, Model = model };
            result.SidecarFiles.Add(Path.GetFileName(pngPath));
            return result;
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Texture decode error: {ex.Message}");
        }
    }
}

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

        try
        {
            var decoded = texture.Decode(ETexturePlatform.DesktopMobile); // largest valid mip
            if (decoded is null)
                return new ReconstructionResult { Fidelity = Fidelity.Stub, Note = "Mip data missing/streamed-out; settings preserved" };

            using var bitmap = decoded.ToSkBitmap();
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var pngPath = outputPathNoExt + ".png";
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
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
                Properties = texture.Properties // preserve raw property tags
            };

            Log.Information("Texture {Name}: wrote {W}x{H} PNG  [fmt={Fmt} srgb={SRGB} comp={Comp} normal={NM} colortype={CT}]",
                texture.Name, bitmap.Width, bitmap.Height, texture.Format, texture.SRGB, texture.CompressionSettings, texture.IsNormalMap, bitmap.ColorType);
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

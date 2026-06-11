using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Builds an editor-loadable UTexture2D by decoding the cooked platform mip to source PNG art.
/// Keep this on the legacy FByteBulkData path for now: UE5 FEditorBulkData also needs package
/// bulk metadata/trailer records, and emitting only its object header trips UE5's BulkMeta assert.
/// </summary>
public static class TextureWriter
{
    public static int MaxDim = 1024;

    public static bool WriteEditorTexture(UTexture2D tex, string outFile, string targetShort, string targetPackagePath)
    {
        byte[] png; int w, h;
        try
        {
            var decoded = tex.Decode(MaxDim, ETexturePlatform.DesktopMobile) ?? tex.Decode(ETexturePlatform.DesktopMobile);
            if (decoded is null) { Log.Warning("Texture {N}: no decodable mip", targetShort); return false; }
            using var raw = decoded.ToSkBitmap();
            w = raw.Width; h = raw.Height;
            using var bmp = (raw.ColorType == SKColorType.Rgba8888 && raw.AlphaType == SKAlphaType.Unpremul)
                ? raw : raw.Copy(SKColorType.Rgba8888);

            var px = bmp.Bytes;
            if (LooksBlackOrEmpty(px))
            {
                Log.Warning("Texture {N}: decoded mip is all black/empty ({W}x{H}, format={Format}); source bulk may be missing or misread",
                    targetShort, w, h, tex.Format);
            }

            using var img = SKImage.FromBitmap(bmp);
            using var d = img.Encode(SKEncodedImageFormat.Png, 100);
            png = d.ToArray();
        }
        catch (Exception ex) { Log.Warning(ex, "Texture {N}: decode failed", targetShort); return false; }
        if (png.Length == 0 || w <= 0 || h <= 0) return false;

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int tex2dClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Texture2D");

        using var ms = new MemoryStream();
        using var aw = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(aw, spw.Name);

        t.Struct("Source", "TextureSource", () =>
        {
            var inner = new TaggedPropertyWriter(aw, spw.Name);
            inner.GuidStruct("Id", FGuid16.NewGuid());
            inner.Int("SizeX", w);
            inner.Int("SizeY", h);
            inner.Int("NumSlices", 1);
            inner.Int("NumMips", 1);
            inner.Bool("bPNGCompressed", true);
            inner.ByteEnum("Format", "ETextureSourceFormat", "TSF_BGRA8");
            inner.WriteNone();
        });
        t.Struct("ImportedSize", "IntPoint", () => { aw.Write(w); aw.Write(h); });
        t.GuidStruct("LightingGuid", FGuid16.NewGuid());
        t.Bool("NeverStream", true);
        if (!tex.SRGB) t.Bool("SRGB", false);
        var cs = tex.CompressionSettings.ToString();
        if (cs != "TC_Default") t.ByteEnum("CompressionSettings", "TextureCompressionSettings", cs);
        var lg = tex.LODGroup.ToString();
        if (lg != "TEXTUREGROUP_World") t.ByteEnum("LODGroup", "TextureGroup", lg);
        t.ByteEnum("MipGenSettings", "TextureMipGenSettings", "TMGS_NoMipmaps");
        t.WriteNone();

        aw.Write(0);                           // UObject bSerializeGuid
        aw.Write((byte)0); aw.Write((byte)0); // UTexture FStripDataFlags: editor data present
        aw.Write(0x40);                        // FByteBulkData flags = BULKDATA_ForceInlinePayload
        aw.Write(png.Length);
        aw.Write(png.Length);
        aw.Write((long)0);
        aw.WriteBytes(png);
        aw.Write((byte)0); aw.Write((byte)0); // UTexture2D FStripDataFlags
        aw.Write(0);                           // bCooked = false
        aw.Flush();

        spw.AddExport(targetShort, tex2dClass, 0, 0, ms.ToArray(), objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.Write(outFile);
        Log.Information("Editor texture {N} -> {Out} ({W}x{H}, {B}B PNG)", targetShort, outFile, w, h, png.Length);
        return true;
    }

    private static bool LooksBlackOrEmpty(byte[] rgba)
    {
        if (rgba.Length == 0) return true;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0 || rgba[i + 3] != 0)
                return false;
        }
        return true;
    }
}

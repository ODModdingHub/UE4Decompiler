using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Builds an editor-loadable UTexture2D (.uasset) from scratch for UE4.21 by decoding the cooked
/// platform mip (BC/ASTC/etc. via CUE4Parse) to a PNG and embedding it as the FTextureSource art.
///
/// 4.21 editor-texture layout (reversed from /Engine/EngineResources/Black.uasset):
///   tagged props:
///     Source (StructProperty "TextureSource"):
///       Id(StructProperty "Guid") SizeX(int) SizeY(int) NumSlices(int) NumMips(int)
///       bPNGCompressed(bool=true) Format(ByteProperty "ETextureSourceFormat" = "TSF_BGRA8") None
///     ImportedSize (StructProperty "IntPoint" = {X,Y}) None
///   FStripDataFlags(2 bytes, 0/0)         <- UTexture; editor data NOT stripped
///   FByteBulkData(source PNG)             <- flags=1 (at-end), count=size=pngLen, offset=0(rel)
///   FStripDataFlags(2 bytes, 0/0)         <- UTexture2D
///   int32 bCooked = 0
/// bulk region = the PNG bytes.
/// </summary>
public static class TextureWriter
{
    /// <summary>Max source dimension. Decoding a smaller mip (not the 4096² top) is dramatically faster and
    /// yields far smaller assets; the editor rebuilds the platform mip chain anyway. Override via --tex-max.</summary>
    public static int MaxDim = 1024;

    public static bool WriteEditorTexture(UTexture2D tex, string outFile, string targetShort, string targetPackagePath)
    {
        byte[] png; int w, h;
        try
        {
            // Pick the largest mip <= MaxDim (avoids decoding/encoding the huge top mip on 2k/4k textures).
            var decoded = tex.Decode(MaxDim, ETexturePlatform.DesktopMobile) ?? tex.Decode(ETexturePlatform.DesktopMobile);
            if (decoded is null) { Log.Warning("Texture {N}: no decodable mip", targetShort); return false; }
            using var raw = decoded.ToSkBitmap();
            w = raw.Width; h = raw.Height;
            // Normalize to canonical RGBA8888 (unpremultiplied) so the byte order is known regardless of decode.
            using var bmp = (raw.ColorType == SKColorType.Rgba8888 && raw.AlphaType == SKAlphaType.Unpremul)
                ? raw : raw.Copy(SKColorType.Rgba8888);
            // 4.21's only 8-bit source format is TSF_BGRA8, and the editor reads our PNG bytes AS BGRA. So
            // pre-swap R<->B: PNG then stores (B,G,R,A), and UE reading it as BGRA yields the correct color.
            var px = bmp.Bytes;                         // RGBA bytes (copy)
            for (int i = 0; i + 2 < px.Length; i += 4) { (px[i], px[i + 2]) = (px[i + 2], px[i]); }
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var img = SKImage.FromPixelCopy(info, px);
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

        // Source (FTextureSource) — nested tagged-property struct.
        t.Struct("Source", "TextureSource", () =>
        {
            var inner = new TaggedPropertyWriter(aw, spw.Name);
            var g = FGuid16.NewGuid();
            inner.GuidStruct("Id", g);
            inner.Int("SizeX", w);
            inner.Int("SizeY", h);
            inner.Int("NumSlices", 1);
            inner.Int("NumMips", 1);
            inner.Bool("bPNGCompressed", true);
            inner.ByteEnum("Format", "ETextureSourceFormat", "TSF_BGRA8");
            inner.WriteNone();
        });
        t.Struct("ImportedSize", "IntPoint", () => { aw.Write(w); aw.Write(h); });
        // Carry the source's render settings so normal maps / masks / data textures aren't treated as sRGB color
        // (the cause of "wrong colors"). Only emit non-defaults (default = SRGB true, TC_Default, World group).
        if (!tex.SRGB) t.Bool("SRGB", false);
        var cs = tex.CompressionSettings.ToString();
        if (cs != "TC_Default") t.ByteEnum("CompressionSettings", "TextureCompressionSettings", cs);
        var lg = tex.LODGroup.ToString();
        if (lg != "TEXTUREGROUP_World") t.ByteEnum("LODGroup", "TextureGroup", lg);
        // Non-power-of-two textures crash the editor's mip generator (TextureCompressor assert: can't halve an
        // odd dimension). Disable mip generation for them so they load instead of taking down the whole map.
        bool isPow2(int v) => v > 0 && (v & (v - 1)) == 0;
        if (!isPow2(w) || !isPow2(h)) t.ByteEnum("MipGenSettings", "TextureMipGenSettings", "TMGS_NoMipmaps");
        t.WriteNone();

        aw.Write(0);                                    // UObject: bSerializeGuid (int32 bool) = 0 (no ObjectGuid)
        aw.Write((byte)0); aw.Write((byte)0);          // FStripDataFlags (UTexture); editor data NOT stripped
        aw.Write(0x40);                                 // FByteBulkData flags = BULKDATA_ForceInlinePayload
        aw.Write(png.Length);                           // ElementCount
        aw.Write(png.Length);                           // SizeOnDisk
        aw.Write((long)0);                              // OffsetInFile (ignored for inline payload)
        aw.WriteBytes(png);                             // inline source PNG, right after the header
        aw.Write((byte)0); aw.Write((byte)0);          // FStripDataFlags (UTexture2D)
        aw.Write(0);                                    // bCooked (int32) = false
        aw.Flush();

        spw.AddExport(targetShort, tex2dClass, 0, 0, ms.ToArray(), objectFlags: 0x3, templatePkgIndex: 0, isAsset: true);
        spw.Write(outFile);
        Log.Information("Editor texture {N} -> {Out} ({W}x{H}, {B}B PNG)", targetShort, outFile, w, h, png.Length);
        return true;
    }
}

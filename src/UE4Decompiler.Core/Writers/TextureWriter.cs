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

    public static bool WriteEditorTexture(CUE4Parse.UE4.Assets.Exports.Texture.UTexture tex, string outFile, string targetShort, string targetPackagePath)
    {
        bool isCube = tex is CUE4Parse.UE4.Assets.Exports.Texture.UTextureCube;
        byte[] png; int w = 0, h = 0, numSlices;
        try
        {
            // One decode gives the whole image. For a cube, CUE4Parse returns the 6 faces as a vertical strip
            // (W x 6W); reinterpret that as NumSlices=6 of WxW (the strip bytes are already 6 consecutive WxW faces).
            int mipIdx = tex.GetMipIndexByMaxSize(MaxDim);
            // Detex (native) is initialized at startup, so the default decoder handles BC1/3/7 AND BC6H HDR correctly.
            var decoded = TextureDecoder.DecodeMip(tex, mipIdx, ETexturePlatform.DesktopMobile, 0) ?? TextureDecoder.Decode(tex, ETexturePlatform.DesktopMobile);
            if (decoded is null) { Log.Warning("Texture {N}: no decodable mip", targetShort); return false; }
            using (var raw = TextureEncoder.ToSkBitmap(decoded))
            {
                int dw = raw.Width, dh = raw.Height;
                using var bmp = (raw.ColorType == SKColorType.Bgra8888 && raw.AlphaType == SKAlphaType.Unpremul)
                    ? raw : raw.Copy(SKColorType.Bgra8888);
                png = bmp.Bytes;          // raw BGRA8888. NOTE: UE5.1 still mis-lays-out this source (sheared/recolored)
                                          // because it expects an FEditorBulkData end-of-file payload, not our 4.21
                                          // inline FByteBulkData. PNG-compressed source (TSCF_PNG) is worse — UE5
                                          // builds 0 mips (black) from the inline bulk. A correct UE5.1 editor texture
                                          // needs the FEditorBulkData payload + package trailer (TODO).
                w = dw; h = dh; numSlices = 1;
                if (LooksBlackOrEmpty(png)) Log.Warning("Texture {N}: decoded all black/empty ({W}x{H})", targetShort, dw, dh);
                if (isCube && dw > 0 && dh == dw * 6) { numSlices = 6; h = dw; }   // vertical 6-face strip -> 6 slices WxW
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Texture {N}: decode failed", targetShort); return false; }
        if (png.Length == 0 || w <= 0 || h <= 0) return false;

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int tex2dClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, isCube ? "TextureCube" : "Texture2D");

        using var ms = new MemoryStream();
        using var aw = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(aw, spw.Name);

        t.Struct("Source", "TextureSource", () =>
        {
            var inner = new TaggedPropertyWriter(aw, spw.Name);
            inner.GuidStruct("Id", FGuid16.NewGuid());
            inner.Int("SizeX", w);
            inner.Int("SizeY", h);
            inner.Int("NumSlices", numSlices);
            inner.Int("NumMips", 1);
            inner.Bool("bPNGCompressed", false);                                                  // raw bulk, not PNG
            inner.ByteEnum("CompressionFormat", "ETextureSourceCompressionFormat", "TSCF_None");    // UE5: source bulk is uncompressed
            inner.Bool("bGuidIsHash", false);                                                     // UE5: Id is a plain guid, not a content hash
            inner.ByteEnum("Format", "ETextureSourceFormat", "TSF_BGRA8");
            inner.ByteEnumArray("LayerFormat", new[] { "TSF_BGRA8" });                              // per-layer format (UE5 reads this, not Format) — without it the editor mis-laid-out the raw source (shear + channel swap)
            inner.Int64Array("BlockDataOffsets", new long[] { 0 });                                 // single block at bulk offset 0 (matches editor-saved source)
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

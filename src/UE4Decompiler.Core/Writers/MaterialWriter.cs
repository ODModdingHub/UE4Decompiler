using CUE4Parse.UE4.Versions;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Builds an editor-loadable default-LIT UMaterial from scratch (4.21) that samples one texture into BaseColor,
/// so meshes show their texture under lighting:
///   EXPORT[0] Material: BaseColor(ColorMaterialInput wired to the sample), ShadingModel default (MSM_DefaultLit),
///                       Expressions=[expr]; then bSerializeGuid=0 + inline-shader-map count=0 (editor recompiles).
///   EXPORT[1] MaterialExpressionTextureSampleParameter2D: Texture=<our texture>, Material=back-ref.
///
/// FExpressionInput native layout (4.21, editor): Expression(FPackageIndex i32) OutputIndex(i32) InputName(FName)
///   Mask(i32) MaskR MaskG MaskB MaskA(i32); FMaterialInput adds UseConstant(i32 bool) + Constant(FColor 4B).
/// </summary>
public static class MaterialWriter
{
    /// <summary>Fallback when a material has no resolvable texture: a valid unlit UMaterial with a constant grey
    /// EmissiveColor (no expressions, no imports beyond the Material class). Writing this instead of a stub header
    /// keeps the asset PARSEABLE — a 248-byte placeholder header reads as "unrecognizable data" and crashes the
    /// editor when a map references it.</summary>
    public static bool WriteEditorMaterialFlat(string outFile, string targetShort, string targetPackagePath)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");

        byte[] matPayload;
        using (var ms = new MemoryStream())
        {
            using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            t.Struct("BaseColor", "ColorMaterialInput", () =>
            {
                w.Write(0);             // Expression (none)
                w.Write(0);             // OutputIndex
                w.Write(spw.Name("None")); w.Write(0);   // InputName
                w.Write(0);             // Mask
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);   // MaskR/G/B/A
                w.Write(1);             // UseConstant = true
                w.Write(0xFF808080u);   // Constant (FColor BGRA) = mid grey
            });
            // ShadingModel left default (MSM_DefaultLit).
            t.GuidStruct("StateId", FGuid16.NewGuid());
            t.WriteNone();
            w.Write(0);                 // UObject bSerializeGuid = 0
            w.Write(0);                 // SerializeInlineShaderMaps: NumResources = 0
            w.Flush();
            matPayload = ms.ToArray();
        }
        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2, templatePkgIndex: 0, isAsset: true);
        spw.Write(outFile);
        Log.Information("Flat editor material {N} -> {Out} (no texture)", targetShort, outFile);
        return true;
    }

    public static bool WriteEditorMaterial(string outFile, string targetShort, string targetPackagePath,
        string texturePackagePath, string textureName)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);

        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int sampleClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureSampleParameter2D");
        int texPkg = spw.AddImport("/Script/CoreUObject", "Package", 0, texturePackagePath);
        int texImp = spw.AddImport("/Script/Engine", "Texture2D", texPkg, textureName);

        const int matExport = 1;        // FPackageIndex of EXPORT[0] (the Material)
        const int exprExport = 2;       // FPackageIndex of EXPORT[1] (the TextureSample)

        // EXPORT[1] payload: MaterialExpressionTextureSampleParameter2D
        byte[] exprPayload;
        using (var ms = new MemoryStream())
        {
            using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            t.Name("ParameterName", "Param");
            t.Object("Texture", texImp);
            t.ByteEnum("SamplerType", "EMaterialSamplerType", "SAMPLERTYPE_Color");
            t.Object("Material", matExport);
            t.WriteNone();
            w.Write(0);                 // UObject bSerializeGuid = 0
            w.Flush();
            exprPayload = ms.ToArray();
        }

        // EXPORT[0] payload: Material (unlit, emissive wired to the sample)
        byte[] matPayload;
        using (var ms = new MemoryStream())
        {
            using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            // BaseColor = ColorMaterialInput wired to exprExport's RGB output (default-lit material).
            t.Struct("BaseColor", "ColorMaterialInput", () =>
            {
                w.Write(exprExport);    // FExpressionInput.Expression (FPackageIndex)
                w.Write(0);             // OutputIndex
                w.Write(spw.Name("None")); w.Write(0);   // InputName (FName)
                w.Write(0);             // Mask
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);   // MaskR/G/B/A
                w.Write(0);             // FMaterialInput.UseConstant (i32 bool)
                w.Write(0u);            // FMaterialInput.Constant (FColor, 4 bytes)
            });
            // ShadingModel left default (MSM_DefaultLit) — lit material, not unlit/emissive.
            t.ObjectArray("Expressions", new[] { exprExport });
            t.GuidStruct("StateId", FGuid16.NewGuid());
            t.WriteNone();
            w.Write(0);                 // UObject bSerializeGuid = 0
            w.Write(0);                 // SerializeInlineShaderMaps: NumResources = 0 (editor recompiles)
            w.Flush();
            matPayload = ms.ToArray();
        }

        // RF_Public|RF_Standalone for the material; expr is a sub-object (RF_Public).
        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2, templatePkgIndex: 0, isAsset: true);
        spw.AddExport(targetShort + "_Sample", sampleClass, 0, matExport, exprPayload, objectFlags: 0x1, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("Editor material {N} -> {Out} (tex {T})", targetShort, outFile, texturePackagePath + "." + textureName);
        return true;
    }
}

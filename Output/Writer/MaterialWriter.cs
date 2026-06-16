using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Builds editor-loadable UE material assets from scratch. UE5 keeps editable material inputs on a
/// MaterialEditorOnlyData subobject, so these packages synthesize that shape instead of putting graph
/// properties directly on UMaterial.
/// </summary>
public static class MaterialWriter
{
    private const string DefaultTemplatePath = @"C:\Temp\A2Project\Content\A2\Maps\LookDev\Arenas\matitddy.uasset";

    public static bool WriteEditorMaterialFlat(string outFile, string targetShort, string targetPackagePath,
        uint constantColorBgra = 0xFF808080u, uint? emissiveColorBgra = null)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");

        const int matExport = 1;
        const int editorDataExport = 2;

        var editorPayload = BuildEditorOnlyData(spw, expressionExport: 0, constantColorBgra: constantColorBgra, emissiveColorBgra: emissiveColorBgra);
        var matPayload = BuildMaterialShell(spw, editorDataExport);

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("Flat editor material {N} -> {Out} (no texture)", targetShort, outFile);
        return true;
    }

    public static bool WriteEditorMaterial(string outFile, string targetShort, string targetPackagePath,
        string texturePackagePath, string textureName, float uTiling = 1f, float vTiling = 1f)
    {
        var templatePath = Environment.GetEnvironmentVariable("MAT_TEMPLATE");
        if (string.IsNullOrWhiteSpace(templatePath)) templatePath = DefaultTemplatePath;
        if (File.Exists(templatePath) && TryCloneMaterialTemplate(templatePath, outFile, targetShort, targetPackagePath, texturePackagePath, textureName))
            return true;

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);

        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");
        int sampleClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureSample");
        int texPkg = spw.AddImport("/Script/CoreUObject", "Package", 0, texturePackagePath);
        int texImp = spw.AddImport("/Script/Engine", "Texture2D", texPkg, textureName);

        const int matExport = 1;
        const int editorDataExport = 2;
        const int exprExport = 3;        // TextureSample
        // Only add a TextureCoordinate node when the material actually tiles (U/V != 1) — keeps simple materials simple.
        bool tile = MathF.Abs(uTiling - 1f) > 0.001f || MathF.Abs(vTiling - 1f) > 0.001f;
        int coordExport = tile ? 4 : 0;
        int coordClass = tile ? spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureCoordinate") : 0;

        var matPayload = BuildMaterialShell(spw, editorDataExport);
        var editorPayload = BuildEditorOnlyData(spw, exprExport, constantColorBgra: 0, extraExpression: coordExport);
        var exprPayload = BuildTextureSample(spw, matExport, texImp, coordExport);

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        spw.AddExport(targetShort + "_Sample", sampleClass, 0, matExport, exprPayload, objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        if (tile)
            spw.AddExport(targetShort + "_TexCoord", coordClass, 0, matExport, BuildTextureCoordinate(spw, matExport, uTiling, vTiling), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("Editor material {N} -> {Out} (tex {T}, tiling {U}x{V})", targetShort, outFile, texturePackagePath + "." + textureName, uTiling, vTiling);
        return true;
    }

    /// <summary>Texture base-color × recovered tint color (the MIC's dominant Vector param), fully-rough lit. Recovers
    /// the team/accent colors that a plain TextureSample drops (e.g. MI_TeamArena_* tinting the arena pattern blue/gold).
    /// Graph: TextureSample [× TextureCoordinate] -> Multiply.A ; Constant3Vector(tint) -> Multiply.B ; Multiply -> BaseColor.</summary>
    public static bool WriteEditorMaterialTinted(string outFile, string targetShort, string targetPackagePath,
        string texturePackagePath, string textureName, float uTiling, float vTiling, uint tintBgra)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");
        int sampleClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureSample");
        int const3Class = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionConstant3Vector");
        int mulClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionMultiply");
        int texPkg = spw.AddImport("/Script/CoreUObject", "Package", 0, texturePackagePath);
        int texImp = spw.AddImport("/Script/Engine", "Texture2D", texPkg, textureName);

        const int matExport = 1;
        const int editorDataExport = 2;
        const int sampleExport = 3;
        bool tile = MathF.Abs(uTiling - 1f) > 0.001f || MathF.Abs(vTiling - 1f) > 0.001f;
        int coordExport = tile ? 4 : 0;
        int coordClass = tile ? spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureCoordinate") : 0;
        int const3Export = tile ? 5 : 4;
        int mulExport = tile ? 6 : 5;

        var exprs = new List<int> { sampleExport };
        if (tile) exprs.Add(coordExport);
        exprs.Add(const3Export);
        exprs.Add(mulExport);

        var matPayload = BuildMaterialShell(spw, editorDataExport);
        var editorPayload = BuildEditorOnlyDataExprs(spw, baseColorExpr: mulExport, allExprs: exprs);
        var samplePayload = BuildTextureSample(spw, matExport, texImp, coordExport);
        var const3Payload = BuildConstant3Vector(spw, matExport, tintBgra);
        var mulPayload = BuildMultiply(spw, matExport, sampleExport, const3Export);

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        spw.AddExport(targetShort + "_Sample", sampleClass, 0, matExport, samplePayload, objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        if (tile)
            spw.AddExport(targetShort + "_TexCoord", coordClass, 0, matExport, BuildTextureCoordinate(spw, matExport, uTiling, vTiling), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        spw.AddExport(targetShort + "_Tint", const3Class, 0, matExport, const3Payload, objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        spw.AddExport(targetShort + "_Mul", mulClass, 0, matExport, mulPayload, objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("Tinted editor material {N} -> {Out} (tex {T} x #{C:X8}, tiling {U}x{V})", targetShort, outFile, textureName, tintBgra, uTiling, vTiling);
        return true;
    }

    private static bool TryCloneMaterialTemplate(string templatePath, string outFile, string targetShort, string targetPackagePath,
        string texturePackagePath, string textureName)
    {
        try
        {
            var data = File.ReadAllBytes(templatePath);
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(templatePath), data, new VersionContainer(EGame.GAME_UE5_1));
            var pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null,
                (CUE4Parse.FileProvider.IFileProvider?)null, false);

            var materialExport = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "Material");
            if (materialExport < 0) return false;
            var oldShort = pkg.ExportMap[materialExport].ObjectName.Text;
            var oldPath = pkg.NameMap.Select(n => n.Name)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.EndsWith("/" + oldShort, StringComparison.Ordinal));

            var texImportIdx = Array.FindIndex(pkg.ImportMap, i => i.ClassName.Text == "Texture2D");
            string? oldTexName = null, oldTexPath = null;
            if (texImportIdx >= 0)
            {
                oldTexName = pkg.ImportMap[texImportIdx].ObjectName.Text;
                var outer = pkg.ImportMap[texImportIdx].OuterIndex?.Index ?? 0;
                if (outer < 0 && -outer - 1 < pkg.ImportMap.Length)
                    oldTexPath = pkg.ImportMap[-outer - 1].ObjectName.Text;
            }

            var rename = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(oldPath)) rename[oldPath] = targetPackagePath;
            rename[oldShort] = targetShort;
            rename[oldShort + "EditorOnlyData"] = targetShort + "EditorOnlyData";
            if (!string.IsNullOrWhiteSpace(oldTexPath)) rename[oldTexPath] = texturePackagePath;
            if (!string.IsNullOrWhiteSpace(oldTexName)) rename[oldTexName] = textureName;

            var spw = new SynthPackageWriter(EGame.GAME_UE5_1, targetPackagePath)
            {
                PackageFlags = (uint)pkg.Summary.PackageFlags,
                CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions
                    ?.Select(v => (v.Key, v.Version)).ToList()
            };

            foreach (var n in pkg.NameMap)
            {
                var s = n.Name ?? "None";
                spw.AddRawName(rename.TryGetValue(s, out var rn) ? rn : s);
            }

            foreach (var imp in pkg.ImportMap)
            {
                spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                    imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number,
                    imp.PackageName.Index, imp.PackageName.Number, imp.ImportOptional);
            }

            foreach (var e in pkg.ExportMap)
            {
                var off = (int)e.SerialOffset;
                var size = (int)e.SerialSize;
                var payload = new byte[size];
                Array.Copy(data, off, payload, 0, size);
                spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                    e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                    payload, (uint)e.ObjectFlags, e.IsAsset, e.ForcedExport, e.NotForClient, e.NotForServer,
                    e.PackageFlags, e.NotAlwaysLoadedForEditorGame, e.GeneratePublicHash,
                    e.ScriptSerializationStartOffset, e.ScriptSerializationEndOffset);
            }

            spw.Write(outFile);
            Log.Information("Cloned editor material {N} -> {Out} from {Template} (tex {T})",
                targetShort, outFile, templatePath, texturePackagePath + "." + textureName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Material template clone failed for {N}; falling back to synthetic material", targetShort);
            return false;
        }
    }

    private static byte[] BuildMaterialShell(SynthPackageWriter spw, int editorDataExport)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Bool("bCanMaskedBeAssumedOpaque", true);
        t.GuidStruct("StateId", FGuid16.NewGuid());
        t.Object("EditorOnlyData", editorDataExport);
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Write(0); // inline shader map count; editor recompiles
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildEditorOnlyData(SynthPackageWriter spw, int expressionExport, uint constantColorBgra,
        uint? emissiveColorBgra = null, int extraExpression = 0)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);

        t.Struct("BaseColor", "ColorMaterialInput", () =>
        {
            w.Write(expressionExport);
            w.Write(0);
            w.Write(spw.Name("None")); w.Write(0);
            w.Write(0);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(expressionExport == 0 ? 1 : 0);
            w.Write(constantColorBgra);
        });

        // Recovered emissive (Use Emissive switch + Emissive Color param) -> constant EmissiveColor input (same
        // ColorMaterialInput struct as BaseColor), so emissive surfaces (screens/lights) glow their real color.
        if (emissiveColorBgra.HasValue)
        {
            t.Struct("EmissiveColor", "ColorMaterialInput", () =>
            {
                w.Write(0);                              // no Expression -> constant
                w.Write(0);
                w.Write(spw.Name("None")); w.Write(0);
                w.Write(0);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                w.Write(1);                              // bUseConstant
                w.Write(emissiveColorBgra.Value);
            });
        }

        t.Struct("ExpressionCollection", "MaterialExpressionCollection", () =>
        {
            var exprs = new List<int>();
            if (expressionExport != 0) exprs.Add(expressionExport);
            if (extraExpression != 0) exprs.Add(extraExpression);
            if (exprs.Count > 0) t.ObjectArray("Expressions", exprs);
            t.WriteNone();
        });
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Write(0); // bSavedCachedExpressionData
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>MaterialEditorOnlyData with BaseColor driven by <paramref name="baseColorExpr"/> and the full expression
    /// list registered in ExpressionCollection (so the graph nodes survive load + the material compiles).</summary>
    private static byte[] BuildEditorOnlyDataExprs(SynthPackageWriter spw, int baseColorExpr, IReadOnlyList<int> allExprs)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Struct("BaseColor", "ColorMaterialInput", () =>
        {
            w.Write(baseColorExpr);                    // Expression (FPackageIndex)
            w.Write(0);                                // OutputIndex
            w.Write(spw.Name("None")); w.Write(0);     // InputName
            w.Write(0);                                // Mask
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(0);                                // bUseConstant = false (driven by expression)
            w.Write(0u);                               // Constant (unused)
        });
        t.Struct("ExpressionCollection", "MaterialExpressionCollection", () =>
        {
            if (allExprs.Count > 0) t.ObjectArray("Expressions", allExprs.ToList());
            t.WriteNone();
        });
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Write(0); // bSavedCachedExpressionData
        w.Flush();
        return ms.ToArray();
    }

    private static float SrgbToLinear(float s) => s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);

    private static byte[] BuildConstant3Vector(SynthPackageWriter spw, int matExport, uint bgra)
    {
        // bgra carries sRGB byte values (from FLinearColor.ToFColor(sRGB=true)); Constant3Vector.Constant is LINEAR,
        // so convert back to linear or the tint renders washed-out/too bright.
        float r = SrgbToLinear(((bgra >> 16) & 0xFF) / 255f), g = SrgbToLinear(((bgra >> 8) & 0xFF) / 255f), b = SrgbToLinear((bgra & 0xFF) / 255f);
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Struct("Constant", "LinearColor", () => { w.Write(r); w.Write(g); w.Write(b); w.Write(1f); });
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildMultiply(SynthPackageWriter spw, int matExport, int aExpr, int bExpr)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        void Input(string name, int expr) => t.Struct(name, "ExpressionInput", () =>
        {
            w.Write(expr); w.Write(0);
            w.Write(spw.Name("None")); w.Write(0);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        });
        Input("A", aExpr);
        Input("B", bExpr);
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildTextureSample(SynthPackageWriter spw, int matExport, int texImport, int coordExpr = 0)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Object("Texture", texImport);
        // Wire UV tiling: Coordinates input <- a TextureCoordinate expression (FExpressionInput, no constant).
        if (coordExpr != 0)
            t.Struct("Coordinates", "ExpressionInput", () =>
            {
                w.Write(coordExpr);                       // Expression (FPackageIndex)
                w.Write(0);                               // OutputIndex
                w.Write(spw.Name("None")); w.Write(0);    // InputName
                w.Write(0);                               // Mask
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);   // MaskR/G/B/A
            });
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildTextureCoordinate(SynthPackageWriter spw, int matExport, float uTiling, float vTiling)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Int("CoordinateIndex", 0);
        t.Float("UTiling", uTiling);
        t.Float("VTiling", vTiling);
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Flush();
        return ms.ToArray();
    }
}

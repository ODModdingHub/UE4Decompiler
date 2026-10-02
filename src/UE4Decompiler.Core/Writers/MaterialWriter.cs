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
        uint constantColorBgra = 0xFF808080u, uint? emissiveColorBgra = null,
        string? shadingModel = null, string? blendMode = null)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");

        const int matExport = 1;
        const int editorDataExport = 2;

        var editorPayload = BuildEditorOnlyData(spw, expressionExport: 0, constantColorBgra: constantColorBgra, emissiveColorBgra: emissiveColorBgra);
        var matPayload = BuildMaterialShell(spw, editorDataExport, shadingModel, blendMode);

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

    /// <summary>A recovered texture slot for the PBR material: package path, asset name, and the EMaterialSamplerType
    /// name ("SAMPLERTYPE_Color"/"SAMPLERTYPE_Normal"/"SAMPLERTYPE_LinearColor"/"SAMPLERTYPE_Masks").</summary>
    public sealed record MatSlot(string Pkg, string Name, string SamplerType);

    /// <summary>Reconstructs a lit PBR material from the material instance's recovered texture parameters:
    /// BaseColor (sRGB color), Normal (normal sampler), ORM (R=AO/G=Roughness/B=Metallic, linear), Emissive, and
    /// an optional OpacityMask alpha. This replaces the old "sample the first texture, unlit" heuristic so the
    /// cooked game's surfaces read correctly (normals, roughness, metal, emission) instead of flat grey/one map.</summary>
    public static bool WriteEditorMaterialPbr(string outFile, string targetShort, string targetPackagePath,
        MatSlot? baseColor, MatSlot? normal, MatSlot? orm, MatSlot? emissive, MatSlot? opacity,
        float uTiling = 1f, float vTiling = 1f, string? shadingModel = null, string? blendMode = null,
        uint emissiveTintBgra = 0xFFFFFFFFu, float emissiveStrength = 1f)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");
        int sampleClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureSample");
        int maskClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionComponentMask");
        int const3Class = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionConstant3Vector");
        int constScalarClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionConstant");
        int mulClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionMultiply");
        bool tile = MathF.Abs(uTiling - 1f) > 0.001f || MathF.Abs(vTiling - 1f) > 0.001f;
        int coordClass = tile ? spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureCoordinate") : 0;

        const int matExport = 1;
        const int editorDataExport = 2;
        int next = 3;

        var allExprs = new List<int>();
        var samples = new List<(int exp, MatSlot slot, int texImp)>();
        var masks = new List<(int exp, int inputExpr, bool r, bool g, bool b, bool a)>();
        int coordExport = 0;
        if (tile) { coordExport = next++; allExprs.Add(coordExport); }

        int AddSample(MatSlot? slot)
        {
            if (slot is null) return 0;
            int pkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, slot.Pkg);
            int texImp = spw.AddImport("/Script/Engine", "Texture2D", pkgImp, slot.Name);
            int e = next++;
            samples.Add((e, slot, texImp));
            allExprs.Add(e);
            return e;
        }
        int AddMask(int inputExpr, bool r, bool g, bool b, bool a)
        {
            if (inputExpr == 0) return 0;
            int e = next++;
            masks.Add((e, inputExpr, r, g, b, a));
            allExprs.Add(e);
            return e;
        }

        int baseColorExpr = AddSample(baseColor);
        int normalExpr = AddSample(normal);
        int ormExpr = AddSample(orm);
        int emissiveExpr = AddSample(emissive);
        int opacityExpr = AddSample(opacity);
        int ormR = AddMask(ormExpr, true, false, false, false);
        int ormG = AddMask(ormExpr, false, true, false, false);
        int ormB = AddMask(ormExpr, false, false, true, false);
        int opacityA = AddMask(opacityExpr, false, false, false, true);
        // Emissive = sample x tint x strength (MM_Environment semantics: Emissive Mask x Emissive
        // Color x Emissive Strength). Without this, strength-10 trim/signage glow renders ~black.
        var tintMuls = new List<(int exp, int a, int b)>();
        var const3s = new List<(int exp, uint bgra)>();
        var constScalars = new List<(int exp, float val)>();
        if (emissiveExpr != 0 && (emissiveTintBgra != 0xFFFFFFFFu || MathF.Abs(emissiveStrength - 1f) > 0.0001f))
        {
            int cur = emissiveExpr;
            if (emissiveTintBgra != 0xFFFFFFFFu)
            {
                int ce = next++; allExprs.Add(ce); const3s.Add((ce, emissiveTintBgra));
                int me = next++; allExprs.Add(me); tintMuls.Add((me, cur, ce));
                cur = me;
            }
            if (MathF.Abs(emissiveStrength - 1f) > 0.0001f && emissiveStrength > 0f)
            {
                int se = next++; allExprs.Add(se); constScalars.Add((se, emissiveStrength));
                int me = next++; allExprs.Add(me); tintMuls.Add((me, cur, se));
                cur = me;
            }
            emissiveExpr = cur;
        }

        var matPayload = BuildMaterialShell(spw, editorDataExport, shadingModel, blendMode);
        var editorPayload = BuildPbrEditorOnlyData(spw, matExport, allExprs, new PbrInputs
        {
            BaseColor = baseColorExpr,
            Normal = normalExpr,
            Roughness = ormG != 0 ? ormG : -1,     // -1 = constant fallback
            Metallic = ormB != 0 ? ormB : -1,
            AmbientOcclusion = ormR,
            Emissive = emissiveExpr,
            OpacityMask = opacityA,
        });

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        if (tile)
            spw.AddExport(targetShort + "_TexCoord", coordClass, 0, matExport, BuildTextureCoordinate(spw, matExport, uTiling, vTiling), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        foreach (var (exp, slot, texImp) in samples)
            spw.AddExport(targetShort + "_S" + exp, sampleClass, 0, matExport, BuildTextureSampleTyped(spw, matExport, texImp, coordExport, slot.SamplerType), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        foreach (var (exp, inputExpr, r, g, b, a) in masks)
            spw.AddExport(targetShort + "_M" + exp, maskClass, 0, matExport, BuildComponentMask(spw, matExport, inputExpr, r, g, b, a), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        foreach (var (exp, bgra) in const3s)
            spw.AddExport(targetShort + "_T" + exp, const3Class, 0, matExport, BuildConstant3Vector(spw, matExport, bgra), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        foreach (var (exp, val) in constScalars)
            spw.AddExport(targetShort + "_E" + exp, constScalarClass, 0, matExport, BuildConstantScalar(spw, matExport, val), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        foreach (var (exp, a, b) in tintMuls)
            spw.AddExport(targetShort + "_X" + exp, mulClass, 0, matExport, BuildMultiply(spw, matExport, a, b), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("PBR editor material {N} -> {Out} (bc={BC} n={NM} orm={ORM} em={EM} op={OP}, tiling {U}x{V} tint=#{T:X8} emStr={S})",
            targetShort, outFile, baseColor?.Name, normal?.Name, orm?.Name, emissive?.Name, opacity?.Name, uTiling, vTiling, emissiveTintBgra, emissiveStrength);
        return true;
    }

    /// <summary>Reconstructs an UNLIT material (signage, message boards, holo, VFX): the source texture drives
    /// EmissiveColor (optionally × the MI's Emissive Color tint) on an MSM_Unlit shell with the source blend mode
    /// (Additive/Translucent/…). Without this, additive red boards flatten to dark-red lit-opaque planes.</summary>
    public static bool WriteEditorMaterialUnlit(string outFile, string targetShort, string targetPackagePath,
        MatSlot? emissive, uint emissiveTintBgra, string blendMode,
        float uTiling = 1f, float vTiling = 1f, float emissiveStrength = 1f)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");
        int sampleClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureSample");
        int const3Class = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionConstant3Vector");
        int constScalarClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionConstant");
        int mulClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionMultiply");
        bool tile = MathF.Abs(uTiling - 1f) > 0.001f || MathF.Abs(vTiling - 1f) > 0.001f;
        int coordClass = tile ? spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialExpressionTextureCoordinate") : 0;

        const int matExport = 1;
        const int editorDataExport = 2;
        int next = 3;
        var allExprs = new List<int>();
        int coordExport = 0;
        if (tile) { coordExport = next++; allExprs.Add(coordExport); }
        int sampleExport = 0, texImp = 0;
        string sampler = "SAMPLERTYPE_Color";
        if (emissive is not null)
        {
            int pkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, emissive.Pkg);
            texImp = spw.AddImport("/Script/Engine", "Texture2D", pkgImp, emissive.Name);
            sampler = emissive.SamplerType;
            sampleExport = next++;
            allExprs.Add(sampleExport);
        }
        // Emissive chain: sample x tint x strength (source master semantics).
        bool tinted = emissiveTintBgra != 0xFFFFFFFFu;
        bool boosted = MathF.Abs(emissiveStrength - 1f) > 0.0001f && emissiveStrength > 0f;
        int const3Export = 0, mulExport = 0, scalarExport = 0, mul2Export = 0;
        int emOut = sampleExport;
        if (sampleExport != 0 && tinted)
        {
            const3Export = next++; allExprs.Add(const3Export);
            mulExport = next++; allExprs.Add(mulExport);
            emOut = mulExport;
        }
        if (emOut != 0 && boosted)
        {
            scalarExport = next++; allExprs.Add(scalarExport);
            mul2Export = next++; allExprs.Add(mul2Export);
            emOut = mul2Export;
        }

        var matPayload = BuildMaterialShell(spw, editorDataExport, "MSM_Unlit", blendMode);
        var editorPayload = BuildPbrEditorOnlyData(spw, matExport, allExprs, new PbrInputs
        {
            Emissive = emOut,
        });

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        if (tile)
            spw.AddExport(targetShort + "_TexCoord", coordClass, 0, matExport, BuildTextureCoordinate(spw, matExport, uTiling, vTiling), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        if (sampleExport != 0)
            spw.AddExport(targetShort + "_S" + sampleExport, sampleClass, 0, matExport, BuildTextureSampleTyped(spw, matExport, texImp, coordExport, sampler), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        if (mulExport != 0)
        {
            spw.AddExport(targetShort + "_Tint", const3Class, 0, matExport, BuildConstant3Vector(spw, matExport, emissiveTintBgra), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
            spw.AddExport(targetShort + "_Mul", mulClass, 0, matExport, BuildMultiply(spw, matExport, sampleExport, const3Export), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        }
        if (mul2Export != 0)
        {
            spw.AddExport(targetShort + "_Str", constScalarClass, 0, matExport, BuildConstantScalar(spw, matExport, emissiveStrength), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
            spw.AddExport(targetShort + "_Mul2", mulClass, 0, matExport, BuildMultiply(spw, matExport, mulExport != 0 ? mulExport : sampleExport, scalarExport), objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        }
        spw.Write(outFile);
        Log.Information("Unlit editor material {N} -> {Out} (em={EM} tint=#{T:X8} str={S} blend={B})",
            targetShort, outFile, emissive?.Name, emissiveTintBgra, emissiveStrength, blendMode);
        return true;
    }

    private sealed class PbrInputs
    {
        public int BaseColor;
        public int Normal;
        public int Roughness;      // -1 = constant
        public int Metallic;       // -1 = constant
        public int AmbientOcclusion;
        public int Emissive;
        public int OpacityMask;
    }

    private static byte[] BuildPbrEditorOnlyData(SynthPackageWriter spw, int matExport, IReadOnlyList<int> allExprs, PbrInputs inp)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);

        // FExpressionInput body shared by every *MaterialInput struct (matches the proven BaseColor layout).
        void ExprInput(int expr)
        {
            w.Write(expr);                             // Expression (FPackageIndex, positive = export)
            w.Write(0);                                // OutputIndex
            w.Write(spw.Name("None")); w.Write(0);     // InputName
            w.Write(0);                                // Mask
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);   // MaskR/G/B/A
        }
        void ColorInput(string name, int expr, uint constant)
        {
            t.Struct(name, "ColorMaterialInput", () => { ExprInput(expr); w.Write(expr == 0 ? 1 : 0); w.Write(constant); });
        }
        void ScalarInput(string name, int expr, float constant)
        {
            t.Struct(name, "ScalarMaterialInput", () => { ExprInput(expr); w.Write(expr == 0 ? 1 : 0); w.Write(constant); });
        }
        void VectorInput(string name, int expr, float x, float y, float z)
        {
            t.Struct(name, "VectorMaterialInput", () => { ExprInput(expr); w.Write(expr == 0 ? 1 : 0); w.Write(x); w.Write(y); w.Write(z); });
        }

        ColorInput("BaseColor", inp.BaseColor, 0xFF808080u);
        if (inp.Normal != 0) VectorInput("Normal", inp.Normal, 0f, 0f, 1f);
        ScalarInput("Roughness", inp.Roughness > 0 ? inp.Roughness : 0, 0.5f);
        ScalarInput("Metallic", inp.Metallic > 0 ? inp.Metallic : 0, 0f);
        if (inp.AmbientOcclusion != 0) ScalarInput("AmbientOcclusion", inp.AmbientOcclusion, 1f);
        if (inp.Emissive != 0) ColorInput("EmissiveColor", inp.Emissive, 0u);
        if (inp.OpacityMask != 0) ScalarInput("OpacityMask", inp.OpacityMask, 1f);

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

    private static byte[] BuildTextureSampleTyped(SynthPackageWriter spw, int matExport, int texImport, int coordExpr, string samplerType)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Object("Texture", texImport);
        if (coordExpr != 0)
            t.Struct("Coordinates", "ExpressionInput", () =>
            {
                w.Write(coordExpr); w.Write(0); w.Write(spw.Name("None")); w.Write(0);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            });
        t.ByteEnum("SamplerType", "EMaterialSamplerType", samplerType);
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildComponentMask(SynthPackageWriter spw, int matExport, int inputExpr, bool r, bool g, bool b, bool a)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Struct("Input", "ExpressionInput", () =>
        {
            w.Write(inputExpr); w.Write(0); w.Write(spw.Name("None")); w.Write(0);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        });
        t.Bool("R", r); t.Bool("G", g); t.Bool("B", b); t.Bool("A", a);
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0);
        w.Flush();
        return ms.ToArray();
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

    private static byte[] BuildMaterialShell(SynthPackageWriter spw, int editorDataExport,
        string? shadingModel = null, string? blendMode = null)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Bool("bCanMaskedBeAssumedOpaque", true);
        // Source-driven shading path: unlit/additive masters (signage, message boards, holo, VFX) must keep
        // their look — without this a red additive-unlit board flattens to a dark-red lit-opaque plane.
        if (!string.IsNullOrEmpty(blendMode) && blendMode != "BLEND_Opaque")
            t.ByteEnum("BlendMode", "EBlendMode", blendMode);
        if (!string.IsNullOrEmpty(shadingModel) && shadingModel != "MSM_DefaultLit")
            t.ByteEnum("ShadingModel", "EMaterialShadingModel", shadingModel);
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

    private static byte[] BuildConstantScalar(SynthPackageWriter spw, int matExport, float value)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Float("Constant", value);
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

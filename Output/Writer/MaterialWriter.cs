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

    public static bool WriteEditorMaterialFlat(string outFile, string targetShort, string targetPackagePath)
    {
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int matClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Material");
        int editorDataClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MaterialEditorOnlyData");

        const int matExport = 1;
        const int editorDataExport = 2;

        var editorPayload = BuildEditorOnlyData(spw, expressionExport: 0, constantColorBgra: 0xFF808080u);
        var matPayload = BuildMaterialShell(spw, editorDataExport);

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("Flat editor material {N} -> {Out} (no texture)", targetShort, outFile);
        return true;
    }

    public static bool WriteEditorMaterial(string outFile, string targetShort, string targetPackagePath,
        string texturePackagePath, string textureName)
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
        const int exprExport = 3;

        var matPayload = BuildMaterialShell(spw, editorDataExport);
        var editorPayload = BuildEditorOnlyData(spw, exprExport, constantColorBgra: 0);
        var exprPayload = BuildTextureSample(spw, matExport, texImp);

        spw.AddExport(targetShort, matClass, 0, 0, matPayload, objectFlags: 0x1 | 0x2 | 0x8, templatePkgIndex: 0, isAsset: true);
        spw.AddExport("MaterialEditorOnlyData", editorDataClass, 0, matExport, editorPayload, objectFlags: 0x1 | 0x8, templatePkgIndex: 0, isAsset: false);
        spw.AddExport(targetShort + "_Sample", sampleClass, 0, matExport, exprPayload, objectFlags: 0x8, templatePkgIndex: 0, isAsset: false);
        spw.Write(outFile);
        Log.Information("Editor material {N} -> {Out} (tex {T})", targetShort, outFile, texturePackagePath + "." + textureName);
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

    private static byte[] BuildEditorOnlyData(SynthPackageWriter spw, int expressionExport, uint constantColorBgra)
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

        t.Struct("ExpressionCollection", "MaterialExpressionCollection", () =>
        {
            if (expressionExport != 0) t.ObjectArray("Expressions", new[] { expressionExport });
            t.WriteNone();
        });
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Write(0); // bSavedCachedExpressionData
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildTextureSample(SynthPackageWriter spw, int matExport, int texImport)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Object("Texture", texImport);
        t.Object("Material", matExport);
        t.GuidStruct("MaterialExpressionGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Write(0); // UObject bSerializeGuid
        w.Flush();
        return ms.ToArray();
    }
}

using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// [Tier2 Phase 4 — module #3] A from-scratch 4.21 package emitter driven by a plain model (names,
/// imports, exports + payload bytes), so we can SYNTHESIZE editor exports (UBlueprint/EdGraph/K2Node)
/// that don't exist in cooked paks. Mirrors the proven byte layout of <see cref="UncookedPackageWriter"/>
/// (versioned -7 / FileVersionUE4 517, same custom-version set, trailing PACKAGE_FILE_TAG) but takes a
/// mutable model instead of a CUE4Parse Package.
///
/// Layout: [Summary][NameTable][ImportMap][ExportMap][DependsMap][AssetRegistry][ExportData][tag].
/// FPackageIndex convention: export i -&gt; (i+1); import i -&gt; -(i+1); null -&gt; 0.
/// </summary>
public sealed class SynthPackageWriter
{
    private const uint RF_Load = 0x02D4003B;

    private readonly EGame _game;
    private readonly string _packageName;        // e.g. "/Game/Synth/SynthTest"
    public uint PackageFlags = 0;                // preserve original (e.g. PKG_RequiresLocalizationGather)
    /// <summary>If set, write these EXACT custom versions (GUID+version) instead of the hardcoded 4.21 set.
    /// CRITICAL when reusing real editor payloads: the engine deserializes them against the summary's
    /// custom versions, so a mismatch corrupts reads (Assertion SerializeNum>=0).</summary>
    public IReadOnlyList<(FGuid Key, int Version)>? CustomVersionsOverride;
    private readonly List<string> _names = new();
    private readonly Dictionary<string, int> _nameIdx = new(StringComparer.Ordinal);
    private readonly List<Imp> _imports = new();
    private readonly List<Exp> _exports = new();

    public SynthPackageWriter(EGame game, string packageName)
    {
        _game = game; _packageName = packageName;
        // NOTE: do NOT pre-seed "None" — when re-adding a cooked NameMap we must preserve its exact order
        // (its own None sits at the cooked position); pre-seeding would dedup-collapse and shift all indices.
    }

    public int Name(string s)
    {
        if (_nameIdx.TryGetValue(s, out var i)) return i;
        i = _names.Count; _names.Add(s); _nameIdx[s] = i; return i;
    }

    private sealed class Imp { public int ClassPkg, ClassPkgN, ClassName, ClassNameN, Outer, ObjName, ObjNameN; }
    private sealed class Exp
    {
        public int ObjName, ObjNameN, ClassIdx, SuperIdx, TemplateIdx, OuterIdx; public uint Flags; public bool IsAsset;
        public byte[] Payload = Array.Empty<byte>();
    }

    /// <summary>Pre-add a base name at a specific position (cooked NameMap order). Returns its index.</summary>
    public int AddRawName(string s) => Name(s);

    /// <summary>Re-add a cooked import preserving exact name indices + numbers. Returns FPackageIndex (negative).</summary>
    public int AddImportRaw(int classPkgIdx, int classPkgN, int classNameIdx, int classNameN, int outer, int objNameIdx, int objNameN)
    {
        _imports.Add(new Imp { ClassPkg = classPkgIdx, ClassPkgN = classPkgN, ClassName = classNameIdx, ClassNameN = classNameN, Outer = outer, ObjName = objNameIdx, ObjNameN = objNameN });
        return -_imports.Count;
    }

    /// <summary>Add a synthesized import (number 0). Returns FPackageIndex (negative).</summary>
    public int AddImport(string classPkg, string className, int outerPkgIndex, string objName)
        => AddImportRaw(Name(classPkg), 0, Name(className), 0, outerPkgIndex, Name(objName), 0);

    /// <summary>Re-add a cooked export preserving exact name index + number. Returns FPackageIndex (positive).</summary>
    public int AddExportRaw(int objNameIdx, int objNameN, int classIdx, int superIdx, int templateIdx, int outerIdx,
        byte[] payload, uint objectFlags, bool isAsset)
    {
        _exports.Add(new Exp { ObjName = objNameIdx, ObjNameN = objNameN, ClassIdx = classIdx, SuperIdx = superIdx, TemplateIdx = templateIdx, OuterIdx = outerIdx, Flags = objectFlags, IsAsset = isAsset, Payload = payload });
        return _exports.Count;
    }

    /// <summary>Add a synthesized export (object-name number 0). Returns FPackageIndex (positive).</summary>
    public int AddExport(string objName, int classPkgIndex, int superPkgIndex, int outerPkgIndex,
        byte[] payload, uint objectFlags = 0, int templatePkgIndex = 0, bool isAsset = false)
        => AddExportRaw(Name(objName), 0, classPkgIndex, superPkgIndex, templatePkgIndex, outerPkgIndex, payload, objectFlags, isAsset);

    public void Write(string outPath)
    {
        var namesBuf = SerializeNames();
        var importsBuf = SerializeImports();
        var dependsBuf = new byte[_exports.Count * 4];     // one empty depends array per export
        var (arBuf, _) = SerializeAssetRegistry();

        var summarySize = SerializeSummary(0, 0, 0, 0, 0, 0, 0).Length;
        var exportsSize = SerializeExports(null).Length;

        var nameOffset = summarySize;
        var importOffset = nameOffset + namesBuf.Length;
        var exportOffset = importOffset + importsBuf.Length;
        var dependsOffset = exportOffset + exportsSize;
        var arOffset = dependsOffset + dependsBuf.Length;
        var headerSize = arOffset + arBuf.Length;

        var offsets = new int[_exports.Count];
        var cursor = headerSize; var totalPayload = 0;
        for (var i = 0; i < _exports.Count; i++) { offsets[i] = cursor; cursor += _exports[i].Payload.Length; totalPayload += _exports[i].Payload.Length; }
        var bulkStart = headerSize + totalPayload;

        var exportsBuf = SerializeExports(offsets);
        var summaryBuf = SerializeSummary(headerSize, nameOffset, importOffset, exportOffset, dependsOffset, arOffset, bulkStart);
        if (summaryBuf.Length != summarySize) throw new InvalidOperationException($"summary drift {summaryBuf.Length}!={summarySize}");
        if (exportsBuf.Length != exportsSize) throw new InvalidOperationException($"export-map drift {exportsBuf.Length}!={exportsSize}");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var fs = File.Create(outPath);
        fs.Write(summaryBuf); fs.Write(namesBuf); fs.Write(importsBuf); fs.Write(exportsBuf); fs.Write(dependsBuf); fs.Write(arBuf);
        foreach (var e in _exports) fs.Write(e.Payload);
        fs.Write(BitConverter.GetBytes(0x9E2A83C1u));
    }

    private byte[] SerializeNames()
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        foreach (var n in _names) { w.WriteFString(n); w.Write(FCrc.NonCasePreservingHash(n)); w.Write(FCrc.CasePreservingHash(n)); }
        w.Flush(); return ms.ToArray();
    }

    private byte[] SerializeImports()
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        foreach (var imp in _imports)
        { w.Write(imp.ClassPkg); w.Write(imp.ClassPkgN); w.Write(imp.ClassName); w.Write(imp.ClassNameN); w.Write(imp.Outer); w.Write(imp.ObjName); w.Write(imp.ObjNameN); }
        w.Flush(); return ms.ToArray();
    }

    private byte[] SerializeExports(int[]? serialOffsets)
    {
        var ver = _game.GetVersion();
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        for (var i = 0; i < _exports.Count; i++)
        {
            var e = _exports[i];
            w.Write(e.ClassIdx); w.Write(e.SuperIdx);
            if (ver >= EUnrealEngineObjectUE4Version.TemplateIndex_IN_COOKED_EXPORTS) w.Write(e.TemplateIdx);
            w.Write(e.OuterIdx);
            w.Write(e.ObjName); w.Write(e.ObjNameN);                // ObjectName FName
            w.Write(e.Flags & RF_Load);
            if (ver < EUnrealEngineObjectUE4Version.e64BIT_EXPORTMAP_SERIALSIZES)
            { w.Write(e.Payload.Length); w.Write(serialOffsets?[i] ?? 0); }
            else
            { w.Write((long)e.Payload.Length); w.Write((long)(serialOffsets?[i] ?? 0)); }
            w.WriteBool(false); w.WriteBool(false); w.WriteBool(false);   // Forced/NotForClient/NotForServer
            w.WriteGuid(default);                                          // PackageGuid
            w.Write(0u);                                                   // PackageFlags
            if (ver >= EUnrealEngineObjectUE4Version.LOAD_FOR_EDITOR_GAME) w.WriteBool(true);  // NotAlwaysLoadedForEditorGame
            if (ver >= EUnrealEngineObjectUE4Version.COOKED_ASSETS_IN_EDITOR_SUPPORT) w.WriteBool(e.IsAsset);
            if (ver >= EUnrealEngineObjectUE4Version.PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
            { w.Write(-1); w.Write(0); w.Write(0); w.Write(0); w.Write(0); }
        }
        w.Flush(); return ms.ToArray();
    }

    private (byte[] buf, int count) SerializeAssetRegistry()
    {
        var assets = _exports.Where(e => e.IsAsset).ToList();
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        w.Write(assets.Count);
        foreach (var e in assets) { w.WriteFString(_names[e.ObjName]); w.WriteFString(ClassNameOf(e)); w.Write(0); }
        w.Flush(); return (ms.ToArray(), assets.Count);
    }

    /// <summary>Resolve an export's class name for the AssetRegistry section (import or export ref).</summary>
    private string ClassNameOf(Exp e)
    {
        if (e.ClassIdx < 0) { var i = -e.ClassIdx - 1; if (i < _imports.Count) return _names[_imports[i].ObjName]; }
        else if (e.ClassIdx > 0) { var i = e.ClassIdx - 1; if (i < _exports.Count) return _names[_exports[i].ObjName]; }
        return "Object";
    }

    private byte[] SerializeSummary(int totalHeaderSize, int nameOffset, int importOffset, int exportOffset,
        int dependsOffset, int arOffset, int bulkDataStart)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var ver = _game.GetVersion();
        w.Write(0x9E2A83C1u);
        w.Write(-7); w.Write(864);
        w.Write(ver.FileVersionUE4); w.Write(0);
        if (CustomVersionsOverride != null)
        {
            w.Write(CustomVersionsOverride.Count);
            foreach (var (key, version) in CustomVersionsOverride) { w.WriteGuid(key); w.Write(version); }
        }
        else
        {
            var cvs = UncookedPackageWriter.Build421CustomVersions();
            w.Write(cvs.Count);
            foreach (var cv in cvs) { w.WriteGuid(cv.Key); w.Write(cv.Version); }
        }
        w.Write(totalHeaderSize);
        w.WriteFString("None");                                  // FolderName
        w.Write(PackageFlags);                                   // PackageFlags
        w.Write(_names.Count); w.Write(nameOffset);
        if (ver >= EUnrealEngineObjectUE4Version.ADDED_PACKAGE_SUMMARY_LOCALIZATION_ID) w.WriteFString(string.Empty);
        if (ver >= EUnrealEngineObjectUE4Version.SERIALIZE_TEXT_IN_PACKAGES) { w.Write(0); w.Write(0); }
        w.Write(_exports.Count); w.Write(exportOffset);
        w.Write(_imports.Count); w.Write(importOffset);
        w.Write(dependsOffset);
        if (ver >= EUnrealEngineObjectUE4Version.ADD_STRING_ASSET_REFERENCES_MAP) { w.Write(0); w.Write(0); }
        if (ver >= EUnrealEngineObjectUE4Version.ADDED_SEARCHABLE_NAMES) w.Write(0);
        w.Write(0);                                              // ThumbnailTableOffset
        w.WriteGuid(MakeGuid());                                 // package Guid
        w.Write(1);                                              // Generations count
        w.Write(_exports.Count); w.Write(_names.Count);
        if (ver >= EUnrealEngineObjectUE4Version.ENGINE_VERSION_OBJECT) w.WriteEngineVersion(4, 21, 2, 0, "++UE4+Release-4.21"); else w.Write(0);
        if (ver >= EUnrealEngineObjectUE4Version.PACKAGE_SUMMARY_HAS_COMPATIBLE_ENGINE_VERSION) w.WriteEngineVersion(4, 21, 2, 0, "++UE4+Release-4.21");
        w.Write(0u);                                             // CompressionFlags
        w.Write(0);                                              // CompressedChunks count
        w.Write(0u);                                             // PackageSource
        w.Write(0);                                             // AdditionalPackagesToCook count
        w.Write(arOffset);
        w.Write((long)bulkDataStart);
        if (ver >= EUnrealEngineObjectUE4Version.WORLD_LEVEL_INFO) w.Write(0);
        if (ver >= EUnrealEngineObjectUE4Version.CHANGED_CHUNKID_TO_BE_AN_ARRAY_OF_CHUNKIDS) w.Write(0);
        else if (ver >= EUnrealEngineObjectUE4Version.ADDED_CHUNKID_TO_ASSETDATA_AND_UPACKAGE) w.Write(-1);
        if (ver >= EUnrealEngineObjectUE4Version.PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS) { w.Write(-1); w.Write(0); }
        w.Flush(); return ms.ToArray();
    }

    private static FGuid MakeGuid()
    {
        var g = FGuid16.NewGuid();
        return new FGuid(g.A, g.B, g.C, g.D);
    }
}

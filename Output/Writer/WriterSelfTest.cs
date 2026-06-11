using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Validation oracle for <see cref="UncookedPackageWriter"/>: write a package, then re-parse the
/// output through CUE4Parse and diff exports/properties against the source. Confirms the bytes are
/// structurally valid + carry the real data, without needing the editor.
/// </summary>
public static class WriterSelfTest
{
    public static bool Run(AbstractFileProvider provider, EGame game, string packagePath, string outputPath)
    {
        if (provider.LoadPackage(packagePath) is not Package source)
        {
            Log.Error("Self-test: {Path} is not a legacy Package (IoStore not supported by writer)", packagePath);
            return false;
        }

        // Concatenate .uasset + .uexp so export SerialOffsets index into the combined space.
        var parts = provider.SavePackage(packagePath);
        var uasset = parts.FirstOrDefault(p => p.Key.EndsWith(".uasset") || p.Key.EndsWith(".umap")).Value
                     ?? parts.Values.First();
        var uexp = parts.FirstOrDefault(p => p.Key.EndsWith(".uexp")).Value;
        var combined = uexp is null ? uasset : Concat(uasset, uexp);

        // DIAG: does CUE4Parse's FName.Index align with NameMap positions? Dump package imports.
        var nm = source.NameMap;
        Log.Information("DIAG NameMap[{N}] sample: 0='{A}' 1='{B}' 2='{C}'", nm.Length,
            nm.Length > 0 ? nm[0].Name : "-", nm.Length > 1 ? nm[1].Name : "-", nm.Length > 2 ? nm[2].Name : "-");
        for (var k = 0; k < source.ImportMap.Length; k++)
        {
            var imp = source.ImportMap[k];
            var ci = imp.ClassName.Index;
            var nameAtCi = ci >= 0 && ci < nm.Length ? nm[ci].Name : "<OOR>";
            Log.Information("DIAG imp[{K}] Class='{CT}'(idx={CI}->NameMap='{NA}') Outer={OI} Obj='{ON}'(idx={OBI})",
                k, imp.ClassName.Text, ci, nameAtCi, imp.OuterIndex?.Index ?? 0, imp.ObjectName.Text, imp.ObjectName.Index);
        }

        var outFile = Path.Combine(outputPath, Path.GetFileName(packagePath));
        if (!outFile.EndsWith(".uasset") && !outFile.EndsWith(".umap")) outFile += ".uasset";

        new UncookedPackageWriter(game).Write(source, combined, outFile);

        // Re-parse the written file in isolation.
        var written = File.ReadAllBytes(outFile);
        RawDumpImports(written);
        Package roundtrip;
        try
        {
            // Re-parse with the SAME engine version (the string/byte ctor would default to latest/UE5).
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(
                Path.GetFileNameWithoutExtension(outFile), written, new VersionContainer(game));
            roundtrip = new Package(ar, (CUE4Parse.UE4.Readers.FArchive?)null,
                (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                provider, false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Self-test FAILED: written package did not re-parse");
            return false;
        }

        return Diff(source, roundtrip);
    }

    private static bool Diff(Package src, Package rt)
    {
        var ok = true;
        if (src.ExportMap.Length != rt.ExportMap.Length)
        {
            Log.Error("Self-test: export count {A} != {B}", src.ExportMap.Length, rt.ExportMap.Length);
            return false;
        }

        var srcExports = ((IPackage)src).GetExports().ToList();
        var rtExports = ((IPackage)rt).GetExports().ToList();
        for (var i = 0; i < srcExports.Count; i++)
        {
            var a = srcExports[i];
            var b = rtExports[i];
            var an = PropNames(a);
            var bn = PropNames(b);
            var classMatch = a.ExportType == b.ExportType;
            var propsMatch = an.SequenceEqual(bn);
            if (!classMatch || !propsMatch)
            {
                ok = false;
                Log.Warning("Export {I} '{Name}': class {AC}/{BC}, props src=[{AP}] rt=[{BP}]",
                    i, a.Name, a.ExportType, b.ExportType, string.Join(",", an), string.Join(",", bn));
            }
            else
            {
                Log.Information("Export {I} '{Name}' ({Class}): {N} properties match", i, a.Name, a.ExportType, an.Count);
            }
        }

        Log.Information(ok ? "Self-test PASSED: round-trip matches" : "Self-test: differences found (see warnings)");
        return ok;
    }

    /// <summary>Decode the written 4.21 summary/name-table/import-map raw (as the editor reads it) to
    /// verify package imports serialize with ClassName=='Package' and OuterIndex==0.</summary>
    public static void RawDumpImports(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            string ReadFStr()
            {
                var len = r.ReadInt32();
                if (len == 0) return "";
                if (len > 0) { var b = r.ReadBytes(len); return System.Text.Encoding.ASCII.GetString(b, 0, len - 1); }
                var n = -len; var b2 = r.ReadBytes(n * 2); return System.Text.Encoding.Unicode.GetString(b2, 0, (n - 1) * 2);
            }
            r.ReadUInt32();                 // tag
            r.ReadInt32();                  // legacy -7
            r.ReadInt32();                  // ue3 864
            r.ReadInt32();                  // ue4ver 517
            r.ReadInt32();                  // licensee
            var cvCount = r.ReadInt32();
            for (var i = 0; i < cvCount; i++) { r.ReadBytes(16); r.ReadInt32(); }
            r.ReadInt32();                  // totalHeaderSize
            ReadFStr();                     // FolderName
            r.ReadUInt32();                 // packageFlags
            var nameCount = r.ReadInt32(); var nameOffset = r.ReadInt32();
            ReadFStr();                     // LocalizationId (517>=516)
            r.ReadInt32(); r.ReadInt32();   // GatherableText count/offset
            var exportCount = r.ReadInt32(); var exportOffset = r.ReadInt32();
            var importCount = r.ReadInt32(); var importOffset = r.ReadInt32();
            Log.Information("RAW: names={NC} imports={IC} exports={EC}", nameCount, importCount, exportCount);

            // Names
            ms.Position = nameOffset;
            var names = new string[nameCount];
            for (var i = 0; i < nameCount; i++) { names[i] = ReadFStr(); r.ReadUInt16(); r.ReadUInt16(); }
            string Res(int idx) => idx >= 0 && idx < nameCount ? names[idx] : "<OOR>";

            // Imports: ClassPackage(FName) ClassName(FName) OuterIndex(i32) ObjectName(FName) = 28 bytes
            var impObj = new int[importCount];
            for (var i = 0; i < importCount; i++)
            {
                ms.Position = importOffset + i * 28 + 16;     // skip ClassPackage+ClassName
                r.ReadInt32();                                 // OuterIndex
                impObj[i] = r.ReadInt32();                     // ObjectName idx
            }

            // Exports (ver 517 fixed layout = 104 bytes): ClassIndex SuperIndex TemplateIndex OuterIndex
            // ObjectName(8) Flags(4) Size(8) Off(8) 3 bools(12) Guid(16) PkgFlags(4) NotAlways(4) IsAsset(4) PreloadDeps(20)
            var exClass = new int[exportCount]; var exObj = new int[exportCount];
            for (var i = 0; i < exportCount; i++)
            {
                ms.Position = exportOffset + i * 104;
                exClass[i] = r.ReadInt32();                    // ClassIndex (FPackageIndex)
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32();   // Super, Template, Outer
                exObj[i] = r.ReadInt32();                      // ObjectName idx
            }
            string ResClass(int pkgIdx) =>
                pkgIdx == 0 ? "Class"
                : pkgIdx < 0 ? Res(impObj[-pkgIdx - 1])
                : Res(exObj[pkgIdx - 1]);

            for (var i = 0; i < exportCount; i++)
                Log.Information("RAW export[{I}] {Obj}  (class {Cls})", i, Res(exObj[i]), ResClass(exClass[i]));
        }
        catch (Exception ex) { Log.Warning(ex, "RawDumpImports failed"); }
    }

    /// <summary>Phase-1 (Tier 2 decompiler): parse a local editor .uasset and dump every export's
    /// class + property tags, so we can reverse the UBlueprint/EdGraph/K2Node serialization format.</summary>
    /// <summary>Dump each export's raw native payload as hex (offset/size from CUE4Parse) — used to
    /// reverse minimal editor UClass/BGC serialization from a known-good editor .uasset.</summary>
    public static void HexDumpExports(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { Log.Error(ex, "read failed"); return; }
        CUE4Parse.UE4.Assets.Package pkg;
        try
        {
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(Path.GetFileNameWithoutExtension(path), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new CUE4Parse.UE4.Assets.Package(ar, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        Log.Information("== {File} names:", Path.GetFileName(path));
        for (var i = 0; i < pkg.NameMap.Length; i++) Log.Information("  name[{I}] {N}", i, pkg.NameMap[i].Name);
        for (var i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            int off = (int)e.SerialOffset, size = (int)e.SerialSize;
            Log.Information("EXPORT[{I}] {Name} class={C} super={S} off={O} size={Z}", i, e.ObjectName.Text, e.ClassName, e.SuperIndex?.Index ?? 0, off, size);
            var end = Math.Min(off + size, data.Length);
            var sb = new System.Text.StringBuilder();
            for (int b = off; b < end; b++) { sb.Append(data[b].ToString("X2")); sb.Append(' '); if ((b - off) % 16 == 15) { Log.Information("    {Hex}", sb.ToString()); sb.Clear(); } }
            if (sb.Length > 0) Log.Information("    {Hex}", sb.ToString());
        }
    }

    /// <summary>Walk every UFunction's Kismet bytecode in a cooked BP and print the opcode tree (the IR we
    /// reconstruct K2 nodes from).</summary>
    public static void WalkBytecode(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { Log.Error(ex, "read failed"); return; }
        CUE4Parse.UE4.Assets.Package pkg;
        try
        {
            // A provider with ReadScriptData=true is required for UStruct to deserialize Kismet bytecode.
            var provider = new CUE4Parse.FileProvider.DefaultFileProvider(
                Path.GetDirectoryName(path)!, System.IO.SearchOption.TopDirectoryOnly, false,
                new VersionContainer(EGame.GAME_UE4_21));
            provider.ReadScriptData = true;
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(Path.GetFileNameWithoutExtension(path), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new CUE4Parse.UE4.Assets.Package(ar, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, provider, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        foreach (var fn in ((IPackage)pkg).GetExports().OfType<CUE4Parse.UE4.Objects.UObject.UFunction>())
        {
            Log.Information("== FUNCTION {Name}: {N} bytecode expr(s) ==", fn.Name, fn.ScriptBytecode?.Length ?? 0);
            if (fn.ScriptBytecode is not { Length: > 0 }) continue;
            foreach (var expr in fn.ScriptBytecode)
            {
                var node = Reconstructors.KismetWalker.WalkOne(expr);
                Log.Information("  {Json}", System.Text.Json.JsonSerializer.Serialize(node));
            }
        }
    }

    /// <summary>Probe a map's PersistentLevel: walk tagged props to None, then print the native int32s
    /// (Actors array + URL) so we can locate the Actors count/offset for grafting.</summary>
    public static void ProbeLevel(string path)
    {
        var data = File.ReadAllBytes(path.Split('|')[0]);
        CUE4Parse.UE4.Assets.Package pkg;
        try
        {
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(Path.GetFileNameWithoutExtension(path), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new CUE4Parse.UE4.Assets.Package(ar, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse"); return; }
        int NIdx(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
        NodePayloadWalker.StructPropertyIdx = NIdx("StructProperty"); NodePayloadWalker.BoolPropertyIdx = NIdx("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = NIdx("ByteProperty"); NodePayloadWalker.EnumPropertyIdx = NIdx("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = NIdx("ArrayProperty"); NodePayloadWalker.SetPropertyIdx = NIdx("SetProperty");
        NodePayloadWalker.MapPropertyIdx = NIdx("MapProperty");
        int noneIdx = NIdx("None");
        var parts = path.Split('|');
        var cls = parts.Length > 1 ? parts[1] : "Level";
        var lvl = pkg.ExportMap.First(e => e.ClassName == cls);
        var p = new byte[(int)lvl.SerialSize]; Array.Copy(data, (int)lvl.SerialOffset, p, 0, p.Length);
        int o = NodePayloadWalker.SkipTaggedProperties(p, 0, noneIdx);
        Log.Information("{File} {C}: post-None offset={O} size={S} (tail {T}B)", Path.GetFileName(parts[0]), cls, o, p.Length, p.Length - o);
        var sb = new System.Text.StringBuilder();
        for (int b = o; b < p.Length; b++) sb.Append(p[b].ToString("X2")).Append(' ');
        Log.Information("  tail hex: {V}", sb.ToString());
    }

    public static void DumpPackage(string path, EGame game = EGame.GAME_UE4_21)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { Log.Error(ex, "read failed"); return; }

        CUE4Parse.UE4.Assets.Package pkg;
        try
        {
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(
                Path.GetFileNameWithoutExtension(path), data, new VersionContainer(game));
            pkg = new CUE4Parse.UE4.Assets.Package(ar,
                (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        List<UObject> exports;
        try { exports = ((IPackage)pkg).GetExports().ToList(); }
        catch (Exception ex) { Log.Error(ex, "GetExports failed"); return; }

        Log.Information("== {File}: {N} export(s) ==  fileLen={Len} bulkStart={Bulk}",
            Path.GetFileName(path), exports.Count, data.Length, pkg.Summary.BulkDataStartOffset);
        if (Environment.GetEnvironmentVariable("DUMP_VERSIONS") == "1")
        {
            var editorGuid = CUE4Parse.UE4.Versions.FEditorObjectVersion.GUID;
            var cvs = pkg.Summary.CustomVersionContainer?.Versions;
            if (cvs != null) foreach (var cv in cvs)
                Log.Information("  CV {Guid} = {Ver}{Tag}", cv.Key, cv.Version, cv.Key == editorGuid ? "  <-- FEditorObjectVersion (RefactorMeshEditorMaterials=8)" : "");
        }
        if (Environment.GetEnvironmentVariable("DUMP_NAMES") == "1")
            for (var n = 0; n < pkg.NameMap.Length; n++) Log.Information("  NAME[{I}] {S}", n, pkg.NameMap[n].Name);
        if (Environment.GetEnvironmentVariable("DUMP_IMPORTS") == "1")
            for (var n = 0; n < pkg.ImportMap.Length; n++)
                Log.Information("  IMP[-{I}] {Cls} '{Obj}' outer={O}", n + 1, pkg.ImportMap[n].ClassName.Text,
                    pkg.ImportMap[n].ObjectName.Text, pkg.ImportMap[n].OuterIndex?.Index ?? 0);
        for (var i = 0; i < pkg.ExportMap.Length; i++)
        {
            var ex = pkg.ExportMap[i];
            Log.Information("  EXP[{I}] {Cls,-22} off={Off} size={Size} (end={End}) flags=0x{F:X}",
                i, ex.ClassName, ex.SerialOffset, ex.SerialSize, ex.SerialOffset + ex.SerialSize, (uint)ex.ObjectFlags);
        }
        if (Environment.GetEnvironmentVariable("DUMP_EXTBOUNDS_RAW") == "1")
        {
            var extIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "ExtendedBounds");
            var structIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "StructProperty");
            var boxIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "BoxSphereBounds");
            foreach (var ex in pkg.ExportMap.Where(e => e.ClassName == "StaticMesh"))
            {
                var start = (int)ex.SerialOffset;
                var end = start + (int)ex.SerialSize;
                Log.Information("  RAW ExtendedBounds idx={Ext} StructProperty={Struct} BoxSphereBounds={Box}", extIdx, structIdx, boxIdx);
                for (var p = start; p + 80 < end; p++)
                {
                    if (BitConverter.ToInt32(data, p) != extIdx || BitConverter.ToInt32(data, p + 8) != structIdx) continue;
                    var size = BitConverter.ToInt32(data, p + 16);
                    var structName = BitConverter.ToInt32(data, p + 24);
                    var hasGuid = data[p + 48];
                    Log.Information("  RAW ExtendedBounds @{Rel}: size={Size} structNameIdx={StructName} hasGuid={HasGuid} bytes={Bytes}",
                        p - start, size, structName, hasGuid, BitConverter.ToString(data, p, Math.Min(128, end - p)));
                }
            }
        }
        for (var i = 0; i < exports.Count; i++)
        {
            var e = exports[i];
            Log.Information("EXPORT[{I}] {Name} : {Type}  ({P} props)", i, e.Name, e.ExportType, e.Properties.Count);
            foreach (var p in e.Properties) DumpProp(p, "    ");
        }
    }

    public static void CompareStaticMeshes(string leftPath, string rightPath, EGame game = EGame.GAME_UE4_21)
    {
        var left = LoadStaticMeshForCompare(leftPath, game);
        var right = LoadStaticMeshForCompare(rightPath, game);
        if (left is null || right is null) return;

        Log.Information("== StaticMesh compare ==");
        Log.Information("LEFT  {Path}", Path.GetFullPath(leftPath));
        Log.Information("RIGHT {Path}", Path.GetFullPath(rightPath));
        Log.Information("Package: fileLen {L} -> {R}, bulkStart {LB} -> {RB}",
            left.Data.Length, right.Data.Length, left.Package.Summary.BulkDataStartOffset, right.Package.Summary.BulkDataStartOffset);
        Log.Information("Export: size {L} -> {R}, flags 0x{LF:X} -> 0x{RF:X}",
            left.Export.SerialSize, right.Export.SerialSize, (uint)left.Export.ObjectFlags, (uint)right.Export.ObjectFlags);

        DumpMeshSummary("LEFT ", left.Mesh);
        DumpMeshSummary("RIGHT", right.Mesh);
        DumpPropDiff(left.Mesh, right.Mesh, "bAutoComputeLODScreenSize");
        DumpPropDiff(left.Mesh, right.Mesh, "PositiveBoundsExtension");
        DumpPropDiff(left.Mesh, right.Mesh, "NegativeBoundsExtension");
        DumpPropDiff(left.Mesh, right.Mesh, "SectionInfoMap");
        DumpPropDiff(left.Mesh, right.Mesh, "OriginalSectionInfoMap");

        var leftPayload = SliceExport(left.Data, left.Export);
        var rightPayload = SliceExport(right.Data, right.Export);
        Log.Information("Payload SHA256: {L} -> {R}", Sha256(leftPayload), Sha256(rightPayload));
        Log.Information("Payload tail LEFT : {Tail}", HexTail(leftPayload, 96));
        Log.Information("Payload tail RIGHT: {Tail}", HexTail(rightPayload, 96));
    }

    private sealed record MeshCompareInfo(byte[] Data, Package Package, CUE4Parse.UE4.Objects.UObject.FObjectExport Export, UStaticMesh Mesh);

    private static MeshCompareInfo? LoadStaticMeshForCompare(string path, EGame game)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(Path.GetFileNameWithoutExtension(path), data, new VersionContainer(game));
            var pkg = new Package(ar, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                (CUE4Parse.UE4.Readers.FArchive?)null, (IFileProvider?)null, false);
            for (var i = 0; i < pkg.ExportMap.Length; i++)
            {
                if (pkg.ExportMap[i].ClassName != "StaticMesh") continue;
                if (pkg.ExportsLazy[i].Value is UStaticMesh sm)
                    return new MeshCompareInfo(data, pkg, pkg.ExportMap[i], sm);
            }
            Log.Error("No StaticMesh export in {Path}", path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load StaticMesh {Path}", path);
        }
        return null;
    }

    private static void DumpMeshSummary(string label, UStaticMesh sm)
    {
        var rd = sm.RenderData;
        Log.Information("{Label}: cooked={Cooked}, props={Props}, staticMaterials={Mats}, LODs={Lods}",
            label, sm.bCooked, sm.Properties.Count, sm.StaticMaterials?.Length ?? 0, rd?.LODs?.Length ?? 0);
        if (rd?.Bounds is { } b)
        {
            Log.Information("{Label}: bounds origin=({OX},{OY},{OZ}) extent=({EX},{EY},{EZ}) sphere={R}",
                label, b.Origin.X, b.Origin.Y, b.Origin.Z, b.BoxExtent.X, b.BoxExtent.Y, b.BoxExtent.Z, b.SphereRadius);
        }
        if (rd?.ScreenSize is { Length: > 0 } ss)
            Log.Information("{Label}: screenSize=[{S}]", label, string.Join(", ", ss.Select(x => x.ToString("G6"))));
        var lod0 = rd?.LODs?.FirstOrDefault();
        if (lod0 != null)
            Log.Information("{Label}: LOD0 sections={Sections}, sourceBounds={SourceBounds}",
                label, lod0.Sections?.Length ?? 0,
                lod0.SourceMeshBounds is null ? "<null>" :
                    $"origin=({lod0.SourceMeshBounds.Origin.X},{lod0.SourceMeshBounds.Origin.Y},{lod0.SourceMeshBounds.Origin.Z}) extent=({lod0.SourceMeshBounds.BoxExtent.X},{lod0.SourceMeshBounds.BoxExtent.Y},{lod0.SourceMeshBounds.BoxExtent.Z}) sphere={lod0.SourceMeshBounds.SphereRadius}");
    }

    private static void DumpPropDiff(UStaticMesh left, UStaticMesh right, string prop)
    {
        var l = left.Properties.FirstOrDefault(p => p.Name.Text == prop)?.Tag?.ToString() ?? "<missing>";
        var r = right.Properties.FirstOrDefault(p => p.Name.Text == prop)?.Tag?.ToString() ?? "<missing>";
        if (l != r) Log.Information("PROP {Prop}: {L} -> {R}", prop, l, r);
    }

    private static byte[] SliceExport(byte[] data, CUE4Parse.UE4.Objects.UObject.FObjectExport export)
    {
        var payload = new byte[(int)export.SerialSize];
        Array.Copy(data, (int)export.SerialOffset, payload, 0, payload.Length);
        return payload;
    }

    private static string Sha256(byte[] data)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    private static string HexTail(byte[] data, int max)
    {
        var start = Math.Max(0, data.Length - max);
        return BitConverter.ToString(data, start, data.Length - start);
    }

    /// <summary>Dump an already-loaded package (works for BOTH legacy Package and UE5 IoPackage/Zen — only the
    /// generic IPackage surface is used). Proves unversioned-property reads when a usmap is mounted.</summary>
    public static void DumpLoadedPackage(CUE4Parse.UE4.Assets.IPackage pkg)
    {
        List<UObject> exports;
        try { exports = pkg.GetExports().ToList(); }
        catch (Exception ex) { Log.Error(ex, "GetExports failed"); return; }
        Log.Information("== {Name}: {N} export(s) ==", pkg.Name, exports.Count);
        for (var i = 0; i < exports.Count; i++)
        {
            var e = exports[i];
            Log.Information("EXPORT[{I}] {Name} : {Type}  ({P} props)", i, e.Name, e.ExportType, e.Properties.Count);
            foreach (var p in e.Properties) DumpProp(p, "    ");
        }
    }

    private static void DumpProp(CUE4Parse.UE4.Assets.Objects.FPropertyTag p, string indent)
        => DumpValue($".{p.Name.Text} ({p.PropertyType.Text})", p.Tag, indent);

    private static void DumpValue(string label, CUE4Parse.UE4.Assets.Objects.Properties.FPropertyTagType? tag, string indent)
    {
        object? gv = null;
        try { gv = tag?.GenericValue; } catch { }
        if (gv is CUE4Parse.UE4.Assets.Objects.FScriptStruct ss) gv = ss.StructType;
        if (gv is CUE4Parse.UE4.Assets.Objects.FStructFallback sf)
        {
            Log.Information("{I}{L} = struct {{", indent, label);
            foreach (var sp in sf.Properties) DumpProp(sp, indent + "  ");
            Log.Information("{I}}}", indent);
            return;
        }
        if (gv is CUE4Parse.UE4.Assets.Objects.UScriptArray arr)
        {
            Log.Information("{I}{L} = [{N}] {{", indent, label, arr.Properties.Count);
            for (var i = 0; i < arr.Properties.Count; i++) DumpValue($"[{i}]", arr.Properties[i], indent + "  ");
            Log.Information("{I}}}", indent);
            return;
        }
        if (gv is CUE4Parse.UE4.Assets.Objects.UScriptMap map)
        {
            Log.Information("{I}{L} = map[{N}] {{", indent, label, map.Properties.Count);
            int k = 0;
            foreach (var kv in map.Properties)
            {
                DumpValue($"key[{k}]", kv.Key, indent + "  ");
                DumpValue($"val[{k}]", kv.Value, indent + "  ");
                k++;
            }
            Log.Information("{I}}}", indent);
            return;
        }
        if (gv is CUE4Parse.UE4.Objects.Engine.EdGraph.FEdGraphPinType pt)
        {
            Log.Information("{I}{L} = PinType {{ Category={C} Sub={S} SubObj={O} Container={Ct} bRef={R} bConst={K} bWeak={W} bWrap={U} }}",
                indent, label, pt.PinCategory.Text, pt.PinSubCategory.Text, pt.PinSubCategoryObject?.Index ?? 0,
                pt.ContainerType, pt.bIsReference, pt.bIsConst, pt.bIsWeakPointer, pt.bIsUObjectWrapper);
            return;
        }
        string val;
        try { val = tag?.ToString() ?? "<null>"; } catch { val = "<err>"; }
        if (val.Length > 120) val = val[..120] + "…";
        Log.Information("{I}{L} = {V}", indent, label, val);
    }

    /// <summary>Phase-2 round-trip: parse a LOCAL editor .uasset and re-emit it through the uncooked
    /// writer (export payloads — incl. native-serialized EdGraph pins — copied verbatim). If the result
    /// opens in-editor, the writer already handles editor-graph packages.</summary>
    public static void ReEmit(string path, string outDir)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { Log.Error(ex, "read failed"); return; }

        CUE4Parse.UE4.Assets.Package pkg;
        try
        {
            var ar = new CUE4Parse.UE4.Readers.FByteArchive(
                Path.GetFileNameWithoutExtension(path), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new CUE4Parse.UE4.Assets.Package(ar,
                (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, Path.GetFileName(path));
        try
        {
            // The local editor .uasset is single-file: export SerialOffsets index into the file bytes,
            // so the whole file IS the combined payload stream.
            var result = new UncookedPackageWriter(EGame.GAME_UE4_21).Write(pkg, data, outFile);
            Log.Information("Re-emitted {In} -> {Out} (bCooked={BC})", Path.GetFileName(path), outFile, result.BCookedPatched);
        }
        catch (Exception ex) { Log.Error(ex, "re-emit failed"); }
    }

    private static List<string> PropNames(UObject o) => o.Properties.Select(p => p.Name.Text).ToList();

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Array.Copy(a, r, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }
}

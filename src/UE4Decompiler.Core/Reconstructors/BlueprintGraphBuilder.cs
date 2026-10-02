using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Serilog;
using UE4Decompiler.Output.Writer;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// [Tier2 Phase 4 — module #4] Builds an editor-openable blueprint from a cooked BP by REUSING the cooked
/// exports verbatim (BGC/CDO/functions — their UClass native serialization + bytecode) and APPENDING the
/// synthesized editor exports the cooker stripped: UBlueprint + EventGraph + a K2Node_Event(BeginPlay)
/// with an exec pin. Append-only, so all cooked FName indices/numbers and FPackageIndex refs stay valid.
///
/// First milestone: a single Event node visible in the graph. Wires/CallFunction come next.
/// </summary>
public static class BlueprintGraphBuilder
{
    /// <summary>Clone an editor .uasset verbatim through SynthPackageWriter (names+imports+exports copied
    /// 1:1) and report byte-diff vs the original. Proves the emitter can faithfully reproduce a
    /// known-loadable editor blueprint before we start mutating its graph.</summary>
    public static void Clone(string editorPath, string outDir)
    {
        var data = File.ReadAllBytes(editorPath);
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(editorPath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        var name = Path.GetFileNameWithoutExtension(editorPath);
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, "/Game/" + name);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions
            ?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(n.Name ?? "None");
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);
        foreach (var e in pkg.ExportMap)
        {
            var off = (int)e.SerialOffset; var size = (int)e.SerialSize;
            var payload = new byte[size];
            Array.Copy(data, off, payload, 0, size);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                payload, (uint)e.ObjectFlags, e.IsAsset);
        }
        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, name + ".uasset");
        spw.Write(outFile);

        var orig = data; var clone = File.ReadAllBytes(outFile);
        int firstDiff = -1, diffs = 0;
        int min = Math.Min(orig.Length, clone.Length);
        for (int i = 0; i < min; i++) if (orig[i] != clone[i]) { if (firstDiff < 0) firstDiff = i; diffs++; }
        Log.Information("Clone {Name}: orig={O}B clone={C}B firstDiff={F} totalDiffBytes={D}", name, orig.Length, clone.Length, firstDiff, diffs + Math.Abs(orig.Length - clone.Length));
        if (firstDiff >= 0)
        {
            int s = Math.Max(0, firstDiff - 4), e2 = Math.Min(min, firstDiff + 16);
            var ob = new System.Text.StringBuilder(); var cb = new System.Text.StringBuilder();
            for (int i = s; i < e2; i++) { ob.Append(orig[i].ToString("X2")).Append(' '); cb.Append(clone[i].ToString("X2")).Append(' '); }
            Log.Information("  @ {S}: orig {O}", s, ob.ToString());
            Log.Information("  @ {S}: clon {C}", s, cb.ToString());
        }
    }

    /// <summary>Template-based reconstruction: clone a known-loadable editor BP verbatim, then SYNTHESIZE a
    /// new K2Node_Event and inject it into the EventGraph (append export + patch the Nodes array). Exercises
    /// the whole emit path (TaggedPropertyWriter + PinSerializer + SynthPackageWriter + payload patch) and
    /// proves we can add reconstructed nodes to a graph that the editor will open. eventName must be an
    /// override event the parent class exposes (e.g. ReceiveEndPlay on Actor).</summary>
    public static void InjectEvent(string editorPath, string outDir, string eventName)
    {
        var data = File.ReadAllBytes(editorPath);
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(editorPath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        var name = Path.GetFileNameWithoutExtension(editorPath);
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, "/Game/" + name);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions
            ?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(n.Name ?? "None");
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        // Locate the EventGraph export, an existing K2Node_Event (to copy its ClassIndex), and resolve the
        // Actor class import (MemberParent for the EventReference).
        int egIdx = Array.FindIndex(pkg.ExportMap, e => e.ObjectName.Text == "EventGraph" && e.ClassName == "EdGraph");
        int evTemplate = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "K2Node_Event");
        if (egIdx < 0 || evTemplate < 0) { Log.Error("template missing EventGraph/K2Node_Event"); return; }
        int k2EventClassIdx = pkg.ExportMap[evTemplate].ClassIndex?.Index ?? 0;     // FPackageIndex of K2Node_Event class import
        int actorImport = Array.FindIndex(pkg.ImportMap, i => i.ObjectName.Text == "Actor");
        int actorPkgIdx = actorImport >= 0 ? -(actorImport + 1) : 0;

        int newNodePkg = pkg.ExportMap.Length + 1;     // FPackageIndex the injected node will get
        int egPkgIdx = egIdx + 1;
        int nodesNameIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "Nodes");
        int arrayPropNameIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "ArrayProperty");

        // Clone all exports verbatim, patching only the EventGraph's Nodes array.
        for (var i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            var off = (int)e.SerialOffset; var size = (int)e.SerialSize;
            var payload = new byte[size];
            Array.Copy(data, off, payload, 0, size);
            if (i == egIdx) payload = PatchNodesArray(payload, nodesNameIdx, arrayPropNameIdx, newNodePkg);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                payload, (uint)e.ObjectFlags, e.IsAsset);
        }

        // Synthesize the new K2Node_Event (override event on Actor) and append it (outer = EventGraph).
        var evPayload = BuildEventNode2(spw, ownerPkg: newNodePkg, actorClass: actorPkgIdx, eventName: eventName, nodePosY: 400);
        spw.AddExportRaw(spw.Name("K2Node_Event"), 9 /*FName number → K2Node_Event_8, unique*/,
            k2EventClassIdx, 0, 0, egPkgIdx, evPayload, objectFlags: 0x1, isAsset: false);

        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, name + ".uasset");
        spw.Write(outFile);
        Log.Information("Injected K2Node_Event({Ev}) into {Name} (newNode pkgidx {P}, EventGraph export[{E}])", eventName, name, newNodePkg, egIdx);

        // Self-verify: re-parse and dump the EventGraph + new node.
        WriterSelfTest.DumpPackage(outFile);
    }

    /// <summary>Append one FPackageIndex element to a Nodes ArrayProperty value (count+1, Size+4, splice).</summary>
    private static byte[] PatchNodesArray(byte[] p, int nodesNameIdx, int arrayPropNameIdx, int newElemPkgIdx, int noneIdx = -1)
    {
        if (NodePayloadWalker.IsUe5 && noneIdx >= 0)
        {
            if (!NodePayloadWalker.FindTag5(p, noneIdx, nodesNameIdx, arrayPropNameIdx,
                    out _, out int sizePos5, out int valueOff5, out int tagEnd5))
            { Log.Warning("Nodes array tag not found; injecting unreferenced node"); return p; }
            int count5 = BitConverter.ToInt32(p, valueOff5);
            var outp5 = new byte[p.Length + 4];
            Array.Copy(p, 0, outp5, 0, tagEnd5);
            BitConverter.GetBytes(newElemPkgIdx).CopyTo(outp5, tagEnd5);
            Array.Copy(p, tagEnd5, outp5, tagEnd5 + 4, p.Length - tagEnd5);
            BitConverter.GetBytes(count5 + 1).CopyTo(outp5, valueOff5);
            BitConverter.GetBytes(BitConverter.ToInt32(p, sizePos5) + 4).CopyTo(outp5, sizePos5);
            Log.Information("Patched Nodes array: count {A}->{B}, +1 elem (pkgidx {E})", count5, count5 + 1, newElemPkgIdx);
            return outp5;
        }
        int tag = -1;
        for (int i = 0; i + 16 <= p.Length; i++)
        {
            if (BitConverter.ToInt32(p, i) == nodesNameIdx && BitConverter.ToInt32(p, i + 4) == 0 &&
                BitConverter.ToInt32(p, i + 8) == arrayPropNameIdx && BitConverter.ToInt32(p, i + 12) == 0)
            { tag = i; break; }
        }
        if (tag < 0) { Log.Warning("Nodes array tag not found; injecting unreferenced node"); return p; }
        int sizePos = tag + 16;          // Size int32
        int countPos = tag + 33;         // count int32 (after Size,ArrayIndex,InnerType FName(8),HasGuid(1))
        int count = BitConverter.ToInt32(p, countPos);
        int insertPos = countPos + 4 + count * 4;   // after the last element
        var outp = new byte[p.Length + 4];
        Array.Copy(p, 0, outp, 0, insertPos);
        BitConverter.GetBytes(newElemPkgIdx).CopyTo(outp, insertPos);
        Array.Copy(p, insertPos, outp, insertPos + 4, p.Length - insertPos);
        BitConverter.GetBytes(count + 1).CopyTo(outp, countPos);
        BitConverter.GetBytes(BitConverter.ToInt32(p, sizePos) + 4).CopyTo(outp, sizePos);
        Log.Information("Patched Nodes array: count {A}->{B}, +1 elem (pkgidx {E})", count, count + 1, newElemPkgIdx);
        return outp;
    }

    private static byte[] BuildEventNode2(SynthPackageWriter spw, int ownerPkg, int actorClass, string eventName, int nodePosY)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Struct("EventReference", "MemberReference", () =>
        {
            var inner = new TaggedPropertyWriter(w, spw.Name);
            inner.Object("MemberParent", actorClass);
            inner.Name("MemberName", eventName);
            inner.WriteNone();
        });
        t.Bool("bOverrideFunction", true);
        t.Int("NodePosY", nodePosY);
        t.GuidStruct("NodeGuid", FGuid16.NewGuid());
        t.WriteNone();
        var pins = new PinSerializer(w, spw.Name);
        // Only the exec "then" output pin (byte-verified against ground-truth). The editor regenerates
        // the event's data pins (e.g. EndPlayReason) from the EventReference signature via AllocateDefaultPins.
        pins.WriteOwningPins(new[]
        {
            new SynthPin { OwningNodePkg = ownerPkg, PinName = "then", Category = "exec", SubCategory = "None", Direction = 1 }
        });
        w.Flush(); return ms.ToArray();
    }

    /// <summary>Produce an openable editor BP for a cooked target by RESKINNING a known-loadable editor BP
    /// template: clone it verbatim (loadable BGC/CDO/SCS/EventGraph) but rename the package+class+CDO names
    /// to the target. Faithful when the target's graph is trivial (e.g. BP_HandProxyExample, whose BeginPlay
    /// is empty boilerplate). Names are remapped by exact match so unrelated names are untouched.</summary>
    public static void Reskin(string templatePath, string outDir, string targetShort, string targetPackagePath)
    {
        var data = File.ReadAllBytes(templatePath);
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(templatePath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        var oldPath = pkg.NameMap[0].Name ?? "";                  // e.g. /Game/Maps/base
        var oldShort = oldPath.Contains('/') ? oldPath[(oldPath.LastIndexOf('/') + 1)..] : oldPath;  // base
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [oldPath] = targetPackagePath,
            [oldPath + "." + oldShort] = targetPackagePath + "." + targetShort,
            [oldPath + "." + oldShort + "_C"] = targetPackagePath + "." + targetShort + "_C",
            [oldShort] = targetShort,
            [oldShort + "_C"] = targetShort + "_C",
            ["Default__" + oldShort + "_C"] = "Default__" + targetShort + "_C",
        };

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(map.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);
        foreach (var e in pkg.ExportMap)
        {
            var off = (int)e.SerialOffset; var size = (int)e.SerialSize;
            var payload = new byte[size];
            Array.Copy(data, off, payload, 0, size);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                payload, (uint)e.ObjectFlags, e.IsAsset);
        }
        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, targetShort + ".uasset");
        spw.Write(outFile);
        Log.Information("Reskinned {Old} -> {New} ({Path}) -> {Out}", oldShort, targetShort, targetPackagePath, outFile);
    }

    /// <summary>Inject a reconstructed K2Node_CallFunction (PrintString) wired off the template's real
    /// BeginPlay event: synthesize the CallFunction export, patch BeginPlay's "then" pin LinkedTo to it,
    /// and add it to the EventGraph Nodes. Proves CallFunction + exec-wire reconstruction end to end.</summary>
    public static void InjectWiredCall(string editorPath, string outDir, string message)
    {
        var data = File.ReadAllBytes(editorPath);
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(editorPath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        int NIdx(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
        int noneIdx = NIdx("None"), thenIdx = NIdx("then"), beginPlayIdx = NIdx("ReceiveBeginPlay");
        // configure the payload walker's property-type indices
        NodePayloadWalker.StructPropertyIdx = NIdx("StructProperty");
        NodePayloadWalker.BoolPropertyIdx = NIdx("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = NIdx("ByteProperty");
        NodePayloadWalker.EnumPropertyIdx = NIdx("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = NIdx("ArrayProperty");
        NodePayloadWalker.SetPropertyIdx = NIdx("SetProperty");
        NodePayloadWalker.MapPropertyIdx = NIdx("MapProperty");

        // Locate the BeginPlay K2Node_Event export (payload contains FName(ReceiveBeginPlay,0)).
        var bpSig = new byte[8]; BitConverter.GetBytes(beginPlayIdx).CopyTo(bpSig, 0);
        int bpExport = -1;
        for (int i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            if (e.ClassName != "K2Node_Event") continue;
            var seg = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, seg, 0, seg.Length);
            if (IndexOf(seg, bpSig) >= 0) { bpExport = i; break; }
        }
        if (bpExport < 0) { Log.Error("BeginPlay event not found"); return; }
        int bpPkg = bpExport + 1;

        // Walk the BeginPlay payload: find its "then" pin (PinId + LinkedTo offset).
        var bpData = new byte[(int)pkg.ExportMap[bpExport].SerialSize];
        Array.Copy(data, (int)pkg.ExportMap[bpExport].SerialOffset, bpData, 0, bpData.Length);
        int pinStart = NodePayloadWalker.SkipTaggedProperties(bpData, 0, noneIdx);
        var pins = NodePayloadWalker.WalkPins(bpData, pinStart);
        var thenPin = pins.FirstOrDefault(p => p.PinNameIdx == thenIdx);
        if (thenPin == null) { Log.Error("BeginPlay 'then' pin not found ({N} pins)", pins.Count); return; }
        if (thenPin.LinkedToCount != 0) Log.Warning("BeginPlay 'then' already has {N} links", thenPin.LinkedToCount);

        // Set up the writer (clone names/imports/exports, preserving CVs).
        var name = Path.GetFileNameWithoutExtension(editorPath);
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, "/Game/" + name);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(n.Name ?? "None");
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        int egIdx = Array.FindIndex(pkg.ExportMap, e => e.ObjectName.Text == "EventGraph" && e.ClassName == "EdGraph");
        int callPkg = pkg.ExportMap.Length + 1;     // FPackageIndex the CallFunction will get
        int nodesNameIdx = NIdx("Nodes"), arrPropIdx = NIdx("ArrayProperty");

        // PinIds: BeginPlay.then already has one (thenPin.PinId); our execute pin gets a fresh id.
        var execPinId = FGuid16.NewGuid();

        // Clone exports, patching BeginPlay's then-pin LinkedTo to point at our new execute pin.
        for (int i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            if (i == bpExport)
                payload = PatchLinkedTo(payload, thenPin.LinkedToCountOffset, callPkg, execPinId);
            else if (i == egIdx)
                payload = PatchNodesArray(payload, nodesNameIdx, arrPropIdx, callPkg);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                payload, (uint)e.ObjectFlags, e.IsAsset);
        }

        // Imports for the CallFunction: K2Node_CallFunction class + KismetSystemLibrary class.
        int bgPkgImp = FindPackageImport(pkg, "/Script/BlueprintGraph");
        int enginePkgImp = FindPackageImport(pkg, "/Script/Engine");
        int impCallFunc = spw.AddImport("/Script/CoreUObject", "Class", bgPkgImp, "K2Node_CallFunction");
        int impKSL = spw.AddImport("/Script/CoreUObject", "Class", enginePkgImp, "KismetSystemLibrary");

        // Synthesize the CallFunction payload (FunctionReference=KismetSystemLibrary:PrintString) + pins.
        var callPayload = BuildCallFunction(spw, ownerPkg: callPkg, funcClassImport: impKSL, funcName: "PrintString",
            beginPlayPkg: bpPkg, beginPlayThenPinId: thenPin.PinId, execPinId: execPinId, defaultMessage: message);
        spw.AddExportRaw(spw.Name("K2Node_CallFunction"), 7, impCallFunc, 0, 0, egIdx + 1, callPayload, 0x1, false);

        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, name + ".uasset");
        spw.Write(outFile);
        Log.Information("Injected wired CallFunction(PrintString \"{M}\") off BeginPlay -> {Out}", message, outFile);
        WriterSelfTest.DumpPackage(outFile);
    }

    private static int FindPackageImport(Package pkg, string pkgPath)
    {
        for (int i = 0; i < pkg.ImportMap.Length; i++)
            if (pkg.ImportMap[i].ObjectName.Text == pkgPath && pkg.ImportMap[i].ClassName.Text == "Package")
                return -(i + 1);
        return 0;
    }

    /// <summary>Find a template import matching (class package/class, outer, object name) so synthesized
    /// references REUSE the template's import instead of appending a duplicate. Duplicate FObjectImports
    /// for an already-imported class confuse the loader's import maps (check(false) on open).</summary>
    public static int FindTemplateImport(Package tpl, string classPkg, string className, int outerIdx, string objName)
    {
        for (int i = 0; i < tpl.ImportMap.Length; i++)
        {
            var imp = tpl.ImportMap[i];
            if ((imp.OuterIndex?.Index ?? 0) != outerIdx) continue;
            if (imp.ObjectName.Text != objName || imp.ClassName.Text != className) continue;
            if (imp.ClassPackage.Text != classPkg) continue;
            return -(i + 1);
        }
        return 0;
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        { bool ok = true; for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; } if (ok) return i; }
        return -1;
    }

    /// <summary>Patch a pin's LinkedTo array (count 0 -> 1) with one ref to (peerNodePkg, peerPinId).</summary>
    private static byte[] PatchLinkedTo(byte[] p, int linkedCountOffset, int peerNodePkg, FGuid16 peerPinId)
    {
        var entry = new byte[24];
        // bNullPtr=0, OwningNode=peerNodePkg, PinId=peerPinId
        BitConverter.GetBytes(peerNodePkg).CopyTo(entry, 4);
        BitConverter.GetBytes(peerPinId.A).CopyTo(entry, 8);
        BitConverter.GetBytes(peerPinId.B).CopyTo(entry, 12);
        BitConverter.GetBytes(peerPinId.C).CopyTo(entry, 16);
        BitConverter.GetBytes(peerPinId.D).CopyTo(entry, 20);
        int insertAt = linkedCountOffset + 4;
        var outp = new byte[p.Length + 24];
        Array.Copy(p, 0, outp, 0, insertAt);
        entry.CopyTo(outp, insertAt);
        Array.Copy(p, insertAt, outp, insertAt + 24, p.Length - insertAt);
        BitConverter.GetBytes(1).CopyTo(outp, linkedCountOffset);   // count 0 -> 1
        Log.Information("Patched BeginPlay then-pin LinkedTo -> node {P}", peerNodePkg);
        return outp;
    }

    private static byte[] BuildCallFunction(SynthPackageWriter spw, int ownerPkg, int funcClassImport, string funcName,
        int beginPlayPkg, byte[] beginPlayThenPinId, FGuid16 execPinId, string defaultMessage)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Struct("FunctionReference", "MemberReference", () =>
        {
            var inner = new TaggedPropertyWriter(w, spw.Name);
            inner.Object("MemberParent", funcClassImport);
            inner.Name("MemberName", funcName);
            inner.WriteNone();
        });
        t.Int("NodePosX", 464);
        t.Int("NodePosY", 64);
        t.GuidStruct("NodeGuid", FGuid16.NewGuid());
        t.WriteNone();

        var beginPlayThen = new FGuid16(
            BitConverter.ToUInt32(beginPlayThenPinId, 0), BitConverter.ToUInt32(beginPlayThenPinId, 4),
            BitConverter.ToUInt32(beginPlayThenPinId, 8), BitConverter.ToUInt32(beginPlayThenPinId, 12));
        var execPin = new SynthPin { OwningNodePkg = ownerPkg, PinId = execPinId, PinName = "execute", Category = "exec", SubCategory = "None", Direction = 0 };
        execPin.LinkedTo.Add((beginPlayPkg, beginPlayThen));   // wire back to BeginPlay.then
        var thenOut = new SynthPin { OwningNodePkg = ownerPkg, PinName = "then", Category = "exec", SubCategory = "None", Direction = 1 };
        var pinW = new PinSerializer(w, spw.Name);
        pinW.WriteOwningPins(new[] { execPin, thenOut });
        w.Flush(); return ms.ToArray();
    }

    /// <summary>Reconstruct a cooked BP's BeginPlay graph onto a loadable template: decompile its
    /// ExecuteUbergraph bytecode into the ordered impure CallFunction sequence, reskin the template to the
    /// target name, and emit BeginPlay -> call0 -> call1 -> ... wired by exec. Pure/data calls and control
    /// flow are skipped (the editor regenerates data pins). First end-to-end Pavlov graph decompile.</summary>
    public static void ReconstructChain(string cookedPath, string templatePath, string outDir, string targetShort, string targetPackagePath)
    {
        // 1) Decompile the cooked ubergraph -> ordered (pkg, class, func) calls.
        var calls = ExtractExecChain(cookedPath);
        Log.Information("Decompiled {N} exec calls: {Calls}", calls.Count, string.Join(" -> ", calls.Select(c => c.func)));
        if (calls.Count == 0) { Log.Warning("no exec calls extracted; nothing to reconstruct"); return; }

        // 2) Parse template, set up writer with rename + preserved CVs.
        var data = File.ReadAllBytes(templatePath);
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(templatePath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "parse failed"); return; }

        int NIdx(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
        int noneIdx = NIdx("None"), thenIdx = NIdx("then"), beginPlayIdx = NIdx("ReceiveBeginPlay");
        NodePayloadWalker.StructPropertyIdx = NIdx("StructProperty"); NodePayloadWalker.BoolPropertyIdx = NIdx("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = NIdx("ByteProperty"); NodePayloadWalker.EnumPropertyIdx = NIdx("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = NIdx("ArrayProperty"); NodePayloadWalker.SetPropertyIdx = NIdx("SetProperty");
        NodePayloadWalker.MapPropertyIdx = NIdx("MapProperty");

        var oldPath = pkg.NameMap[0].Name ?? "";
        var oldShort = oldPath.Contains('/') ? oldPath[(oldPath.LastIndexOf('/') + 1)..] : oldPath;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [oldPath] = targetPackagePath,
            [oldPath + "." + oldShort] = targetPackagePath + "." + targetShort,
            [oldPath + "." + oldShort + "_C"] = targetPackagePath + "." + targetShort + "_C",
            [oldShort] = targetShort, [oldShort + "_C"] = targetShort + "_C",
            ["Default__" + oldShort + "_C"] = "Default__" + targetShort + "_C",
        };

        // Find BeginPlay event + its then-pin.
        var bpSig = new byte[8]; BitConverter.GetBytes(beginPlayIdx).CopyTo(bpSig, 0);
        int bpExport = -1;
        for (int i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            if (e.ClassName != "K2Node_Event") continue;
            var seg = new byte[(int)e.SerialSize]; Array.Copy(data, (int)e.SerialOffset, seg, 0, seg.Length);
            if (IndexOf(seg, bpSig) >= 0) { bpExport = i; break; }
        }
        if (bpExport < 0) { Log.Error("BeginPlay event not found in template"); return; }
        var bpData = new byte[(int)pkg.ExportMap[bpExport].SerialSize];
        Array.Copy(data, (int)pkg.ExportMap[bpExport].SerialOffset, bpData, 0, bpData.Length);
        int pinStart = NodePayloadWalker.SkipTaggedProperties(bpData, 0, noneIdx);
        var thenPin = NodePayloadWalker.WalkPins(bpData, pinStart).FirstOrDefault(p => p.PinNameIdx == thenIdx);
        if (thenPin == null) { Log.Error("BeginPlay then-pin not found"); return; }

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        int egIdx = Array.FindIndex(pkg.ExportMap, e => e.ObjectName.Text == "EventGraph" && e.ClassName == "EdGraph");
        int nodesNameIdx = NIdx("Nodes"), arrPropIdx = NIdx("ArrayProperty");
        int baseExport = pkg.ExportMap.Length;                  // first appended export index
        var callPkgs = Enumerable.Range(0, calls.Count).Select(i => baseExport + i + 1).ToArray();
        var execIds = calls.Select(_ => FGuid16.NewGuid()).ToArray();   // each call's "execute" pin id
        var thenIds = calls.Select(_ => FGuid16.NewGuid()).ToArray();   // each call's "then" pin id

        // Clone exports: BeginPlay.then -> call0.execute; EventGraph.Nodes += all calls.
        for (int i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            if (i == bpExport) payload = PatchLinkedTo(payload, thenPin.LinkedToCountOffset, callPkgs[0], execIds[0]);
            else if (i == egIdx) foreach (var cp in callPkgs) payload = PatchNodesArray(payload, nodesNameIdx, arrPropIdx, cp);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                payload, (uint)e.ObjectFlags, e.IsAsset);
        }

        // Imports: K2Node_CallFunction class + each unique function class.
        int bgPkgImp = FindPackageImport(pkg, "/Script/BlueprintGraph");
        int impCallFunc = spw.AddImport("/Script/CoreUObject", "Class", bgPkgImp, "K2Node_CallFunction");
        var pkgImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var classImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
            int ClassImport(string scriptPkg, string cls)
            {
                if (string.IsNullOrWhiteSpace(scriptPkg) || string.IsNullOrWhiteSpace(cls)) return 0;
                var key = scriptPkg + "." + cls;
                if (classImpCache.TryGetValue(key, out var c)) return c;
            if (!pkgImpCache.TryGetValue(scriptPkg, out var pkgImp))
            {
                pkgImp = FindPackageImport(pkg, scriptPkg);
                if (pkgImp == 0) pkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, scriptPkg);
                pkgImpCache[scriptPkg] = pkgImp;
            }
            c = spw.AddImport("/Script/CoreUObject", "Class", pkgImp, cls);
            classImpCache[key] = c; return c;
        }

        // Append the CallFunction chain.
        var bpThen = new FGuid16(BitConverter.ToUInt32(thenPin.PinId, 0), BitConverter.ToUInt32(thenPin.PinId, 4),
            BitConverter.ToUInt32(thenPin.PinId, 8), BitConverter.ToUInt32(thenPin.PinId, 12));
        for (int i = 0; i < calls.Count; i++)
        {
            var (scriptPkg, cls, func) = calls[i];
            int classImp = ClassImport(scriptPkg, cls);
            var execPin = new SynthPin { OwningNodePkg = callPkgs[i], PinId = execIds[i], PinName = "execute", Category = "exec", SubCategory = "None", Direction = 0 };
            execPin.LinkedTo.Add(i == 0 ? (bpExport + 1, bpThen) : (callPkgs[i - 1], thenIds[i - 1]));
            var thenOut = new SynthPin { OwningNodePkg = callPkgs[i], PinId = thenIds[i], PinName = "then", Category = "exec", SubCategory = "None", Direction = 1 };
            if (i + 1 < calls.Count) thenOut.LinkedTo.Add((callPkgs[i + 1], execIds[i + 1]));

            using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            t.Struct("FunctionReference", "MemberReference", () =>
            {
                var inner = new TaggedPropertyWriter(w, spw.Name);
                WriteMemberReference(inner, classImp, func);
            });
            t.Int("NodePosX", 400 + i * 280);
            t.Int("NodePosY", 64);
            t.GuidStruct("NodeGuid", FGuid16.NewGuid());
            t.WriteNone();
            new PinSerializer(w, spw.Name).WriteOwningPins(new[] { execPin, thenOut });
            w.Flush();
            spw.AddExportRaw(spw.Name("K2Node_CallFunction"), 7 + i, impCallFunc, 0, 0, egIdx + 1, ms.ToArray(), 0x1, false);
        }

        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, targetShort + ".uasset");
        spw.Write(outFile);
        Log.Information("Reconstructed {Target}: BeginPlay -> {N} CallFunction nodes -> {Out}", targetShort, calls.Count, outFile);
    }

    /// <summary>Decompile a cooked BP's ExecuteUbergraph bytecode into the ordered list of impure
    /// /Script/ CallFunctions (skip pure assignments, control flow, and non-script refs).</summary>
    private static List<(string scriptPkg, string cls, string func)> ExtractExecChain(string cookedPath)
    {
        var data = File.ReadAllBytes(cookedPath);
        CUE4Parse.UE4.Assets.Package pkg;
        try
        {
            var provider = new CUE4Parse.FileProvider.DefaultFileProvider(
                Path.GetDirectoryName(cookedPath)!, System.IO.SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_UE4_21));
            provider.ReadScriptData = true;
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(cookedPath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, provider, false);
        }
        catch (Exception ex) { Log.Error(ex, "cooked parse failed"); return new(); }
        return ExtractExecChainCore(pkg);
    }

    public static List<(string scriptPkg, string cls, string func)> ExtractCallsFromExports(IEnumerable<CUE4Parse.UE4.Assets.Exports.UObject> exports)
        => ExtractCallsFromFunctions(exports.OfType<CUE4Parse.UE4.Objects.UObject.UFunction>());

    private static List<(string scriptPkg, string cls, string func)> ExtractCallsFromFunctions(IEnumerable<CUE4Parse.UE4.Objects.UObject.UFunction> functions)
    {
        var result = new List<(string, string, string)>();
        var fns = functions.Where(fn => fn.ScriptBytecode is { Length: > 0 })
            .OrderBy(fn => fn.Name.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(fn => fn.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var fn in fns)
            foreach (var expr in fn.ScriptBytecode!)
                CollectCalls(KismetWalker.WalkOne(expr), result);
        return result;
    }

    /// <summary>Core of the ubergraph decompile: walk the (already parsed, ReadScriptData=true) package's
    /// ExecuteUbergraph_* bytecode and return the ordered impure /Script/ function calls. Exports are loaded
    /// one at a time so a single broken export can't wipe the whole chain.</summary>
    private static List<(string scriptPkg, string cls, string func)> ExtractExecChainCore(Package pkg)
    {
        var result = new List<(string, string, string)>();

        // Gather EVERY UFunction that carries bytecode (the ubergraph holds all event-driven logic, but
        // UserConstructionScript + custom functions live in their own UFunctions). Process the ubergraph
        // first so the natural event-flow order leads, then the rest.
        var fns = new List<CUE4Parse.UE4.Objects.UObject.UFunction>();
        CUE4Parse.UE4.Objects.UObject.UFunction? uber = null;
        for (int i = 0; i < pkg.ExportsLazy.Length; i++)
        {
            if (pkg.ExportMap[i].ClassName != "Function") continue;
            CUE4Parse.UE4.Objects.UObject.UFunction? fn = null;
            try { fn = pkg.ExportsLazy[i].Value as CUE4Parse.UE4.Objects.UObject.UFunction; } catch { }
            if (fn?.ScriptBytecode is not { Length: > 0 }) continue;
            if (pkg.ExportMap[i].ObjectName.Text.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)) uber = fn;
            else fns.Add(fn);
        }
        if (uber != null) fns.Insert(0, uber);

        // Recurse the FULL walked expression tree of each statement, collecting EVERY function reference —
        // top-level calls, calls nested as call arguments, calls inside Let/branch expressions, delegate
        // calls, etc. Every call site becomes its own node (no dedup: 3 calls to the same fn = 3 nodes).
        return ExtractCallsFromFunctions(fns);
    }

    /// <summary>Depth-first walk of a KismetWalker node dict, appending every resolvable function reference
    /// (under the "FunctionRef" key, which KismetWalker emits for StackNode + VirtualFunctionName) in source
    /// order. Recurses into every child dict and every list-of-dicts (Parameters, ContextExpression, …).</summary>
    private static void CollectCalls(Dictionary<string, object?> node, List<(string, string, string)> result)
    {
        if (node.TryGetValue("FunctionRef", out var fr) && fr is string s)
        {
            var parsed = ParseFunctionRef(s);
            if (parsed != null) result.Add(parsed.Value);
        }
        foreach (var kv in node)
        {
            switch (kv.Value)
            {
                case Dictionary<string, object?> child:
                    CollectCalls(child, result);
                    break;
                case System.Collections.IEnumerable en when kv.Value is not string:
                    foreach (var o in en) if (o is Dictionary<string, object?> cd) CollectCalls(cd, result);
                    break;
            }
        }
    }

    /// <summary>"/Script/Engine.KismetSystemLibrary:PrintString" -> (/Script/Engine, KismetSystemLibrary, PrintString).</summary>
    private static (string scriptPkg, string cls, string func)? ParseFunctionRef(string fref)
    {
        if (string.IsNullOrWhiteSpace(fref) || fref == "None") return null;
        var colon = fref.LastIndexOf(':');
        if (colon < 0) return ("", "", fref);
        var func = fref[(colon + 1)..];
        var left = fref[..colon];
        var dot = left.LastIndexOf('.');
        if (dot < 0) return ("", "", func);
        return (left[..dot], left[(dot + 1)..], func);
    }

    private static int AddClassImportForObjectPath(SynthPackageWriter spw, Package templatePkg, string? objectPath)
    {
        if (string.IsNullOrWhiteSpace(objectPath) || objectPath == "None") return 0;
        var path = objectPath.Replace('\\', '/');
        if (!path.StartsWith("/Script/", StringComparison.Ordinal)) return 0;
        var dot = path.LastIndexOf('.');
        if (dot <= 0 || dot + 1 >= path.Length) return 0;
        var packagePath = path[..dot];
        var className = path[(dot + 1)..];
        if (className.Contains(':', StringComparison.Ordinal)) return 0;

        var packageImport = FindPackageImport(templatePkg, packagePath);
        if (packageImport == 0) packageImport = spw.AddImport("/Script/CoreUObject", "Package", 0, packagePath);
        return spw.AddImport("/Script/CoreUObject", "Class", packageImport, className);
    }

    private static bool IsBlueprintAssetClass(string className) =>
        className == "Blueprint" || className.EndsWith("Blueprint", StringComparison.Ordinal);

    private static bool IsBlueprintGeneratedClass(string className) =>
        className == "BlueprintGeneratedClass" || className.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal);

    private static byte[] PatchObjectPropertyValue(byte[] payload, int noneIdx, int propNameIdx, int packageIndex)
    {
        if (NodePayloadWalker.IsUe5)
        {
            if (!NodePayloadWalker.FindTag5(payload, noneIdx, propNameIdx, -1,
                    out _, out _, out int valueOff5, out int tagEnd5))
                return payload;
            if (valueOff5 + 4 > tagEnd5) return payload;
            var copy5 = (byte[])payload.Clone();
            BitConverter.GetBytes(packageIndex).CopyTo(copy5, valueOff5);
            return copy5;
        }
        if (noneIdx < 0 || propNameIdx < 0) return payload;
        var (start, end) = NodePayloadWalker.FindPropertySpan(payload, 0, noneIdx, propNameIdx);
        if (start < 0) return payload;
        var valueOff = start + 8 + 8 + 4 + 4 + 1; // Name, Type, Size, ArrayIndex, HasPropertyGuid
        if (valueOff + 4 > end) return payload;
        var copy = (byte[])payload.Clone();
        BitConverter.GetBytes(packageIndex).CopyTo(copy, valueOff);
        return copy;
    }

    private static void WriteMemberReference(TaggedPropertyWriter inner, int memberParent, string memberName, bool withGuid = false)
    {
        if (memberParent != 0) inner.Object("MemberParent", memberParent);
        else inner.Bool("bSelfContext", true);
        inner.Name("MemberName", memberName);
        // UE5 member references carry MemberGuid (redirector fixup); 4.21 has no such field —
        // writing it there would desync the struct. Zero guid = "no redirector", always safe.
        if (withGuid) inner.GuidStruct("MemberGuid", default);
        inner.WriteNone();
    }

    /// <summary>Place a cooked map's actors into an editor map by SYNTHESIZING each actor + its root
    /// component fresh from CUE4Parse-parsed transforms (no byte-graft/remap). Actor = {RootComponent,
    /// ActorLabel} + None + int32 0; Component = {RelativeLocation/Rotation/Scale} + None + int32 0; both
    /// outers/refs assigned to new indices. Patches ULevel.Actors. No-mesh actors only for now (mesh actors
    /// need StaticMesh asset resolution).</summary>
    /// <summary>Template's saved engine version for the writer to stamp (avoids downgrade-upgrade churn).</summary>
    private static (ushort Major, ushort Minor, ushort Patch, uint Changelist, string Branch)? ReadSavedEngine(Package pkg)
    {
        try
        {
            var v = pkg.Summary.SavedByEngineVersion;
            if (v.Major == 0 && v.Minor == 0 && v.Patch == 0) return null;
            return (v.Major, v.Minor, v.Patch, v.Changelist, v.Branch ?? "");
        }
        catch { return null; }
    }

    /// <summary>Parse an editor template as the running game version, falling back to 4.21 legacy.
    /// Returns the package plus the version that parsed, which the writer must reuse verbatim.</summary>
    private static bool TryParseTemplate(string templatePath, byte[] data, EGame preferred, out Package pkg, out EGame usedGame)
    {
        Exception? first = null;
        foreach (var g in preferred == EGame.GAME_UE4_21 ? new[] { preferred } : new[] { preferred, EGame.GAME_UE4_21 })
        {
            try
            {
                var ar = new FByteArchive(Path.GetFileNameWithoutExtension(templatePath), data, new VersionContainer(g));
                pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
                usedGame = g;
                if (g != preferred) Log.Information("Template {T} parsed as 4.21 legacy (not {G})", templatePath, preferred);
                return true;
            }
            catch (Exception ex) { first ??= ex; }
        }
        Log.Warning(first, "Template parse failed for {T}", templatePath);
        pkg = null!; usedGame = preferred;
        return false;
    }

    /// <summary>Clone an empty 4.21 editor Blueprint template (valid UBlueprint+BlueprintGeneratedClass+SCS), renaming
    /// it to the target BP, so cooked blueprints (incl. UE5/Zen, which the byte-based reconstructor can't handle) show
    /// and open in the content browser. Reskin-only (like the map/material template clones) — crash-safe; 5.x auto-upgrades.</summary>
    /// <summary>A recovered SCS component to graft into a reskinned BP (engine ComponentClass, the variable name shown
    /// in the Components panel, an optional StaticMesh, and the relative transform).</summary>
    public readonly record struct ScsComp(string CompClass, string VarName, string? MeshPkg, string? MeshName,
        float[] Loc, float[] Rot, float[] Scale);

    public static bool CloneBlueprintTemplate(string templatePath, string outFile, string targetShort, string targetPackagePath,
        EGame game = EGame.GAME_UE4_21, IReadOnlyList<ScsComp>? scsComps = null,
        IReadOnlyList<(string scriptPkg, string cls, string func)>? recoveredCalls = null,
        string? parentClassPath = null, IReadOnlyList<FunctionGraph>? functionGraphs = null,
        IReadOnlyList<int>? extraNodePkgs = null, bool skipNodesPatch = false, bool noDepOverride = false,
        string selfPkg = "", string selfClass = "", IReadOnlyList<GraftedVar>? graftedVars = null)
    {
        byte[] data;
        try { data = File.ReadAllBytes(templatePath); } catch { return false; }
        // Templates may be 4.21 (legacy) or match the running game (5.x, saved by the user's editor).
        // The writer follows the template: 4.21 bytes stay 4.21, 5.x bytes stay 5.x — never mixed.
        if (!TryParseTemplate(templatePath, data, game, out var pkg, out var tplGame)) return false;
        bool isUe5 = tplGame >= EGame.GAME_UE5_0;
        try
        {

            int bpExport = Array.FindIndex(pkg.ExportMap, e => IsBlueprintAssetClass(e.ClassName));
            if (bpExport < 0) { Log.Warning("BP template has no Blueprint-family export: {T}", templatePath); return false; }
            int bgcExport = Array.FindIndex(pkg.ExportMap, e => IsBlueprintGeneratedClass(e.ClassName));
            int scsExport = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "SimpleConstructionScript");
            var oldShort = pkg.ExportMap[bpExport].ObjectName.Text;
            var oldPath = pkg.NameMap.Select(n => n.Name)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.EndsWith("/" + oldShort, StringComparison.Ordinal));

            var rename = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [oldShort] = targetShort,
                [oldShort + "_C"] = targetShort + "_C",                  // the generated class
                ["Default__" + oldShort + "_C"] = "Default__" + targetShort + "_C",
            };
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                rename[oldPath] = targetPackagePath;
                rename[oldPath + "." + oldShort] = targetPackagePath + "." + targetShort;
                rename[oldPath + "." + oldShort + "_C"] = targetPackagePath + "." + targetShort + "_C";
            }

            var spw = new SynthPackageWriter(tplGame, targetPackagePath)
            {
                PackageFlags = (uint)pkg.Summary.PackageFlags,
                CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList(),
                // UE5 content-browser indexing reads the package asset-registry block; emit a minimal
                // UE5-shaped record (short name + class + basic tags) so reskinned BPs list like editor files.
                PrimaryArAsset = isUe5 ? (targetShort, "/Script/Engine.Blueprint") : null,
                SavedEngine = ReadSavedEngine(pkg),
            };
            foreach (var n in pkg.NameMap)
            { var s = n.Name ?? "None"; spw.AddRawName(rename.TryGetValue(s, out var rn) ? rn : s); }
            foreach (var imp in pkg.ImportMap)
                spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                    imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number,
                    imp.PackageName.Index, imp.PackageName.Number, imp.ImportOptional);

            // FindPropertySpan/SkipTypeTagData need the template's property-type name indices (static, per-package).
            int TIdx(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
            NodePayloadWalker.StructPropertyIdx = TIdx("StructProperty"); NodePayloadWalker.BoolPropertyIdx = TIdx("BoolProperty");
            NodePayloadWalker.BytePropertyIdx = TIdx("ByteProperty"); NodePayloadWalker.EnumPropertyIdx = TIdx("EnumProperty");
            NodePayloadWalker.ArrayPropertyIdx = TIdx("ArrayProperty"); NodePayloadWalker.SetPropertyIdx = TIdx("SetProperty");
            NodePayloadWalker.MapPropertyIdx = TIdx("MapProperty");
            NodePayloadWalker.IsUe5 = isUe5;
            NodePayloadWalker.NameCount = pkg.NameMap.Length;

            // Plan injected SCS exports: per recovered component -> [ComponentTemplate, SCS_Node]; the node is the 2nd.
            // SCS/component native tails are modelled exactly for 4.21 only — 5.x templates skip grafting
            // (their BPs still reskin + carry graphs; components ride the JSON sidecar for now).
            var inject = (scsComps != null && bgcExport >= 0 && scsExport >= 0 && tplGame == EGame.GAME_UE4_21)
                ? scsComps : null;   // injection only for the proven 4.21 write path
            int egExport = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "EdGraph" && e.ObjectName.Text == "EventGraph");
            // Full graph recovery supersedes the legacy flat exec-chain: per-function entry events with
            // exec + data wiring (see KismetGraphDecompiler). Legacy chain stays as the fallback when no
            // graphs were decompiled (or the template has no EventGraph to hang them on).
            bool useGraphs = functionGraphs != null && functionGraphs.Count > 0
                && egExport >= 0 && (tplGame == EGame.GAME_UE4_21 || isUe5);
            var chain = (!useGraphs ? recoveredCalls?.Where(c => !string.IsNullOrWhiteSpace(c.func)).ToList() : null)
                ?? new List<(string scriptPkg, string cls, string func)>();
            // Drop chain calls that cannot resolve (same rule as graph nodes): self calls must hit a
            // reconstructed function; foreign calls must target stock engine API. Unresolvable
            // FunctionReferences fail the compile ("Could not find function").
            if (chain.Count > 0)
            {
                var chainSelfFuncs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (functionGraphs != null) foreach (var g in functionGraphs)
                    if (!string.IsNullOrWhiteSpace(g.FunctionName)) chainSelfFuncs.Add(g.FunctionName);
                int before = chain.Count;
                chain = chain.Where(c => BlueprintNodeEmitter.ChainCallResolves(
                    c.scriptPkg, c.cls, c.func, selfPkg, selfClass, chainSelfFuncs)).ToList();
                if (chain.Count != before)
                    Log.Information("Pruned {N}/{T} unresolvable legacy call nodes for {Bp}", before - chain.Count, before, targetShort);
            }
            const int MaxRecoveredCalls = 400;
            if (chain.Count > MaxRecoveredCalls)
            {
                Log.Information("Recovered BP call nodes truncated {N} -> {M} for {Bp}", chain.Count, MaxRecoveredCalls, targetShort);
                chain = chain.Take(MaxRecoveredCalls).ToList();
            }
            int baseExp = pkg.ExportMap.Length;
            var newNodePkgs = new List<int>();
            if (inject != null) for (int i = 0; i < inject.Count; i++) newNodePkgs.Add(baseExp + i * 2 + 2);
            int callBaseExp = baseExp + (inject?.Count ?? 0) * 2;
            var callPkgs = Enumerable.Range(0, chain.Count).Select(i => callBaseExp + i + 1).ToArray();
            var callExecIds = chain.Select(_ => FGuid16.NewGuid()).ToArray();
            var callThenIds = chain.Select(_ => FGuid16.NewGuid()).ToArray();
            int noneIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "None");
            int rootNodesIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "RootNodes");
            int allNodesIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "AllNodes");
            int nodesNameIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "Nodes");
            int arrPropIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "ArrayProperty");
            int parentClassIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "ParentClass");
            int parentClassImp = AddClassImportForObjectPath(spw, pkg, parentClassPath);
            if (parentClassImp != 0) Log.Information("Recovered parent class for {Bp}: {Parent}", targetShort, parentClassPath);

            // Build full-graph node payloads BEFORE the clone loop (payload building adds K2 imports,
            // and the EventGraph.Nodes patch below needs the exact dense export indices upfront).
            // Synthesized payloads carry the template's own version preamble (UE5: 1 byte; 4.21: none),
            // captured from the EventGraph export so emitted nodes are byte-shaped like editor nodes.
            List<BlueprintNodeEmitter.BuiltNode> graphNodes = new();
            byte[] graphPreamble = Array.Empty<byte>();
            if (useGraphs)
            {
                try
                {
                    int graphBaseExp = baseExp + (inject?.Count ?? 0) * 2 + chain.Count;
                    var egRaw = new byte[(int)pkg.ExportMap[egExport].SerialSize];
                    Array.Copy(data, (int)pkg.ExportMap[egExport].SerialOffset, egRaw, 0, egRaw.Length);
                    int egStart = NodePayloadWalker.FindPayloadStart(egRaw, pkg.NameMap.Length);
                    graphPreamble = egRaw[..egStart];
                    var varGuids = ReadTemplateVariableGuids(pkg, data);
                    if (graftedVars != null) foreach (var gv in graftedVars) varGuids[gv.Name] = gv.Guid;
                    Dictionary<string, (string Cat, string Sub, string? Obj)>? varCats = null;
                    if (graftedVars != null)
                    {
                        varCats = new Dictionary<string, (string, string, string?)>(StringComparer.Ordinal);
                        foreach (var gv in graftedVars) varCats[gv.Name] = (gv.Category, gv.SubCategory, gv.SubCatObjPath);
                    }
                    graphNodes = BlueprintNodeEmitter.Build(spw, pkg, egExport, targetShort, functionGraphs!, graphBaseExp, isUe5, graphPreamble, varGuids, selfPkg, selfClass, parentClassPath ?? "", varCats);
                }
                catch (Exception ex) { Log.Warning(ex, "Graph node build failed for {Bp}; falling back to no graph nodes", targetShort); graphNodes = new(); }
            }
            var graphPkgs = graphNodes.Select(b => b.Pkg).ToList();
            if (extraNodePkgs != null) graphPkgs.AddRange(extraNodePkgs);

            // UE5 dependency data: parse the template's per-export dep lists (count + FPackageIndex each)
            // so cloned exports keep byte-verbatim dependency entries (imports are preserved 1:1, keeping
            // every index valid). Anything after the base entries (editor trailer block) rides along intact.
            byte[] tplDepEntries = Array.Empty<byte>();
            byte[] tplDepTrailer = Array.Empty<byte>();
            if (isUe5 && !noDepOverride)
            {
                try
                {
                    int depOff = (int)pkg.Summary.DependsOffset;
                    int depArOff = (int)pkg.Summary.AssetRegistryDataOffset;
                    int oe = depOff;
                    for (int ei = 0; ei < baseExp; ei++)
                    {
                        int c = BitConverter.ToInt32(data, oe); oe += 4;
                        if (c < 0 || c > 4096 || oe + c * 4 > depArOff) throw new InvalidOperationException("dep entry OOB");
                        oe += c * 4;
                    }
                    tplDepEntries = new byte[oe - depOff];
                    Array.Copy(data, depOff, tplDepEntries, 0, tplDepEntries.Length);
                    tplDepTrailer = new byte[depArOff - oe];
                    Array.Copy(data, oe, tplDepTrailer, 0, tplDepTrailer.Length);
                }
                catch (Exception ex) { Log.Debug(ex, "Template dep parse failed; using empty depends map"); tplDepEntries = Array.Empty<byte>(); tplDepTrailer = Array.Empty<byte>(); }
            }

            // Split a template payload into (version preamble, tag stream): tag patches run on the stream,
            // then the preamble is rejoined verbatim. 4.21 payloads have no preamble (no-op).
            int nameCount = pkg.NameMap.Length;
            byte[] PatchPayload(byte[] raw, Func<byte[], byte[]> patch)
            {
                int start = NodePayloadWalker.FindPayloadStart(raw, nameCount);
                if (start == 0) return patch(raw);
                var body = new byte[raw.Length - start];
                Array.Copy(raw, start, body, 0, body.Length);
                var patched = patch(body);
                var outp = new byte[start + patched.Length];
                Array.Copy(raw, 0, outp, 0, start);
                Array.Copy(patched, 0, outp, start, patched.Length);
                return outp;
            }

            foreach (var e in pkg.ExportMap)
            {
                var payload = new byte[(int)e.SerialSize];
                Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
                // Append the recovered SCS nodes to the template SCS's RootNodes/AllNodes so they show in Components.
                if (inject != null && Array.IndexOf(pkg.ExportMap, e) == scsExport && newNodePkgs.Count > 0)
                {
                    if (rootNodesIdx >= 0) payload = PatchPayload(payload, b => AppendToObjectArray(b, noneIdx, rootNodesIdx, newNodePkgs));
                    if (allNodesIdx >= 0) payload = PatchPayload(payload, b => AppendToObjectArray(b, noneIdx, allNodesIdx, newNodePkgs));
                }
                if (Array.IndexOf(pkg.ExportMap, e) == egExport && nodesNameIdx >= 0 && arrPropIdx >= 0)
                {
                    if (!skipNodesPatch)
                    {
                        foreach (var cp in callPkgs) payload = PatchPayload(payload, b => PatchNodesArray(b, nodesNameIdx, arrPropIdx, cp, noneIdx));
                        foreach (var gp in graphPkgs) payload = PatchPayload(payload, b => PatchNodesArray(b, nodesNameIdx, arrPropIdx, gp, noneIdx));
                    }
                }
                if (parentClassImp != 0 && Array.IndexOf(pkg.ExportMap, e) == bpExport && parentClassIdx >= 0)
                    payload = PatchPayload(payload, b => PatchObjectPropertyValue(b, noneIdx, parentClassIdx, parentClassImp));
                // Grafted member variables ride the UBlueprint's NewVariables (names+guids shared with nodes).
                if (graftedVars != null && graftedVars.Count > 0 && isUe5 && Array.IndexOf(pkg.ExportMap, e) == bpExport)
                    payload = PatchPayload(payload, b => GraftVariables(pkg, b, graftedVars, spw, isUe5));
                var superIdx = parentClassImp != 0 && Array.IndexOf(pkg.ExportMap, e) == bgcExport
                    ? parentClassImp
                    : e.SuperIndex?.Index ?? 0;
                // UE5 LinkerLoad asserts ScriptSerializationEndOffset == bytes consumed deserializing the
                // script region: any payload that GREW (EventGraph/SCS appends) must extend EndOffset by
                // the same delta or the open crashes. Unpatched payloads pass the template values through.
                long scriptEnd = e.ScriptSerializationEndOffset + (payload.Length - (int)e.SerialSize);
                spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0, superIdx,
                    e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload, (uint)e.ObjectFlags, e.IsAsset,
                    e.ForcedExport, e.NotForClient, e.NotForServer, e.PackageFlags, e.NotAlwaysLoadedForEditorGame,
                    e.GeneratePublicHash, e.ScriptSerializationStartOffset, scriptEnd);
            }

            // Append synthesized [ComponentTemplate, SCS_Node] pairs (outer = BGC / SCS respectively).
            if (inject != null)
            {
                int enginePkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
                int scsNodeClassImp = spw.AddImport("/Script/CoreUObject", "Class", enginePkgImp, "SCS_Node");
                var classImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
                var pkgImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
                int bgcPkg = bgcExport + 1, scsPkg = scsExport + 1;
                for (int i = 0; i < inject.Count; i++)
                {
                    var c = inject[i];
                    // Only emit component classes whose native serialization tail we model exactly: StaticMeshComponent
                    // (UCSMod int32 + LODData int32) and everything else as SceneComponent (UCSMod int32). Emitting an
                    // exotic class (SkyLightComponent, PostProcessComponent, …) with a guessed tail makes its Serialize
                    // read the wrong number of bytes -> "Serial size mismatch: Got X, Expected Y" -> HARD content-browser
                    // crash. Substituting to SceneComponent keeps the BP loadable (component is inert) while meshes,
                    // the actual visual payload, stay intact.
                    var safeClass = c.CompClass == "StaticMeshComponent" ? "StaticMeshComponent" : "SceneComponent";
                    if (!classImpCache.TryGetValue(safeClass, out var compClassImp))
                    { compClassImp = spw.AddImport("/Script/CoreUObject", "Class", enginePkgImp, safeClass); classImpCache[safeClass] = compClassImp; }
                    int meshImp = 0;
                    if (c.MeshPkg != null && c.MeshName != null && safeClass == "StaticMeshComponent")
                    {
                        if (!pkgImpCache.TryGetValue(c.MeshPkg, out var mp)) { mp = spw.AddImport("/Script/CoreUObject", "Package", 0, c.MeshPkg); pkgImpCache[c.MeshPkg] = mp; }
                        meshImp = spw.AddImport("/Script/Engine", "StaticMesh", mp, c.MeshName);
                    }
                    int compPkg = baseExp + i * 2 + 1;
                    // Component template (archetype) under the BGC.
                        using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                        var t = new TaggedPropertyWriter(w, spw.Name);
                        if (meshImp != 0) t.Object("StaticMesh", meshImp);
                        t.ByteEnum("Mobility", "EComponentMobility::Type", "EComponentMobility::Movable");
                        if (c.Loc[0] != 0 || c.Loc[1] != 0 || c.Loc[2] != 0) t.Struct("RelativeLocation", "Vector", () => { w.Write(c.Loc[0]); w.Write(c.Loc[1]); w.Write(c.Loc[2]); });
                        if (c.Rot[0] != 0 || c.Rot[1] != 0 || c.Rot[2] != 0) t.Struct("RelativeRotation", "Rotator", () => { w.Write(c.Rot[0]); w.Write(c.Rot[1]); w.Write(c.Rot[2]); });
                        if (c.Scale[0] != 1 || c.Scale[1] != 1 || c.Scale[2] != 1) t.Struct("RelativeScale3D", "Vector", () => { w.Write(c.Scale[0]); w.Write(c.Scale[1]); w.Write(c.Scale[2]); });
                        t.WriteNone(); w.Write(0);                              // UActorComponent: UCSModifiedProperties count
                        if (safeClass == "StaticMeshComponent") w.Write(0);    // UStaticMeshComponent: LODData int32
                        w.Flush();
                        spw.AddExportRaw(spw.Name(c.VarName + "_GEN_VARIABLE"), 0, compClassImp, 0, 0, bgcPkg, ms.ToArray(), 0x1 | 0x8 | 0x20, false);
                    }
                    // SCS_Node under the SimpleConstructionScript.
                    using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                        var t = new TaggedPropertyWriter(w, spw.Name);
                        t.Object("ComponentClass", compClassImp);
                        t.Object("ComponentTemplate", compPkg);
                        t.Text("CategoryName", "SCS", "Default", "Default");
                        t.GuidStruct("VariableGuid", FGuid16.NewGuid());
                        t.Name("InternalVariableName", c.VarName);
                        t.WriteNone();
                        w.Write(0); // UObject optional ObjectGuid bool; SCS_Node must end with this native tail.
                        w.Flush();
                        spw.AddExportRaw(spw.Name("SCS_Node_" + (100 + i)), 0, scsNodeClassImp, 0, 0, scsPkg, ms.ToArray(), 0x8, false);
                    }
                }
            }

            int chainClassImp = 0;   // K2Node_CallFunction class import, for legacy nodes' dep entries
            if (chain.Count > 0 && egExport >= 0)
            {
                int bgPkgImp = FindPackageImport(pkg, "/Script/BlueprintGraph");
                if (bgPkgImp == 0) bgPkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/BlueprintGraph");
                int impCallFunc = spw.AddImport("/Script/CoreUObject", "Class", bgPkgImp, "K2Node_CallFunction");
                chainClassImp = impCallFunc;
                var pkgImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
                var classImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
                int ClassImp(string scriptPkg, string cls)
                {
                    if (string.IsNullOrWhiteSpace(scriptPkg) || string.IsNullOrWhiteSpace(cls)) return 0;
                    var key = scriptPkg + "." + cls;
                    if (classImpCache.TryGetValue(key, out var c)) return c;
                    if (!pkgImpCache.TryGetValue(scriptPkg, out var pImp))
                    {
                        pImp = FindPackageImport(pkg, scriptPkg);
                        if (pImp == 0) pImp = spw.AddImport("/Script/CoreUObject", "Package", 0, scriptPkg);
                        pkgImpCache[scriptPkg] = pImp;
                    }
                    c = spw.AddImport("/Script/CoreUObject", "Class", pImp, cls);
                    classImpCache[key] = c;
                    return c;
                }

                int egPkg = egExport + 1;
                for (int i = 0; i < chain.Count; i++)
                {
                    var (scriptPkg, cls, func) = chain[i];
                    int classImp = ClassImp(scriptPkg, cls);
                    var execPin = new SynthPin { OwningNodePkg = callPkgs[i], PinId = callExecIds[i], PinName = "execute", Category = "exec", SubCategory = "None", Direction = 0 };
                    if (i > 0) execPin.LinkedTo.Add((callPkgs[i - 1], callThenIds[i - 1]));
                    var thenOut = new SynthPin { OwningNodePkg = callPkgs[i], PinId = callThenIds[i], PinName = "then", Category = "exec", SubCategory = "None", Direction = 1 };
                    if (i + 1 < chain.Count) thenOut.LinkedTo.Add((callPkgs[i + 1], callExecIds[i + 1]));

                    using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
                    var t = new TaggedPropertyWriter(w, spw.Name, isUe5);
                    if (isUe5)
                        t.Struct("FunctionReference", "MemberReference", "/Script/Engine", () =>
                        {
                            var inner = new TaggedPropertyWriter(w, spw.Name, true);
                            WriteMemberReference(inner, classImp, func, true);
                        });
                    else
                        t.Struct("FunctionReference", "MemberReference", () =>
                        {
                            var inner = new TaggedPropertyWriter(w, spw.Name);
                            WriteMemberReference(inner, classImp, func, isUe5);
                        });
                    t.Int("NodePosX", 360 + (i % 12) * 300);
                    t.Int("NodePosY", 48 + (i / 12) * 160);
                    t.GuidStruct("NodeGuid", FGuid16.NewGuid());
                    t.WriteNone();
                    long pinStart = ms.Position;
                    new PinSerializer(w, spw.Name, isUe5).WriteOwningPins(new[] { execPin, thenOut });
                    w.Flush();
                    var callPayload = ms.ToArray();
                    if (isUe5 && graphPreamble.Length > 0)
                    {
                        var withPre = new byte[graphPreamble.Length + callPayload.Length];
                        graphPreamble.CopyTo(withPre, 0);
                        callPayload.CopyTo(withPre, graphPreamble.Length);
                        callPayload = withPre;
                    }
                    spw.AddExportRaw(spw.Name("K2Node_CallFunction"), 1000 + i, impCallFunc, 0, 0, egPkg, callPayload, 0x8, false,
                        false, false, false, 0, true, false, 0, pinStart + graphPreamble.Length);
                }
                Log.Information("Recovered BP call nodes for {Bp}: {N} call(s): {C}", targetShort, chain.Count,
                    string.Join(" -> ", chain.Take(40).Select(c => c.func)) + (chain.Count > 40 ? " -> ..." : ""));
            }

            // Full-graph nodes were built (with exact dense indices) before the clone loop; append them now,
            // in the same order, so EventGraph.Nodes (patched above) matches the export table exactly.
            if (graphNodes.Count > 0 && egExport >= 0)
            {
                int egPkg = egExport + 1;
                foreach (var b in graphNodes)
                    spw.AddExportRaw(spw.Name(b.K2Class), b.NameNum, b.ClassImp, 0, 0, egPkg, b.Payload, b.Flags, false,
                        false, false, false, 0, true, false, 0, b.ScriptEnd + graphPreamble.Length);
            }

            // UE5 depends map: cloned entries verbatim + one single-class entry per synthesized export
            // (SCS pairs never occur on the 5.x path), then the template trailer block untouched.
            // Append-only like everything else, so existing indices stay valid.
            if (isUe5 && !noDepOverride && tplDepEntries.Length > 0)
            {
                using var dms = new MemoryStream();
                dms.Write(tplDepEntries);
                using (var dw = new BinaryWriter(dms, System.Text.Encoding.ASCII, leaveOpen: true))
                {
                    // SCS pairs (always empty on the 5.x path — injection is 4.21-gated — but counted
                    // here so the entry array stays aligned with the export table no matter what).
                    int scsPairs = (inject?.Count ?? 0) * 2;
                    for (int i = 0; i < scsPairs; i++) { dw.Write(0); }
                    for (int i = 0; i < chain.Count; i++) { dw.Write(1); dw.Write(chainClassImp); }
                    foreach (var b in graphNodes) { dw.Write(1); dw.Write(b.ClassImp); }
                }
                dms.Write(tplDepTrailer);
                spw.DependsMapOverride = dms.ToArray();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            spw.Write(outFile);
            Log.Information("Cloned editor blueprint {N} -> {Out} ({C} component(s), {G})", targetShort, outFile, inject?.Count ?? 0, tplGame);
            return true;
        }
        catch (Exception ex) { Log.Warning(ex, "BP template clone failed for {N}", targetShort); return false; }
    }

    /// <summary>Dev: hand-build a mirror of the 5.5 actor template's demo graph (Tpl_DemoEvent -&gt;
    /// PrintString("tpl") + Tpl_Flag get/set + branch) and run it through the real CloneBlueprintTemplate
    /// graph path. Used to byte-diff synthesized nodes against editor ground truth (--dump-package).</summary>
    public static bool EmitDemoGraph(string templatePath, string outDir)
    {
        // Three isolation files: colliding-full (same event name as the template's own node),
        // single-unique (one CustomEvent, distinct name), full-unique (demo graph, distinct entry).
        // If only the colliding one crashes, duplicate custom-event names are the vector.
        bool ok = EmitDemoGraphOne(templatePath, outDir, "DemoBP", "/Game/DemoBP", "Tpl_DemoEvent", true);
        ok &= EmitDemoGraphOne(templatePath, outDir, "DemoSingle", "/Game/DemoSingle", "Recovered_Demo", false);
        ok &= EmitDemoGraphOne(templatePath, outDir, "DemoFull", "/Game/DemoFull", "Recovered_Demo", true);
        return ok;
    }

    private static bool EmitDemoGraphOne(string templatePath, string outDir, string targetShort, string packagePath,
        string entryName, bool fullGraph)
    {
        var g = new FunctionGraph { FunctionName = entryName };
        GraphNode Add(string k2)
        {
            var n = new GraphNode { Id = g.Nodes.Count, K2Class = k2 };
            g.Nodes.Add(n);
            return n;
        }
        GraphPin In(string name, string cat, string def = "") => new() { Name = name, Category = cat, Direction = 0, DefaultValue = def };
        GraphPin Out(string name, string cat) => new() { Name = name, Category = cat, Direction = 1 };

        var entry = Add("K2Node_CustomEvent");
        entry.EventName = entryName;
        entry.Outputs.Add(Out("OutputDelegate", "delegate"));
        entry.Outputs.Add(Out("then", "exec"));

        if (fullGraph)
        {
        var call = Add("K2Node_CallFunction");
        call.FunctionRef = "/Script/Engine.KismetSystemLibrary:PrintString";
        call.FuncPkg = "/Script/Engine"; call.FuncClass = "KismetSystemLibrary"; call.FuncName = "PrintString";
        call.Inputs.Add(In("execute", "exec"));
        call.Inputs.Add(In("self", "object"));
        call.Inputs.Add(In("InString", "string", "tpl"));
        call.Inputs.Add(In("bPrintToScreen", "bool", "true"));
        call.Inputs.Add(In("bPrintToLog", "bool", "true"));
        call.Outputs.Add(Out("then", "exec"));

        var get = Add("K2Node_VariableGet");
        get.VarName = "Tpl_Flag";
        get.Inputs.Add(In("self", "object"));
        get.Outputs.Add(Out("Tpl_Flag", "bool"));

        var set = Add("K2Node_VariableSet");
        set.VarName = "Tpl_Flag";
        set.Inputs.Add(In("execute", "exec"));
        set.Inputs.Add(In("Tpl_Flag", "bool"));
        set.Inputs.Add(In("self", "object"));
        set.Outputs.Add(Out("then", "exec"));
        set.Outputs.Add(Out("Output_Get", "bool"));

        var br = Add("K2Node_IfThenElse");
        br.Inputs.Add(In("execute", "exec"));
        br.Inputs.Add(In("Condition", "bool", "true"));
        br.Outputs.Add(Out("then", "exec"));
        br.Outputs.Add(Out("else", "exec"));

        entry.ExecNext.Add(call.Id);
        call.ExecNext.Add(set.Id);
        set.ExecNext.Add(br.Id);
        br.DataLinks["Condition"] = new DataLink(get.Id, "Tpl_Flag");
        } // fullGraph

        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, targetShort + ".uasset");
        return CloneBlueprintTemplate(templatePath, outFile, targetShort, packagePath,
            EGame.GAME_UE5_5, null, null, null, new List<FunctionGraph> { g });
    }

    /// <summary>Dev: grafted-variable isolation test. A hand-built graph (CustomEvent -> IsValid(Object <-
    /// Get GraftedObj)) plus an explicitly grafted generic-object member. If the getter binds, grafted
    /// object members work and LightSwitch-style drops are contextual; if not, generic-object grafts
    /// are broken structurally.</summary>
    public static bool EmitVarTest(string templatePath, string outDir)
    {
        var g = new FunctionGraph { FunctionName = "VarTestGo" };
        var entry = new GraphNode { Id = 0, K2Class = "K2Node_CustomEvent", EventName = "VarTestGo" };
        entry.Outputs.Add(new GraphPin { Name = "OutputDelegate", Category = "delegate", Direction = 1 });
        entry.Outputs.Add(new GraphPin { Name = "then", Category = "exec", Direction = 1 });
        // Mimic LightSwitch's ubergraph entry: user-defined output pins (exercises the tail format).
        entry.Outputs.Add(new GraphPin { Name = "K2Node_ComponentBoundEvent_OtherActor", Category = "object", Direction = 1, IsUserPin = true });
        entry.Outputs.Add(new GraphPin { Name = "K2Node_ComponentBoundEvent_Hit", Category = "struct", SubCategoryObjPath = "/Script/Engine.HitResult", Direction = 1, IsUserPin = true });
        g.Nodes.Add(entry);
        var get = new GraphNode { Id = 1, K2Class = "K2Node_VariableGet", VarName = "GraftedObj" };
        get.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        get.Outputs.Add(new GraphPin { Name = "GraftedObj", Category = "object", Direction = 1 });
        g.Nodes.Add(get);
        var valid = new GraphNode { Id = 2, K2Class = "K2Node_CallFunction", FuncPkg = "/Script/Engine",
            FuncClass = "KismetSystemLibrary", FuncName = "IsValid",
            FunctionRef = "/Script/Engine.KismetSystemLibrary:IsValid" };
        valid.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
        valid.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        valid.Inputs.Add(new GraphPin { Name = "Object", Category = "object", Direction = 0 });
        valid.Outputs.Add(new GraphPin { Name = "then", Category = "exec", Direction = 1 });
        valid.Outputs.Add(new GraphPin { Name = "ReturnValue", Category = "bool", Direction = 1 });
        g.Nodes.Add(valid);
        entry.ExecNext.Add(valid.Id);
        valid.DataLinks["Object"] = new DataLink(get.Id, "GraftedObj");
        // Second consumer shaped like the LightSwitch case: EqualEqual_ObjectObject.B <- getter.
        var get2 = new GraphNode { Id = 3, K2Class = "K2Node_VariableGet", VarName = "GraftedObj2" };
        get2.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        get2.Outputs.Add(new GraphPin { Name = "GraftedObj2", Category = "object", Direction = 1 });
        g.Nodes.Add(get2);
        var equ = new GraphNode { Id = 4, K2Class = "K2Node_CallFunction", FuncPkg = "/Script/Engine",
            FuncClass = "KismetMathLibrary", FuncName = "EqualEqual_ObjectObject",
            FunctionRef = "/Script/Engine.KismetMathLibrary:EqualEqual_ObjectObject" };
        equ.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        equ.Inputs.Add(new GraphPin { Name = "A", Category = "object", Direction = 0 });
        equ.Inputs.Add(new GraphPin { Name = "B", Category = "object", Direction = 0 });
        equ.Outputs.Add(new GraphPin { Name = "ReturnValue", Category = "bool", Direction = 1 });
        g.Nodes.Add(equ);
        valid.ExecNext.Add(equ.Id);
        equ.DataLinks["B"] = new DataLink(get2.Id, "GraftedObj2");
        // Third case mirroring the Dot-temp shape: grafted float member + getter + Greater arg wire.
        var get3 = new GraphNode { Id = 5, K2Class = "K2Node_VariableGet", VarName = "GraftedFloat" };
        get3.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        get3.Outputs.Add(new GraphPin { Name = "GraftedFloat", Category = "float", Direction = 1 });
        g.Nodes.Add(get3);
        var gt = new GraphNode { Id = 6, K2Class = "K2Node_CallFunction", FuncPkg = "/Script/Engine",
            FuncClass = "KismetMathLibrary", FuncName = "Greater_DoubleDouble",
            FunctionRef = "/Script/Engine.KismetMathLibrary:Greater_DoubleDouble" };
        gt.Inputs.Add(new GraphPin { Name = "A", Category = "float", Direction = 0 });
        gt.Inputs.Add(new GraphPin { Name = "B", Category = "float", Direction = 0, DefaultValue = "0.0" });
        gt.Outputs.Add(new GraphPin { Name = "ReturnValue", Category = "bool", Direction = 1 });
        g.Nodes.Add(gt);
        equ.ExecNext.Add(gt.Id);
        gt.DataLinks["A"] = new DataLink(get3.Id, "GraftedFloat");
        // Fourth case mirroring the numbered-temp shape: cooked-style CallFunc_ name (FName-split path).
        var get4 = new GraphNode { Id = 7, K2Class = "K2Node_VariableGet", VarName = "CallFunc_Dot_VectorVector_ReturnValue_1" };
        get4.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        get4.Outputs.Add(new GraphPin { Name = "CallFunc_Dot_VectorVector_ReturnValue_1", Category = "float", Direction = 1 });
        g.Nodes.Add(get4);
        var gt2 = new GraphNode { Id = 8, K2Class = "K2Node_CallFunction", FuncPkg = "/Script/Engine",
            FuncClass = "KismetMathLibrary", FuncName = "Greater_DoubleDouble",
            FunctionRef = "/Script/Engine.KismetMathLibrary:Greater_DoubleDouble" };
        gt2.Inputs.Add(new GraphPin { Name = "A", Category = "float", Direction = 0 });
        gt2.Inputs.Add(new GraphPin { Name = "B", Category = "float", Direction = 0, DefaultValue = "0.0" });
        gt2.Outputs.Add(new GraphPin { Name = "ReturnValue", Category = "bool", Direction = 1 });
        g.Nodes.Add(gt2);
        gt.ExecNext.Add(gt2.Id);
        gt2.DataLinks["A"] = new DataLink(get4.Id, "CallFunc_Dot_VectorVector_ReturnValue_1");
        // Fifth case: exact LightSwitch names for the stubborn drops.
        var get5 = new GraphNode { Id = 9, K2Class = "K2Node_VariableGet", VarName = "LuauBlueprint" };
        get5.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        get5.Outputs.Add(new GraphPin { Name = "LuauBlueprint", Category = "object", Direction = 1 });
        g.Nodes.Add(get5);
        var equ2 = new GraphNode { Id = 10, K2Class = "K2Node_CallFunction", FuncPkg = "/Script/Engine",
            FuncClass = "KismetMathLibrary", FuncName = "EqualEqual_ObjectObject",
            FunctionRef = "/Script/Engine.KismetMathLibrary:EqualEqual_ObjectObject" };
        equ2.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
        equ2.Inputs.Add(new GraphPin { Name = "A", Category = "object", Direction = 0 });
        equ2.Inputs.Add(new GraphPin { Name = "B", Category = "object", Direction = 0 });
        equ2.Outputs.Add(new GraphPin { Name = "ReturnValue", Category = "bool", Direction = 1 });
        g.Nodes.Add(equ2);
        gt2.ExecNext.Add(equ2.Id);
        equ2.DataLinks["B"] = new DataLink(get5.Id, "LuauBlueprint");
        var gv = new GraftedVar("GraftedObj", "object", "None", 0, null, FGuid16.NewGuid());
        var gv2 = new GraftedVar("GraftedObj2", "object", "None", 0, null, FGuid16.NewGuid());
        var gv3 = new GraftedVar("GraftedFloat", "float", "None", 0, null, FGuid16.NewGuid());
        var gv4 = new GraftedVar("CallFunc_Dot_VectorVector_ReturnValue_1", "float", "None", 0, null, FGuid16.NewGuid());
        var gv5 = new GraftedVar("LuauBlueprint", "object", "None", 0, null, FGuid16.NewGuid());
        var gv6 = new GraftedVar("EntryPoint", "int", "None", 0, null, FGuid16.NewGuid());
        // Numbered sibling of gv4 (FName base shared, number 0) + a big dummy table like LightSwitch's.
        var graft = new List<GraftedVar> { gv, gv2, gv3, gv4, gv5, gv6,
            new GraftedVar("CallFunc_Dot_VectorVector_ReturnValue", "float", "None", 0, null, FGuid16.NewGuid()) };
        for (int i = 0; i < 20; i++)
            graft.Add(new GraftedVar($"DummyPad_{i}", i % 3 == 0 ? "bool" : (i % 3 == 1 ? "object" : "float"),
                "None", 0, null, FGuid16.NewGuid()));
        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, "VarTest.uasset");
        return CloneBlueprintTemplate(templatePath, outFile, "VarTest", "/Game/VarTest",
            EGame.GAME_UE5_5, null, null, null, new List<FunctionGraph> { g }, null, false, false, "", "",
            graft);
    }

    /// <summary>Dev: two-file bisection for graph-append crashes. BisectNodes = reskin + a DUPLICATE
    /// EventGraph.Nodes ref (no new exports: isolates the Nodes-patch mechanics). BisectOrphan = reskin +
    /// one new CustomEvent export NOT listed in Nodes (isolates the node payload). Whichever crashes is guilty.</summary>
    public static bool EmitBisect(string templatePath, string outDir)
    {
        bool ok = CloneBlueprintTemplate(templatePath,
            Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, "BisectNodes.uasset"),
            "BisectNodes", "/Game/BisectNodes", EGame.GAME_UE5_5, null, null, null, null, new List<int> { 8 }, false);
        var g = new FunctionGraph { FunctionName = "Recovered_Demo" };
        var entry = new GraphNode { Id = 0, K2Class = "K2Node_CustomEvent", EventName = "Recovered_Demo" };
        entry.Outputs.Add(new GraphPin { Name = "OutputDelegate", Category = "delegate", Direction = 1 });
        entry.Outputs.Add(new GraphPin { Name = "then", Category = "exec", Direction = 1 });
        g.Nodes.Add(entry);
        ok &= CloneBlueprintTemplate(templatePath,
            Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, "BisectOrphan.uasset"),
            "BisectOrphan", "/Game/BisectOrphan", EGame.GAME_UE5_5, null, null, null,
            new List<FunctionGraph> { g }, null, true);
        // NoDep: same orphan node but an empty legacy depends map (tests whether dep entries are poison).
        ok &= CloneBlueprintTemplate(templatePath,
            Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, "BisectNoDep.uasset"),
            "BisectNoDep", "/Game/BisectNoDep", EGame.GAME_UE5_5, null, null, null,
            new List<FunctionGraph> { g }, null, true, true);
        // NoPins: same orphan node with ZERO pins (tests tags/entry vs pin bodies).
        {
            var g2 = new FunctionGraph { FunctionName = "Recovered_Demo" };
            var bare = new GraphNode { Id = 0, K2Class = "K2Node_CustomEvent", EventName = "Recovered_Demo" };
            g2.Nodes.Add(bare);
            ok &= CloneBlueprintTemplate(templatePath,
                Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, "BisectNoPins.uasset"),
                "BisectNoPins", "/Game/BisectNoPins", EGame.GAME_UE5_5, null, null, null,
                new List<FunctionGraph> { g2 }, null, true);
        }
        return ok;
    }

    /// <summary>Template UBlueprint NewVariables: VarName -&gt; VarGuid. VariableGet/Set refs match by
    /// name AND member guid, so synthesized nodes must carry the real guids (zero reads as missing).</summary>
    private static Dictionary<string, FGuid16> ReadTemplateVariableGuids(Package pkg, byte[] data)
    {
        var map = new Dictionary<string, FGuid16>(StringComparer.Ordinal);
        try
        {
            int bpExport = Array.FindIndex(pkg.ExportMap, e => IsBlueprintAssetClass(e.ClassName));
            if (bpExport < 0) return map;
            int newVarsIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "NewVariables");
            int noneIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "None");
            int arrayPropIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "ArrayProperty");
            int varNameIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "VarName");
            int varGuidIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "VarGuid");
            if (newVarsIdx < 0 || noneIdx < 0 || arrayPropIdx < 0 || varNameIdx < 0 || varGuidIdx < 0) return map;
            var raw = new byte[(int)pkg.ExportMap[bpExport].SerialSize];
            Array.Copy(data, (int)pkg.ExportMap[bpExport].SerialOffset, raw, 0, raw.Length);
            int start = NodePayloadWalker.FindPayloadStart(raw, pkg.NameMap.Length);
            var body = new byte[raw.Length - start];
            Array.Copy(raw, start, body, 0, body.Length);
            if (!NodePayloadWalker.FindTag5(body, noneIdx, newVarsIdx, arrayPropIdx,
                    out _, out _, out int vOff, out _)) return map;
            int o = vOff;
            int count = BitConverter.ToInt32(body, o); o += 4;
            if (count < 0 || count > 4096) return map;
            for (int k = 0; k < count; k++)
            {
                string? vn = null; FGuid16 vg = default; bool hasVg = false;
                while (o + 8 <= body.Length)
                {
                    int nm = BitConverter.ToInt32(body, o);
                    if (nm == noneIdx) { o += 8; break; }
                    if (!NodePayloadWalker.ParseTag5(body, o, pkg.NameMap.Length,
                            out int tn, out int sz, out _, out int vv, out int te)) break;
                    if (vv < 0 || sz < 0 || vv + sz > body.Length) break;
                    if (tn == varNameIdx && sz == 8)
                    {
                        int ni = BitConverter.ToInt32(body, vv);
                        if (ni >= 0 && ni < pkg.NameMap.Length) vn = pkg.NameMap[ni].Name;
                    }
                    else if (tn == varGuidIdx && sz == 16)
                    {
                        vg = new FGuid16(BitConverter.ToUInt32(body, vv), BitConverter.ToUInt32(body, vv + 4),
                            BitConverter.ToUInt32(body, vv + 8), BitConverter.ToUInt32(body, vv + 12));
                        hasVg = true;
                    }
                    if (te <= o) break;
                    o = te;
                }
                if (!string.IsNullOrEmpty(vn) && hasVg) map[vn!] = vg;
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Template variable guid scan failed"); }
        return map;
    }

    /// <summary>A recovered member variable to graft into the reskinned UBlueprint's NewVariables
    /// (guid minted here, shared with emitted node member refs so they bind).</summary>
    public sealed record GraftedVar(string Name, string Category, string SubCategory, byte Container, string? SubCatObjPath, FGuid16 Guid);

    /// <summary>Decompiler fallback / producer-titled / temp-local names that are never real members:
    /// "Var", "Call Func ..." (also flat), "KNNode ..." (node titles), "Temp ..." (locals). Grafting them
    /// creates bogus members; referencing nodes are dropped by ref validation.</summary>
    internal static bool IsBogusMemberName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        bool r = name == "Var"
            || name.StartsWith("Call Func", StringComparison.Ordinal)
            || name.StartsWith("Call_Func", StringComparison.Ordinal)
            || name.StartsWith("CallFunc_", StringComparison.Ordinal)
            || name.StartsWith("KNNode", StringComparison.Ordinal)
            || name.StartsWith("Temp ", StringComparison.Ordinal)
            || name.StartsWith("Temp_", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("UE4D_LOGVAR") == "1" && name.Contains("CallFunc"))
            Serilog.Log.Information("BOGUSCHECK [{N}] len={L} result={R}", name, name.Length, r);
        return r;
    }

    /// <summary>Collect grafted vars from decompiled graphs: every clean VarGet/VarSet name that isn't an
    /// entry user pin or Self, typed from its pins. Space-names (struct member paths like "Hit Component")
    /// are sanitized to underscores on the nodes and grafted under the flat name.</summary>
    public static List<GraftedVar> CollectGraftedVars(IReadOnlyList<FunctionGraph> graphs,
        IReadOnlyDictionary<string, (string Cat, string Sub, string? Obj, byte Cont)>? memberTypes = null)
    {
        // FName comparison is case-insensitive: mirror that here so "Var" and "VAR" can't double-graft.
        var userPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in graphs)
        {
            var entry = g.Nodes.FirstOrDefault(n => n.Id == g.EntryNode);
            if (entry is null) continue;
            foreach (var p in entry.Outputs) if (p.IsUserPin) userPins.Add(p.Name);
        }
        var out_ = new List<GraftedVar>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in graphs)
            foreach (var n in g.Nodes)
            {
                if (n.K2Class is not ("K2Node_VariableGet" or "K2Node_VariableSet")) continue;
                if (string.IsNullOrWhiteSpace(n.VarName) || n.VarName == "Self" || n.VarName == "EntryPoint") continue;
                if (IsBogusMemberName(n.VarName)) continue;
                // "Var" is the decompiler's unknown-variable fallback, not a real member. Grafting it
                // creates a bogus member that poisons links ("Can't connect pins Var ...", "property
                // associated with Var could not be found"). The emitter drops nodes referencing it.
                if (n.VarName == "Var") continue;
                if (userPins.Contains(n.VarName)) { Log.Debug("GRAFTSKIP userpin {N}", n.VarName); continue; }
                if (n.VarName.Contains(' '))
                {
                    var flat = n.VarName.Replace(' ', '_');
                    Log.Information("Struct member path '{Old}' grafted as member '{New}' (field write folds into whole-struct default)", n.VarName, flat);
                    n.VarName = flat;
                }
                if (!seen.Add(n.VarName)) continue;
                string cat = "wildcard"; string sub = "None"; string? objPath = null; byte cont = 0;
                // Cooked BGC member types first (declared truth, unpoisoned by propagation); then
                // signature types; engine-table producer types for CallFunc_ temps; node pins last.
                if (memberTypes != null && memberTypes.TryGetValue(n.VarName, out var mmt))
                { cat = mmt.Cat; sub = mmt.Sub; objPath = mmt.Obj; cont = mmt.Cont; }
                if (cat == "wildcard" && objPath is null)
                    foreach (var gg in graphs)
                        if (gg.VarTypes.TryGetValue(n.VarName, out var vt))
                        { cat = vt.Cat; sub = vt.Sub; objPath = vt.Obj; cont = vt.Cont; break; }
                if ((cat == "wildcard" || (cat == "struct" && objPath is null))
                    && KismetGraphDecompiler.EngineTempType(n.VarName) is { } et)
                { cat = et.Cat; objPath = et.Obj; }
                if (cat == "wildcard" && objPath is null)
                {
                    var vp = n.K2Class == "K2Node_VariableGet"
                        ? n.Outputs.FirstOrDefault(p => p.Category != "exec")
                        : n.Inputs.FirstOrDefault(p => p.Category != "exec" && p.Name != "self" && p.Direction == 0);
                    if (vp != null)
                    {
                        if (cat == "wildcard") cat = vp.Category;
                        if (sub == "None") sub = vp.SubCategory;
                        objPath ??= vp.SubCategoryObjPath;
                        if (cont == 0) cont = vp.Container;
                    }
                }
                // A struct/class-typed member without a concrete type is illegal ("invalid type
                // Structure") — worse than an unbound getter. Skip it (honest red).
                // Same for delegates (multicast types can't be synthesized → "invalid type Delegate"),
                // wildcards (a variable needs a concrete type), class refs without a class, and plain
                // object refs without a class (untyped object pins can't be recreated → "RecreatePinForVariable
                // pin not found" → "In use pin no longer exists"). Skipped variables are also skipped as
                // nodes by the emitter's ref validation.
                if (cat is "delegate" or "multicastdelegate" or "multicast_delegate") continue;
                if (cat == "wildcard") continue;
                if (cat is "struct" or "class" or "softclass" or "object") { if (objPath is null) continue; }
                // Deterministic guid (name-seeded): reproducible builds, correlatable logs/diffs.
                byte[] hash;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("VarGuid:" + n.VarName));
                var gv = new GraftedVar(n.VarName, cat, sub, cont, objPath,
                    new FGuid16(BitConverter.ToUInt32(hash, 0), BitConverter.ToUInt32(hash, 4),
                        BitConverter.ToUInt32(hash, 8), BitConverter.ToUInt32(hash, 12)));
                if (Environment.GetEnvironmentVariable("UE4D_LOGVAR") == "1")
                    Log.Information("GRAFTVAR {N} cat={C} sub={S} obj={O} cont={Cont}", gv.Name, cat, sub, objPath ?? "-", cont);
                Log.Debug("GRAFTEDVAR {N} cat={C} sub={S} obj={O} cont={Cont} guid={G}", gv.Name, cat, sub, objPath ?? "-", cont,
                    $"{gv.Guid.A:X8}{gv.Guid.B:X8}{gv.Guid.C:X8}{gv.Guid.D:X8}");
                out_.Add(gv);
            }
        return out_;
    }

    /// <summary>Append grafted variables to the UBlueprint payload's NewVariables array (UE5): count++,
    /// elements appended at the array end, tag Size extended. Element shape mirrors template ground
    /// truth (VarName/VarGuid/VarType-69B-native/PropertyFlags + None); everything else defaults.</summary>
    private static byte[] GraftVariables(Package pkg, byte[] body, IReadOnlyList<GraftedVar> grafted, SynthPackageWriter spw, bool isUe5)
    {
        int noneIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "None");
        int newVarsIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "NewVariables");
        int arrayPropIdx = Array.FindIndex(pkg.NameMap, n => n.Name == "ArrayProperty");
        if (noneIdx < 0 || newVarsIdx < 0 || arrayPropIdx < 0 || grafted.Count == 0) return body;
        if (!NodePayloadWalker.FindTag5(body, noneIdx, newVarsIdx, arrayPropIdx,
                out _, out int sizePos, out int vOff, out _)) return body;
        int count = BitConverter.ToInt32(body, vOff);
        if (count < 0 || count > 4096) return body;
        // Walk existing elements to the array end (each self-delimits via None).
        int o = vOff + 4;
        for (int k = 0; k < count; k++)
        {
            while (o + 8 <= body.Length)
            {
                int nm = BitConverter.ToInt32(body, o);
                if (nm == noneIdx) { o += 8; break; }
                if (!NodePayloadWalker.ParseTag5(body, o, pkg.NameMap.Length,
                        out _, out _, out _, out _, out int te)) return body;
                if (te <= o) return body;
                o = te;
            }
        }
        byte[] added;
        using (var ms = new MemoryStream())
        {
            using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name, true);
            var pinSer = new PinSerializer(w, spw.Name, true);
            foreach (var v in grafted)
            {
                // Mirror the emitter's UE5 float/double convention (PinCategory real, sub = float/double).
                string vcat = v.Category, vsub = string.IsNullOrWhiteSpace(v.SubCategory) ? "None" : v.SubCategory;
                if (isUe5 && (vcat == "float" || vcat == "double")) { vsub = vcat; vcat = "real"; }
                t.Name("VarName", v.Name);
                t.GuidStruct("VarGuid", v.Guid);
                t.Struct("VarType", "EdGraphPinType", "/Script/Engine", () =>
                {
                    pinSer.WritePinTypeRaw(new SynthPin
                    {
                        Category = vcat,
                        SubCategory = vsub,
                        SubCategoryObjPkg = ResolveEngineSubCatObj(pkg, spw, v),
                        ContainerType = v.Container,
                    });
                }, 0x08);
                t.UInt64("PropertyFlags", 65541);
                // Type-appropriate defaults: template-proven "" for bool; parseable zeros elsewhere.
                // An unparseable default (e.g. "" for float/object) can fail UPROPERTY creation.
                string defVal = vcat switch
                {
                    "bool" => "",
                    "int" or "byte" => "0",
                    "float" or "double" or "real" => "0.0",
                    "object" => "None",
                    _ => "",
                };
                t.Str("DefaultValue", defVal);
                t.WriteNone();
            }
            w.Flush();
            added = ms.ToArray();
        }
        var outp = new byte[body.Length + added.Length];
        Array.Copy(body, 0, outp, 0, o);
        Array.Copy(added, 0, outp, o, added.Length);
        Array.Copy(body, o, outp, o + added.Length, body.Length - o);
        BitConverter.GetBytes(count + grafted.Count).CopyTo(outp, vOff);
        BitConverter.GetBytes(BitConverter.ToInt32(outp, sizePos) + added.Length).CopyTo(outp, sizePos);
        return outp;
    }

    /// <summary>Sub-category import for grafted VarTypes: engine structs/classes resolve in a stock
    /// editor, so find-or-create those imports (the path carries its package). Game paths stay
    /// reuse-only (0 when the template lacks them) — a wrong-package guess would dangle LinkerLoad.</summary>
    private static int ResolveEngineSubCatObj(Package pkg, SynthPackageWriter spw, GraftedVar v)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(v.SubCatObjPath) || v.Category is not ("object" or "struct")) return 0;
            bool isEngine = v.SubCatObjPath.StartsWith("/Script/Engine.", StringComparison.Ordinal)
                || v.SubCatObjPath.StartsWith("/Script/CoreUObject.", StringComparison.Ordinal);
            var dot = v.SubCatObjPath.LastIndexOf('.');
            if (dot <= "/Script/".Length) return 0;
            var pkgPath = v.SubCatObjPath.Substring(0, dot);
            var name = v.SubCatObjPath.Substring(dot + 1);
            var kind = v.Category == "struct" ? "ScriptStruct" : "Class";
            int pkgImp = FindPackageImport(pkg, pkgPath);
            if (pkgImp == 0)
            {
                if (!isEngine) return 0;
                pkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, pkgPath);
            }
            int found = FindTemplateImport(pkg, "/Script/CoreUObject", kind, pkgImp, name);
            if (found != 0) return found;
            return isEngine ? spw.AddImport("/Script/CoreUObject", kind, pkgImp, name) : 0;
        }
        catch { return 0; }
    }

    /// <summary>Dev: payload-vs-shell transplant. Reskin + a VERBATIM byte copy of the template's own
    /// K2Node_CallFunction payload as a new export (fresh name "K2Node_Transplant"), listed in EventGraph.Nodes.
    /// If it opens, synthesized payload bytes are guilty; if it crashes, the shell/entry is guilty.</summary>
    public static bool EmitTransplant(string templatePath, string outDir)
    {
        const string targetShort = "TransplantCall";
        const string packagePath = "/Game/TransplantCall";
        byte[] data;
        try { data = File.ReadAllBytes(templatePath); } catch { return false; }
        if (!TryParseTemplate(templatePath, data, EGame.GAME_UE5_5, out var pkg, out var tplGame)) return false;
        bool isUe5 = tplGame >= EGame.GAME_UE5_0;
        try
        {
            int bpExport = Array.FindIndex(pkg.ExportMap, e => IsBlueprintAssetClass(e.ClassName));
            if (bpExport < 0) return false;
            int egExport = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "EdGraph" && e.ObjectName.Text == "EventGraph");
            if (egExport < 0) return false;
            int srcIdx = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "K2Node_CallFunction");
            if (srcIdx < 0) srcIdx = Array.FindIndex(pkg.ExportMap, e => e.ClassName.StartsWith("K2Node_", StringComparison.Ordinal));
            if (srcIdx < 0) return false;
            var src = pkg.ExportMap[srcIdx];
            var srcPayload = new byte[(int)src.SerialSize];
            Array.Copy(data, (int)src.SerialOffset, srcPayload, 0, srcPayload.Length);
            int baseExp = pkg.ExportMap.Length;
            int newIdx = baseExp + 1;   // dense 1-based FPackageIndex of the transplanted export
            int classImp = src.ClassIndex?.Index ?? 0;

            var oldShort = pkg.ExportMap[bpExport].ObjectName.Text;
            var oldPath = pkg.NameMap.Select(n => n.Name)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.EndsWith("/" + oldShort, StringComparison.Ordinal));
            var rename = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [oldShort] = targetShort,
                [oldShort + "_C"] = targetShort + "_C",
                ["Default__" + oldShort + "_C"] = "Default__" + targetShort + "_C",
            };
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                rename[oldPath] = packagePath;
                rename[oldPath + "." + oldShort] = packagePath + "." + targetShort;
                rename[oldPath + "." + oldShort + "_C"] = packagePath + "." + targetShort + "_C";
            }

            var spw = new SynthPackageWriter(tplGame, packagePath)
            {
                PackageFlags = (uint)pkg.Summary.PackageFlags,
                CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList(),
                PrimaryArAsset = isUe5 ? (targetShort, "/Script/Engine.Blueprint") : null,
                SavedEngine = ReadSavedEngine(pkg),
            };
            foreach (var n in pkg.NameMap)
            { var s = n.Name ?? "None"; spw.AddRawName(rename.TryGetValue(s, out var rn) ? rn : s); }
            foreach (var imp in pkg.ImportMap)
                spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                    imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number,
                    imp.PackageName.Index, imp.PackageName.Number, imp.ImportOptional);

            int TIdx(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
            NodePayloadWalker.StructPropertyIdx = TIdx("StructProperty"); NodePayloadWalker.BoolPropertyIdx = TIdx("BoolProperty");
            NodePayloadWalker.BytePropertyIdx = TIdx("ByteProperty"); NodePayloadWalker.EnumPropertyIdx = TIdx("EnumProperty");
            NodePayloadWalker.ArrayPropertyIdx = TIdx("ArrayProperty"); NodePayloadWalker.SetPropertyIdx = TIdx("SetProperty");
            NodePayloadWalker.MapPropertyIdx = TIdx("MapProperty");
            NodePayloadWalker.IsUe5 = isUe5;
            NodePayloadWalker.NameCount = pkg.NameMap.Length;

            int noneIdx = TIdx("None");
            int nodesNameIdx = TIdx("Nodes");
            int arrPropIdx = TIdx("ArrayProperty");

            byte[] tplDepEntries = Array.Empty<byte>();
            byte[] tplDepTrailer = Array.Empty<byte>();
            if (isUe5)
            {
                try
                {
                    int depOff = (int)pkg.Summary.DependsOffset;
                    int depArOff = (int)pkg.Summary.AssetRegistryDataOffset;
                    int oe = depOff;
                    for (int ei = 0; ei < baseExp; ei++)
                    {
                        int c = BitConverter.ToInt32(data, oe); oe += 4;
                        if (c < 0 || c > 4096 || oe + c * 4 > depArOff) throw new InvalidOperationException("dep entry OOB");
                        oe += c * 4;
                    }
                    tplDepEntries = new byte[oe - depOff];
                    Array.Copy(data, depOff, tplDepEntries, 0, tplDepEntries.Length);
                    tplDepTrailer = new byte[depArOff - oe];
                    Array.Copy(data, oe, tplDepTrailer, 0, tplDepTrailer.Length);
                }
                catch (Exception ex) { Log.Debug(ex, "Template dep parse failed; using empty depends map"); tplDepEntries = Array.Empty<byte>(); tplDepTrailer = Array.Empty<byte>(); }
            }

            int nameCount = pkg.NameMap.Length;
            byte[] PatchPayload(byte[] raw, Func<byte[], byte[]> patch)
            {
                int start = NodePayloadWalker.FindPayloadStart(raw, nameCount);
                if (start == 0) return patch(raw);
                var body = new byte[raw.Length - start];
                Array.Copy(raw, start, body, 0, body.Length);
                var patched = patch(body);
                var outp = new byte[start + patched.Length];
                Array.Copy(raw, 0, outp, 0, start);
                Array.Copy(patched, 0, outp, start, patched.Length);
                return outp;
            }

            foreach (var e in pkg.ExportMap)
            {
                var payload = new byte[(int)e.SerialSize];
                Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
                if (Array.IndexOf(pkg.ExportMap, e) == egExport && nodesNameIdx >= 0 && arrPropIdx >= 0)
                    payload = PatchPayload(payload, b => PatchNodesArray(b, nodesNameIdx, arrPropIdx, newIdx, noneIdx));
                long scriptEnd = e.ScriptSerializationEndOffset + (payload.Length - (int)e.SerialSize);
                spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0,
                    e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload,
                    (uint)e.ObjectFlags, e.IsAsset, e.ForcedExport, e.NotForClient, e.NotForServer, e.PackageFlags,
                    e.NotAlwaysLoadedForEditorGame, e.GeneratePublicHash, e.ScriptSerializationStartOffset, scriptEnd);
            }
            // Transplanted export: template payload bytes untouched, fresh export name, same class/outer/flags/offsets.
            spw.AddExportRaw(spw.Name("K2Node_Transplant"), 0, classImp,
                src.SuperIndex?.Index ?? 0, src.TemplateIndex?.Index ?? 0, egExport + 1, srcPayload,
                (uint)src.ObjectFlags, src.IsAsset, src.ForcedExport, src.NotForClient, src.NotForServer, src.PackageFlags,
                src.NotAlwaysLoadedForEditorGame, src.GeneratePublicHash,
                src.ScriptSerializationStartOffset, src.ScriptSerializationEndOffset);

            if (isUe5 && tplDepEntries.Length > 0)
            {
                using var dms = new MemoryStream();
                dms.Write(tplDepEntries);
                using (var dw = new BinaryWriter(dms, System.Text.Encoding.ASCII, leaveOpen: true))
                { dw.Write(1); dw.Write(classImp); }
                dms.Write(tplDepTrailer);
                spw.DependsMapOverride = dms.ToArray();
            }

            var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, targetShort + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            spw.Write(outFile);
            Log.Information("Emitted transplant {Out} (verbatim {Src} payload, {Sz}B)", outFile, src.ObjectName.Text, srcPayload.Length);
            return true;
        }
        catch (Exception ex) { Log.Warning(ex, "Transplant emit failed"); return false; }
    }

    /// <summary>Dev/game: reskin an editor map template (.umap, version-following like the BP path) to a
    /// target package with NO content changes — every map exists and opens; actors come later via the
    /// UE5 placement port. Verbatim imports/exports/payloads/deps, renamed names, World AR record.</summary>
    public static bool ReskinMap(string templatePath, string outFile, string targetShort, string targetPackagePath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(templatePath); } catch { return false; }
        if (!TryParseTemplate(templatePath, data, EGame.GAME_UE5_5, out var pkg, out var tplGame)) return false;
        bool isUe5 = tplGame >= EGame.GAME_UE5_0;
        try
        {
            int worldExport = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "World");
            if (worldExport < 0) { Log.Warning("Map template has no World export: {T}", templatePath); return false; }
            int baseExp = pkg.ExportMap.Length;
            var oldShort = pkg.ExportMap[worldExport].ObjectName.Text;
            var oldPath = pkg.NameMap.Select(n => n.Name)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.EndsWith("/" + oldShort, StringComparison.Ordinal));
            var rename = new Dictionary<string, string>(StringComparer.Ordinal) { [oldShort] = targetShort };
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                rename[oldPath] = targetPackagePath;
                rename[oldPath + "." + oldShort] = targetPackagePath + "." + targetShort;
            }

            var spw = new SynthPackageWriter(tplGame, targetPackagePath)
            {
                PackageFlags = (uint)pkg.Summary.PackageFlags,
                CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList(),
                PrimaryArAsset = isUe5 ? (targetShort, "/Script/Engine.World") : null,
                SavedEngine = ReadSavedEngine(pkg),
            };
            foreach (var n in pkg.NameMap)
            { var s = n.Name ?? "None"; spw.AddRawName(rename.TryGetValue(s, out var rn) ? rn : s); }
            foreach (var imp in pkg.ImportMap)
                spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                    imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number,
                    imp.PackageName.Index, imp.PackageName.Number, imp.ImportOptional);

            byte[] tplDepEntries = Array.Empty<byte>();
            byte[] tplDepTrailer = Array.Empty<byte>();
            if (isUe5)
            {
                try
                {
                    int depOff = (int)pkg.Summary.DependsOffset;
                    int depArOff = (int)pkg.Summary.AssetRegistryDataOffset;
                    int oe = depOff;
                    for (int ei = 0; ei < baseExp; ei++)
                    {
                        int c = BitConverter.ToInt32(data, oe); oe += 4;
                        if (c < 0 || c > 4096 || oe + c * 4 > depArOff) throw new InvalidOperationException("dep entry OOB");
                        oe += c * 4;
                    }
                    tplDepEntries = new byte[oe - depOff];
                    Array.Copy(data, depOff, tplDepEntries, 0, tplDepEntries.Length);
                    tplDepTrailer = new byte[depArOff - oe];
                    Array.Copy(data, oe, tplDepTrailer, 0, tplDepTrailer.Length);
                }
                catch (Exception ex) { Log.Debug(ex, "Template dep parse failed; using empty depends map"); tplDepEntries = Array.Empty<byte>(); tplDepTrailer = Array.Empty<byte>(); }
            }

            foreach (var e in pkg.ExportMap)
            {
                var payload = new byte[(int)e.SerialSize];
                Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
                long scriptEnd = e.ScriptSerializationEndOffset + (payload.Length - (int)e.SerialSize);
                spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0,
                    e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload,
                    (uint)e.ObjectFlags, e.IsAsset, e.ForcedExport, e.NotForClient, e.NotForServer, e.PackageFlags,
                    e.NotAlwaysLoadedForEditorGame, e.GeneratePublicHash, e.ScriptSerializationStartOffset, scriptEnd);
            }
            if (isUe5 && tplDepEntries.Length > 0)
            {
                using var dms = new MemoryStream();
                dms.Write(tplDepEntries);
                dms.Write(tplDepTrailer);
                spw.DependsMapOverride = dms.ToArray();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            spw.Write(outFile);
            Log.Information("Reskinned map {N} -> {Out} ({G})", targetShort, outFile, tplGame);
            return true;
        }
        catch (Exception ex) { Log.Warning(ex, "Map reskin failed for {N}", targetShort); return false; }
    }

    /// <summary>UE5 actor placement: version-following twin of the 4.21 tail of <see cref="PlaceActorsCore"/>.
    /// <summary>A preserved streaming sublevel reference (from the cooked UWorld's StreamingLevels): its level
    /// asset, original streaming class, and the visibility/load flags that decide whether the editor auto-loads
    /// it. Dropping these flags made every sublevel force-load (the "overloading" crash on hub maps).</summary>
    public readonly record struct StreamingLevelRef(string Asset, string Class, bool Visible, bool VisibleInEditor, bool ShouldBeLoaded, bool IsStatic);

    /// Parses the template as UE5, emits UE5 tags (double transforms, 3/4-node enum trees, native-struct
    /// flags), UE5 shell (versions/SavedEngine/World AR/verbatim deps), and 5.5 native tails (actor 8B,
    /// StaticMeshComponent 12B with override array / 16B plain, other components 8B — zero-filled; the
    /// editor's serial-size assert arbitrates exact sizes on the validation loop).</summary>
    private static void PlaceActorsUe5(byte[] templateData, string outFile, string targetShort, string targetPackagePath,
        string? contentRoot,
        List<(string actorPkg, string actorClass, string compPkg, string compClass, string compName, string label, float[] loc, float[] rot, float[] scale, string? meshPkg, string? meshName, string? worldAsset,         List<(string? pkg, string? name, string? cls)> overrideMats, bool actorHidden, bool compVisible, bool compHiddenInGame, bool editorOnly, bool absLoc, bool absRot, bool absScale, string? textValue, float worldSize, (string? pkg, string? name, string? cls) decalMat, float[] decalSize, CUE4Parse.UE4.Objects.Core.Misc.FGuid mapBuildId, CUE4Parse.UE4.Assets.Exports.UObject sourceComp, string? childBp)> place,
        List<StreamingLevelRef> streamingAssets, string? defaultGameMode = null)
    {
        Package tpkg;
        try
        {
            var ar = new FByteArchive("template", templateData, new VersionContainer(EGame.GAME_UE5_5));
            tpkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "UE5 map template parse"); return; }

        int TIdx(string s) => Array.FindIndex(tpkg.NameMap, n => n.Name == s);
        NodePayloadWalker.StructPropertyIdx = TIdx("StructProperty"); NodePayloadWalker.BoolPropertyIdx = TIdx("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = TIdx("ByteProperty"); NodePayloadWalker.EnumPropertyIdx = TIdx("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = TIdx("ArrayProperty"); NodePayloadWalker.SetPropertyIdx = TIdx("SetProperty");
        NodePayloadWalker.MapPropertyIdx = TIdx("MapProperty");
        NodePayloadWalker.IsUe5 = true;
        NodePayloadWalker.NameCount = tpkg.NameMap.Length;
        int noneIdx = TIdx("None");

        int worldExport = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "World");
        if (worldExport < 0) { Log.Error("UE5 map template has no World export"); return; }
        int lvlExport = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "Level");
        if (lvlExport < 0) { Log.Error("UE5 map template has no Level export"); return; }
        int wsExport = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "WorldSettings");
        var oldShort = tpkg.ExportMap[worldExport].ObjectName.Text;
        var oldPath = tpkg.NameMap.Select(n => n.Name)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.EndsWith("/" + oldShort, StringComparison.Ordinal));
        var rename = new Dictionary<string, string>(StringComparer.Ordinal) { [oldShort] = targetShort };
        if (!string.IsNullOrWhiteSpace(oldPath))
        {
            rename[oldPath] = targetPackagePath;
            rename[oldPath + "." + oldShort] = targetPackagePath + "." + targetShort;
        }

        // v1 scope gate: only actor shapes modeled exactly for 5.5 (ground truth: Template_Default +
        // place_sample dumps). Everything else is counted + skipped (honest, crash-free). Mesh refs resolve
        // at emit time (missing file -> valid mesh-less component, never a dangling import).
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<(string actorPkg, string actorClass, string compPkg, string compClass, string compName, string label, float[] loc, float[] rot, float[] scale, string? meshPkg, string? meshName, string? worldAsset, List<(string? pkg, string? name, string? cls)> overrideMats, bool actorHidden, bool compVisible, bool compHiddenInGame, bool editorOnly, bool absLoc, bool absRot, bool absScale, string? textValue, float worldSize, (string? pkg, string? name, string? cls) decalMat, float[] decalSize, CUE4Parse.UE4.Objects.Core.Misc.FGuid mapBuildId, CUE4Parse.UE4.Assets.Exports.UObject sourceComp, string? childBp)>();
        foreach (var a in place)
        {
            bool engineNative = a.actorPkg == "/Script/Engine";
            bool light = a.actorClass.EndsWith("Light", StringComparison.Ordinal)
                && a.compClass.EndsWith("LightComponent", StringComparison.Ordinal);
            bool sma = engineNative && a.actorClass == "StaticMeshActor";
            bool pstart = engineNative && a.actorClass == "PlayerStart";
            bool lvlInst = engineNative && a.actorClass == "LevelInstance" && a.worldAsset != null;
            if (!(sma || (engineNative && light) || pstart || lvlInst))
            {
                var key = a.actorPkg + "." + a.actorClass;
                skipped[key] = skipped.TryGetValue(key, out var n) ? n + 1 : 1;
                continue;
            }
            items.Add(a);
        }
        if (skipped.Count > 0)
            Log.Information("UE5 placement v1 skipped {N}: {L}", skipped.Values.Sum(),
                string.Join(", ", skipped.Select(kv => $"{kv.Key}x{kv.Value}")));
        int childWrapped = items.Count(a => a.childBp != null);
        if (childWrapped > 0)
            Log.Information("UE5 placement: {N} BP actor(s) wrapped with ChildActor spawner", childWrapped);
        int missingMesh = 0;
        bool MeshOnDisk(string? meshPkg, string? meshName)
        {
            if (string.IsNullOrWhiteSpace(meshPkg) || string.IsNullOrWhiteSpace(meshName)) return false;
            if (meshPkg.StartsWith("/Engine/", StringComparison.Ordinal)) return true;
            if (string.IsNullOrWhiteSpace(contentRoot) || !meshPkg.StartsWith("/Game/", StringComparison.Ordinal)) return false;
            var uasset = Path.Combine(contentRoot, meshPkg.Substring("/Game/".Length) + ".uasset");
            if (!File.Exists(uasset)) return false;
            // Only reference meshes with a real-geometry .glb sidecar (conversion succeeded). Without it
            // the .uasset is a verbatim cooked copy the editor can't parse -> Array OOB crash on map load.
            // (After the glTF import batch lands real meshes, the sidecar check keeps passing.)
            return File.Exists(Path.ChangeExtension(uasset, ".glb"));
        }

        var spw = new SynthPackageWriter(EGame.GAME_UE5_5, targetPackagePath)
        {
            PackageFlags = (uint)tpkg.Summary.PackageFlags,
            CustomVersionsOverride = tpkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList(),
            PrimaryArAsset = (targetShort, "/Script/Engine.World"),
            SavedEngine = ReadSavedEngine(tpkg),
        };
        foreach (var n in tpkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));
        foreach (var imp in tpkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number,
                imp.PackageName.Index, imp.PackageName.Number, imp.ImportOptional);

        int baseExp = tpkg.ExportMap.Length;
        byte[] tplDepEntries = Array.Empty<byte>();
        byte[] tplDepTrailer = Array.Empty<byte>();
        try
        {
            int depOff = (int)tpkg.Summary.DependsOffset;
            int depArOff = (int)tpkg.Summary.AssetRegistryDataOffset;
            int oe = depOff;
            for (int ei = 0; ei < baseExp; ei++)
            {
                int c = BitConverter.ToInt32(templateData, oe); oe += 4;
                if (c < 0 || c > 4096 || oe + c * 4 > depArOff) throw new InvalidOperationException("dep entry OOB");
                oe += c * 4;
            }
            tplDepEntries = new byte[oe - depOff];
            Array.Copy(templateData, depOff, tplDepEntries, 0, tplDepEntries.Length);
            tplDepTrailer = new byte[depArOff - oe];
            Array.Copy(templateData, oe, tplDepTrailer, 0, tplDepTrailer.Length);
        }
        catch (Exception ex) { Log.Debug(ex, "UE5 map template dep parse failed"); tplDepEntries = Array.Empty<byte>(); tplDepTrailer = Array.Empty<byte>(); }

        // Level payload: preamble split, native Actors array lives past None.
        var lvlRaw = new byte[(int)tpkg.ExportMap[lvlExport].SerialSize];
        Array.Copy(templateData, (int)tpkg.ExportMap[lvlExport].SerialOffset, lvlRaw, 0, lvlRaw.Length);
        int lvlPre = NodePayloadWalker.FindPayloadStart(lvlRaw, tpkg.NameMap.Length);
        int lvlPostNone;
        {
            var body = new byte[lvlRaw.Length - lvlPre];
            Array.Copy(lvlRaw, lvlPre, body, 0, body.Length);
            lvlPostNone = lvlPre + NodePayloadWalker.SkipTaggedProperties(body, 0, noneIdx);
        }

        int baseExport = baseExp;
        // Per-item export slots: standard items take [component, actor]; PlayerStart items take
        // [arrow, capsule, billboard, billboard, actor] (verbatim template subobjects + synth actor).
        var itemComp = new List<int>(); var itemActor = new List<int>(); var itemSubs = new List<int[]>();
        {
            int next = baseExport;
            foreach (var a in items)
            {
                bool ps = a.actorPkg == "/Script/Engine" && a.actorClass == "PlayerStart";
                if (ps) { itemSubs.Add(new[] { next + 1, next + 2, next + 3, next + 4 }); itemComp.Add(0); itemActor.Add(next + 5); next += 5; }
                // ChildActor-wrapped items emit [component, child-spawner, actor].
                else if (a.childBp != null) { itemSubs.Add(Array.Empty<int>()); itemComp.Add(next + 1); itemActor.Add(next + 3); next += 3; }
                else { itemSubs.Add(Array.Empty<int>()); itemComp.Add(next + 1); itemActor.Add(next + 2); next += 2; }
            }
        }
        var newActorPkgs = itemActor.ToList();
        // Keep the template WorldSettings referenced (a level without one is invalid); every other template
        // demo actor is dropped by the replace (map-exactness: no Floor/skybox/PlayerStart clones).
        // NOTE: template exports keep their table indices (FPI = table+1); do NOT add baseExport here
        // (that pointed past the end of small maps -> Array.h:783 load crash).
        int wsTpl = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "WorldSettings");
        if (wsTpl >= 0) newActorPkgs.Insert(0, wsTpl + 1);
        int streamingBaseExport = baseExport + (itemActor.Count > 0 ? itemActor.Max() - baseExport : 0);
        var newStreamingPkgs = new List<int>();
        for (int i = 0; i < streamingAssets.Count; i++) newStreamingPkgs.Add(streamingBaseExport + i + 1);
        int worldPkgIdx = worldExport + 1, lvlPkg = lvlExport + 1;

        // Template PlayerStart subobject payloads (verbatim copies): Arrow, Capsule, 2x Billboard.
        byte[]? arrowPay = null, capsulePay = null; uint arrowFlags = 0x40008, capsuleFlags = 0x40008;
        int arrowCls = 0, capsuleCls = 0; long arrowSS = 0, arrowSE = 0, capsuleSS = 0, capsuleSE = 0;
        var billPay = new List<(byte[] payload, uint flags, int cls, long ss, long se)>();
        for (int ei = 0; ei < tpkg.ExportMap.Length; ei++)
        {
            var ec = tpkg.ExportMap[ei].ClassName;
            if (ec == "ArrowComponent" && arrowPay == null)
            {
                arrowPay = new byte[(int)tpkg.ExportMap[ei].SerialSize];
                Array.Copy(templateData, (int)tpkg.ExportMap[ei].SerialOffset, arrowPay, 0, arrowPay.Length);
                arrowFlags = (uint)tpkg.ExportMap[ei].ObjectFlags; arrowCls = tpkg.ExportMap[ei].ClassIndex?.Index ?? 0;
                arrowSS = tpkg.ExportMap[ei].ScriptSerializationStartOffset; arrowSE = tpkg.ExportMap[ei].ScriptSerializationEndOffset;
            }
            else if (ec == "CapsuleComponent" && capsulePay == null)
            {
                capsulePay = new byte[(int)tpkg.ExportMap[ei].SerialSize];
                Array.Copy(templateData, (int)tpkg.ExportMap[ei].SerialOffset, capsulePay, 0, capsulePay.Length);
                capsuleFlags = (uint)tpkg.ExportMap[ei].ObjectFlags; capsuleCls = tpkg.ExportMap[ei].ClassIndex?.Index ?? 0;
                capsuleSS = tpkg.ExportMap[ei].ScriptSerializationStartOffset; capsuleSE = tpkg.ExportMap[ei].ScriptSerializationEndOffset;
            }
            else if (ec == "BillboardComponent" && billPay.Count < 2)
            {
                var bp = new byte[(int)tpkg.ExportMap[ei].SerialSize];
                Array.Copy(templateData, (int)tpkg.ExportMap[ei].SerialOffset, bp, 0, bp.Length);
                billPay.Add((bp, (uint)tpkg.ExportMap[ei].ObjectFlags, tpkg.ExportMap[ei].ClassIndex?.Index ?? 0,
                    tpkg.ExportMap[ei].ScriptSerializationStartOffset, tpkg.ExportMap[ei].ScriptSerializationEndOffset));
            }
        }
        bool havePlayerStartTpl = arrowPay != null && capsulePay != null && billPay.Count >= 2;
        if (!havePlayerStartTpl) Log.Information("UE5 placement: no PlayerStart subobject templates; PlayerStarts skipped");

        // Export-name collision guard (cooked StaticMeshActor_0 vs template's own): suffix _R.
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in tpkg.ExportMap)
            if ((e.OuterIndex?.Index ?? 0) == lvlPkg || (e.OuterIndex?.Index ?? 0) == worldPkgIdx)
                takenNames.Add(e.ObjectName.Text + "\0" + (e.OuterIndex?.Index ?? 0));
        string UniqueExportName(string want, int outer)
        {
            if (takenNames.Add(want + "\0" + outer)) return want;
            int k = 1;
            while (!takenNames.Add($"{want}_R{k}\0{outer}")) k++;
            return $"{want}_R{k}";
        }

        // Dev bisection: UE4D_TRANSPLANT=1 lists VERBATIM copies of the template's own light actor +
        // comp as new exports (payload-vs-shell attribution, mirroring the K2 transplant).
        if (Environment.GetEnvironmentVariable("UE4D_TRANSPLANT") == "1")
        {
            int tplLightActor = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "DirectionalLight");
            int tplLightComp = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "DirectionalLightComponent");
            if (tplLightActor >= 0 && tplLightComp >= 0)
            {
                var actorRaw = new byte[(int)tpkg.ExportMap[tplLightActor].SerialSize];
                Array.Copy(templateData, (int)tpkg.ExportMap[tplLightActor].SerialOffset, actorRaw, 0, actorRaw.Length);
                var compRaw = new byte[(int)tpkg.ExportMap[tplLightComp].SerialSize];
                Array.Copy(templateData, (int)tpkg.ExportMap[tplLightComp].SerialOffset, compRaw, 0, compRaw.Length);
                var srcA = tpkg.ExportMap[tplLightActor]; var srcC = tpkg.ExportMap[tplLightComp];
                for (int i = 0; i < tpkg.ExportMap.Length; i++)
                {
                    var e = tpkg.ExportMap[i];
                    var payload = new byte[(int)e.SerialSize];
                    Array.Copy(templateData, (int)e.SerialOffset, payload, 0, payload.Length);
                    if (i == lvlExport) payload = PatchLevelActors(payload, lvlPostNone,
                        new List<int> { baseExport + 2 });
                    long scriptEnd = e.ScriptSerializationEndOffset + (payload.Length - (int)e.SerialSize);
                    spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0,
                        e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload,
                        (uint)e.ObjectFlags, e.IsAsset, e.ForcedExport, e.NotForClient, e.NotForServer, e.PackageFlags,
                        e.NotAlwaysLoadedForEditorGame, e.GeneratePublicHash, e.ScriptSerializationStartOffset, scriptEnd);
                }
                int compClsImp = srcC.ClassIndex?.Index ?? 0, actorClsImp = srcA.ClassIndex?.Index ?? 0;
                spw.AddExportRaw(spw.Name("TransplantLightComponent"), 0, compClsImp, 0, 0, baseExport + 2,
                    compRaw, 0x40008, false, false, false, false, 0, true, false,
                    srcC.ScriptSerializationStartOffset, srcC.ScriptSerializationEndOffset);
                spw.AddExportRaw(spw.Name("TransplantLight"), 0, actorClsImp, 0, 0, lvlPkg,
                    actorRaw, 0x8, false, false, false, false, 0, true, false,
                    srcA.ScriptSerializationStartOffset, srcA.ScriptSerializationEndOffset);
                if (tplDepEntries.Length > 0)
                {
                    using var dms2 = new MemoryStream();
                    dms2.Write(tplDepEntries);
                    using (var dw2 = new BinaryWriter(dms2, System.Text.Encoding.ASCII, leaveOpen: true))
                    { dw2.Write(1); dw2.Write(compClsImp); dw2.Write(1); dw2.Write(actorClsImp); }
                    dms2.Write(tplDepTrailer);
                    spw.DependsMapOverride = dms2.ToArray();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
                spw.Write(outFile);
                Log.Information("UE5 transplanted template light into {T} -> {Out}", targetShort, outFile);
                return;
            }
            Log.Warning("UE5 transplant: no DirectionalLight in template");
        }

        for (int i = 0; i < tpkg.ExportMap.Length; i++)
        {
            var e = tpkg.ExportMap[i];
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(templateData, (int)e.SerialOffset, payload, 0, payload.Length);
            if (i == lvlExport) payload = PatchLevelActors(payload, lvlPostNone, newActorPkgs);
            else if (i == worldExport && newStreamingPkgs.Count > 0) payload = PatchWorldStreamingLevels(payload, noneIdx, newStreamingPkgs);
            var scriptStart = e.ScriptSerializationStartOffset;
            var scriptEnd = e.ScriptSerializationEndOffset;
            // Per-map GameMode: splice the recovered DefaultGameMode tag into the template WorldSettings
            // (growing the script region, so EndOffset grows by the same delta).
            if (i == wsExport && !string.IsNullOrEmpty(defaultGameMode))
            {
                var dot = defaultGameMode.LastIndexOf('.');
                if (dot > 0)
                {
                    int gmPkgImp = FindPackageImport(tpkg, defaultGameMode.Substring(0, dot));
                    if (gmPkgImp == 0) gmPkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, defaultGameMode.Substring(0, dot));
                    int gmCls = FindTemplateImport(tpkg, "/Script/CoreUObject", "Class", gmPkgImp, defaultGameMode.Substring(dot + 1));
                    if (gmCls == 0) gmCls = spw.AddImport("/Script/CoreUObject", "Class", gmPkgImp, defaultGameMode.Substring(dot + 1));
                    if (gmCls != 0)
                    {
                        var (wsp, grew) = PatchWorldSettingsGameMode(payload, noneIdx, spw, gmCls);
                        if (grew > 0) { payload = wsp; scriptEnd += grew; }
                    }
                }
            }
            // Script offsets otherwise pass through VERBATIM: Level/World patches land in the native region
            // (post-None), outside the script region — growing EndOffset by the payload delta trips
            // LinkerLoad 6324 (proven by the transplant run: verbatim bytes + grown End = assert).
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0,
                e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload,
                (uint)e.ObjectFlags, e.IsAsset, e.ForcedExport, e.NotForClient, e.NotForServer, e.PackageFlags,
                e.NotAlwaysLoadedForEditorGame, e.GeneratePublicHash, scriptStart, scriptEnd);
        }
        // UE5 validates the per-export dependency lists on load. Reuse the template's REAL depends map (an
        // all-empty one desyncs the linker, which then mis-parses export payloads — e.g. the WorldSettings
        // NavigationSystemConfig soft-class struct). Zero-count entries are appended for the exports added below.
        spw.TemplateDependsBytes = tplDepEntries.Length > 0 ? tplDepEntries : null;
        spw.TemplateDependsExportCount = baseExp;

        var pkgImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var classImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
        int ClassImp(string pkgPath, string cls)
        {
            var key = pkgPath + "." + cls;
            if (classImpCache.TryGetValue(key, out var c)) return c;
            if (!pkgImpCache.TryGetValue(pkgPath, out var pImp))
            {
                pImp = FindPackageImport(tpkg, pkgPath);
                if (pImp == 0) pImp = spw.AddImport("/Script/CoreUObject", "Package", 0, pkgPath);
                pkgImpCache[pkgPath] = pImp;
            }
            c = FindTemplateImport(tpkg, "/Script/CoreUObject", "Class", pImp, cls);
            if (c == 0) c = spw.AddImport("/Script/CoreUObject", "Class", pImp, cls);
            classImpCache[key] = c; return c;
        }
        int MeshImp(string meshPkg, string meshName)
        {
            if (!pkgImpCache.TryGetValue(meshPkg, out var mp))
            { mp = spw.AddImport("/Script/CoreUObject", "Package", 0, meshPkg); pkgImpCache[meshPkg] = mp; }
            return spw.AddImport("/Script/Engine", "StaticMesh", mp, meshName);
        }

        var depClassImps = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            var a = items[i];
            bool pstart = itemSubs[i].Length == 4 && havePlayerStartTpl;
            if (itemSubs[i].Length == 4 && !havePlayerStartTpl) continue;   // no subobject templates
            int compPkg = itemComp[i], actorPkg = itemActor[i];
            bool lvlInst = a.actorPkg == "/Script/Engine" && a.actorClass == "LevelInstance" && a.worldAsset != null;
            if (lvlInst)
            {
                // LevelInstance: fresh SceneComponent root + actor carrying the embedded WorldAsset.
                // (Verifies the UE5 SoftObject encoding; ground truth: ArrowComponent 8B tail.)
                byte[] liCompPayload;
                long liCompScriptEnd;
                using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                    var t = new TaggedPropertyWriter(w, spw.Name, true);
                    t.VectorD("RelativeLocation", a.loc[0], a.loc[1], a.loc[2]);
                    if (a.rot[0] != 0 || a.rot[1] != 0 || a.rot[2] != 0) t.RotatorD("RelativeRotation", a.rot[0], a.rot[1], a.rot[2]);
                    if (a.scale[0] != 1 || a.scale[1] != 1 || a.scale[2] != 1) t.VectorD("RelativeScale3D", a.scale[0], a.scale[1], a.scale[2]);
                    t.WriteNone();
                    liCompScriptEnd = ms.Position;
                    for (int zb = 0; zb < 8; zb++) w.Write((byte)0);
                    w.Flush();
                    var body = ms.ToArray();
                    liCompPayload = new byte[1 + body.Length];
                    liCompPayload[0] = 0;
                    Array.Copy(body, 0, liCompPayload, 1, body.Length);
                }
                string liCompName = UniqueExportName(a.compName, actorPkg);
                int liCompCls = ClassImp("/Script/Engine", "SceneComponent");
                spw.AddExportRaw(spw.Name(liCompName), 0, liCompCls, 0, 0, actorPkg, liCompPayload,
                    0x40008, false, false, false, false, 0, true, false, 0, liCompScriptEnd + 1);
                depClassImps.Add(liCompCls);
                byte[] liActorPayload;
                long liActorScriptEnd;
                using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                    var t = new TaggedPropertyWriter(w, spw.Name, true);
                    t.Object("RootComponent", compPkg);
                    t.GuidStruct("ActorGuid", FGuid16.NewGuid());
                    t.Str("ActorLabel", a.label);
                    t.SoftObject("WorldAsset", a.worldAsset!);
                    t.Bool("bIsSpatiallyLoaded", false);
                    if (a.actorHidden) t.Bool("bHidden", true);
                    if (a.editorOnly) t.Bool("bIsEditorOnlyActor", true);
                    t.WriteNone();
                    liActorScriptEnd = ms.Position;
                    for (int zb = 0; zb < 8; zb++) w.Write((byte)0);
                    w.Flush();
                    var body = ms.ToArray();
                    liActorPayload = new byte[1 + body.Length];
                    liActorPayload[0] = 0;
                    Array.Copy(body, 0, liActorPayload, 1, body.Length);
                }
                string liActorName = UniqueExportName(a.label, lvlPkg);
                int liActorCls = ClassImp(a.actorPkg, a.actorClass);
                spw.AddExportRaw(spw.Name(liActorName), 0, liActorCls, 0, 0, lvlPkg, liActorPayload,
                    0x8, false, false, false, false, 0, true, false, 0, liActorScriptEnd + 1);
                depClassImps.Add(liActorCls);
                continue;
            }
            if (pstart)
            {
                // PlayerStart: verbatim template subobjects + synth actor shaped like the template's own.
                var subs = itemSubs[i];
                var subDefs = new (string name, byte[] pay, uint flags, int cls, long ss, long se)[]
                {
                    ("ArrowComponent", arrowPay!, arrowFlags, arrowCls, arrowSS, arrowSE),
                    ("CapsuleComponent", capsulePay!, capsuleFlags, capsuleCls, capsuleSS, capsuleSE),
                    ("BillboardComponent", billPay[0].payload, billPay[0].flags, billPay[0].cls, billPay[0].ss, billPay[0].se),
                    ("BillboardComponent", billPay[1].payload, billPay[1].flags, billPay[1].cls, billPay[1].ss, billPay[1].se),
                };
                for (int s = 0; s < 4; s++)
                {
                    string subName = UniqueExportName(subDefs[s].name + "_PS" + i, actorPkg);
                    spw.AddExportRaw(spw.Name(subName), 0, subDefs[s].cls, 0, 0, actorPkg, subDefs[s].pay,
                        subDefs[s].flags, false, false, false, false, 0, true, false, subDefs[s].ss, subDefs[s].se);
                    depClassImps.Add(subDefs[s].cls);
                }
                byte[] psActorPayload;
                long psActorScriptEnd;
                using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                    var t = new TaggedPropertyWriter(w, spw.Name, true);
                    t.Object("ArrowComponent", subs[0]);
                    t.Object("CapsuleComponent", subs[1]);
                    t.Object("GoodSprite", subs[2]);
                    t.Object("BadSprite", subs[3]);
                    t.Object("RootComponent", subs[1]);
                    t.GuidStruct("ActorGuid", FGuid16.NewGuid());
                    t.Str("ActorLabel", a.label);
                    t.Bool("bIsSpatiallyLoaded", false);
                    if (a.actorHidden) t.Bool("bHidden", true);
                    if (a.editorOnly) t.Bool("bIsEditorOnlyActor", true);
                    t.WriteNone();
                    psActorScriptEnd = ms.Position;
                    for (int zb = 0; zb < 8; zb++) w.Write((byte)0);
                    w.Flush();
                    var body = ms.ToArray();
                    psActorPayload = new byte[1 + body.Length];
                    psActorPayload[0] = 0;
                    Array.Copy(body, 0, psActorPayload, 1, body.Length);
                }
                string psActorName = UniqueExportName(a.label, lvlPkg);
                int psActorCls = ClassImp(a.actorPkg, a.actorClass);
                spw.AddExportRaw(spw.Name(psActorName), 0, psActorCls, 0, 0, lvlPkg, psActorPayload,
                    0x8, false, false, false, false, 0, true, false, 0, psActorScriptEnd + 1);
                depClassImps.Add(psActorCls);
                continue;
            }
            bool isLight = a.actorClass.EndsWith("Light", StringComparison.Ordinal);
            string compTypedName = a.compClass;   // e.g. StaticMeshComponent / PointLightComponent
            bool hasMesh = a.meshPkg != null && a.meshName != null && a.compClass == "StaticMeshComponent"
                && MeshOnDisk(a.meshPkg, a.meshName);
            if (a.meshPkg != null && !hasMesh && a.compClass == "StaticMeshComponent") missingMesh++;
            int meshObjImp = hasMesh ? MeshImp(a.meshPkg!, a.meshName!) : 0;

            int[] overrideMatImps = System.Array.Empty<int>();
            if (a.compClass == "StaticMeshComponent" && a.overrideMats.Count > 0)
            {
                overrideMatImps = new int[a.overrideMats.Count];
                for (int mi = 0; mi < a.overrideMats.Count; mi++)
                {
                    var (mpkg, mname, mcls) = a.overrideMats[mi];
                    if (mpkg == null || mname == null) { overrideMatImps[mi] = 0; continue; }
                    if (!pkgImpCache.TryGetValue(mpkg, out var mp)) { mp = spw.AddImport("/Script/CoreUObject", "Package", 0, mpkg); pkgImpCache[mpkg] = mp; }
                    overrideMatImps[mi] = spw.AddImport("/Script/Engine", string.IsNullOrEmpty(mcls) ? "MaterialInstanceConstant" : mcls, mp, mname);
                }
            }

            // ---- component ----
            byte[] compPayload;
            long compScriptEnd;
            // Dev bisection: UE4D_MINCOMP=1 emits Intensity+Location only (isolates tag groups).
            bool minComp = Environment.GetEnvironmentVariable("UE4D_MINCOMP") == "1";
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name, true);
                if (meshObjImp != 0) t.Object("StaticMesh", meshObjImp);
                if (!minComp && a.compClass == "StaticMeshComponent" && a.overrideMats.Count > 0)
                    t.ObjectArray("OverrideMaterials", overrideMatImps);
                if (IsLightComponent(a.compClass))
                {
                    float intensity = MathF.PI;
                    try
                    {
                        if (a.sourceComp is CUE4Parse.UE4.Assets.Exports.Component.Lights.ULightComponentBase lcb)
                            intensity = lcb.Intensity;
                        else intensity = a.sourceComp.GetOrDefault("Intensity", a.sourceComp.GetOrDefault("Brightness", MathF.PI));
                    }
                    catch { }
                    t.Float("Intensity", intensity);
                    // Movable is REQUIRED: the editor defaults placed lights to Static, and static lights
                    // contribute nothing without baked lighting (our maps bind no build data) — the level
                    // renders black except emissive. (Matches the other placement path's Movable tag.)
                    t.ByteEnum("Mobility", "EComponentMobility::Type", "EComponentMobility::Movable");
                }
                if (!minComp && !a.compVisible) t.Bool("bVisible", false);
                if (!minComp && a.compHiddenInGame) t.Bool("bHiddenInGame", true);
                // NOTE: Mobility IS written on light components (see above). Static is the editor
                // default and contributes nothing without baked lighting; Movable is required.
                if (!minComp && a.absLoc) t.Bool("bAbsoluteLocation", true);
                if (!minComp && a.absRot) t.Bool("bAbsoluteRotation", true);
                if (!minComp && a.absScale) t.Bool("bAbsoluteScale", true);
                t.VectorD("RelativeLocation", a.loc[0], a.loc[1], a.loc[2]);
                if (!minComp && (a.rot[0] != 0 || a.rot[1] != 0 || a.rot[2] != 0)) t.RotatorD("RelativeRotation", a.rot[0], a.rot[1], a.rot[2]);
                if (!minComp && (a.scale[0] != 1 || a.scale[1] != 1 || a.scale[2] != 1)) t.VectorD("RelativeScale3D", a.scale[0], a.scale[1], a.scale[2]);
                t.WriteNone();
                compScriptEnd = ms.Position;
                // Native tail (zero-filled): StaticMeshComponent 12B, other components 8B. Ground truth
                // (Floor comp 12B, exercised in PIE) + LinkerLoad 4856 arbitration (plain-shape 16B fails
                // Got/Expected by exactly 4B) — uniform 12B regardless of override arrays.
                int tailLen = a.compClass == "StaticMeshComponent" ? 12 : 8;
                for (int zb = 0; zb < tailLen; zb++) w.Write((byte)0);
                w.Flush();
                var body = ms.ToArray();
                compPayload = new byte[1 + body.Length];
                compPayload[0] = 0;   // version preamble (ground truth: 0x00)
                Array.Copy(body, 0, compPayload, 1, body.Length);
            }
            // RootComponent name MUST match the actor class's native subobject name: the linker binds the
            // instance root to the CDO subobject by name, and a source-named root (SceneComp/Root/Box/...)
            // never binds — the actor falls back to a mesh-less default and the placed mesh never shows.
            // (Ground truth: AStaticMeshActor::StaticMeshComponentName == "StaticMeshComponent0"; audits showed
            // only comps already named StaticMeshComponent0 resolving their StaticMesh at runtime.)
            string compExportName = a.actorClass == "StaticMeshActor"
                ? UniqueExportName("StaticMeshComponent0", actorPkg)
                : UniqueExportName(a.compName, actorPkg);
            int compClassImp = ClassImp(a.compPkg, a.compClass);
            spw.AddExportRaw(spw.Name(compExportName), 0, compClassImp, 0, 0, actorPkg, compPayload,
                0x40008, false, false, false, false, 0, true, false, 0, compScriptEnd + 1);
            depClassImps.Add(compClassImp);

            // ChildActor wrapper (mesh-less BP actors): spawn the real BP class at runtime so its
            // class-built content appears. Outered to the actor; identity relative transform (the
            // root component above carries the world placement).
            if (a.childBp != null)
            {
                var dot = a.childBp.LastIndexOf('.');
                int childClsImp = dot > 0 ? ClassImp(a.childBp.Substring(0, dot), a.childBp.Substring(dot + 1)) : 0;
                if (childClsImp != 0)
                {
                    byte[] childPayload;
                    long childScriptEnd;
                    using (var ms2 = new MemoryStream()) { using var w2 = new FArchiveWriter(ms2);
                        var t2 = new TaggedPropertyWriter(w2, spw.Name, true);
                        t2.Object("ChildActorClass", childClsImp);
                        // User-added (non-SCS) components serialize CreationMethod=Instance; without it the
                        // loader treats the export as a native subobject and it never attaches/registers.
                        t2.Enum("CreationMethod", "EComponentCreationMethod", "Instance");
                        t2.WriteNone();
                        childScriptEnd = ms2.Position;
                        for (int zb = 0; zb < 8; zb++) w2.Write((byte)0);
                        w2.Flush();
                        var body2 = ms2.ToArray();
                        childPayload = new byte[1 + body2.Length];
                        childPayload[0] = 0;
                        Array.Copy(body2, 0, childPayload, 1, body2.Length);
                    }
                    int childCompCls = ClassImp("/Script/Engine", "ChildActorComponent");
                    spw.AddExportRaw(spw.Name(UniqueExportName("ChildActor", actorPkg)), 0, childCompCls, 0, 0, actorPkg, childPayload,
                        0x40008, false, false, false, false, 0, true, false, 0, childScriptEnd + 1);
                    depClassImps.Add(childCompCls);
                    depClassImps.Add(childClsImp);
                }
            }

            // ---- actor ----
            byte[] actorPayload;
            long actorScriptEnd;
            // Dev bisection: UE4D_MINACTOR=1 emits Root+Guid+Label only (isolates tag groups).
            bool minActor = Environment.GetEnvironmentVariable("UE4D_MINACTOR") == "1";
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name, true);
                if (!minActor) t.Object(compTypedName, compPkg);
                if (!minActor && isLight) t.Object("LightComponent", compPkg);
                t.Object("RootComponent", compPkg);
                t.GuidStruct("ActorGuid", FGuid16.NewGuid());
                t.Str("ActorLabel", a.label);
                if (!minActor) t.Bool("bIsSpatiallyLoaded", false);
                if (!minActor && a.actorHidden) t.Bool("bHidden", true);
                if (!minActor && a.editorOnly) t.Bool("bIsEditorOnlyActor", true);
                t.WriteNone();
                actorScriptEnd = ms.Position;
                for (int zb = 0; zb < 8; zb++) w.Write((byte)0);   // AActor tail (ground truth: 8B)
                w.Flush();
                var body = ms.ToArray();
                actorPayload = new byte[1 + body.Length];
                actorPayload[0] = 0;
                Array.Copy(body, 0, actorPayload, 1, body.Length);
            }
            string actorExportName = UniqueExportName(a.label, lvlPkg);
            int actorClassImp = ClassImp(a.actorPkg, a.actorClass);
            spw.AddExportRaw(spw.Name(actorExportName), 0, actorClassImp, 0, 0, lvlPkg, actorPayload,
                0x8, false, false, false, false, 0, true, false, 0, actorScriptEnd + 1);
            depClassImps.Add(actorClassImp);
        }

        // ULevelStreaming exports: preserve the cooked streaming class + flags. Emitting a bare
        // LevelStreamingAlwaysLoaded (its bShouldBeLoaded defaults true) made hub maps force-load every
        // sublevel at once ("overloading"). LevelStreamingDynamic honors the preserved flags.
        for (int i = 0; i < streamingAssets.Count; i++)
        {
            var sref = streamingAssets[i];
            using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name, true);
            t.SoftObject("WorldAsset", sref.Asset);
            t.Bool("bShouldBeVisible", sref.Visible);
            t.Bool("bShouldBeVisibleInEditor", sref.VisibleInEditor);
            t.Bool("bShouldBeLoaded", sref.ShouldBeLoaded);
            t.Bool("bIsStatic", sref.IsStatic);
            t.WriteNone();
            long se = ms.Position + 1;   // + version preamble, like nodes/actors
            w.Write(0);
            w.Flush();
            var body = ms.ToArray();
            var payload = new byte[1 + body.Length];
            payload[0] = 0;
            Array.Copy(body, 0, payload, 1, body.Length);
            int clsImp = ClassImp("/Script/Engine", "LevelStreamingDynamic");
            spw.AddExportRaw(spw.Name("LevelStreamingDynamic_" + i), 0, clsImp, 0, 0, worldPkgIdx, payload,
                0x8, false, false, false, false, 0, true, false, 0, se);
            depClassImps.Add(clsImp);
        }

        if (tplDepEntries.Length > 0)
        {
            using var dms = new MemoryStream();
            dms.Write(tplDepEntries);
            using (var dw = new BinaryWriter(dms, System.Text.Encoding.ASCII, leaveOpen: true))
                foreach (var ci in depClassImps) { dw.Write(1); dw.Write(ci); }
            dms.Write(tplDepTrailer);
            spw.DependsMapOverride = dms.ToArray();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        spw.Write(outFile);
        Log.Information("UE5 placed {N} actors into {T} -> {Out} ({M} mesh-less)", items.Count, targetShort, outFile, missingMesh);
    }

    public static void PlaceActors(string cookedPath, string templatePath, string outDir, string targetShort, string targetPackagePath,
        string? cubePath = null, string? contentRoot = null)
    {
        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, targetShort + ".uasset");
        // Dev path: re-parse the local cooked file as a 4.21 Package (which IS an IPackage) and feed it in.
        var ar = new FByteArchive(Path.GetFileNameWithoutExtension(cookedPath), File.ReadAllBytes(cookedPath), new VersionContainer(EGame.GAME_UE4_21));
        var pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        PlaceActorsCore(pkg, File.ReadAllBytes(templatePath), outFile, targetShort, targetPackagePath, cubePath, contentRoot);
    }

    /// <summary>Append object references to a tagged ArrayProperty&lt;ObjectProperty&gt; (e.g. SCS RootNodes/AllNodes)
    /// in a cloned export payload, fixing the array Count and the property Size. No-op if the property isn't present.</summary>
    private static byte[] AppendToObjectArray(byte[] p, int noneIdx, int propNameIdx, IReadOnlyList<int> newPkgs)
    {
        if (NodePayloadWalker.IsUe5)
        {
            if (!NodePayloadWalker.FindTag5(p, noneIdx, propNameIdx, -1,
                    out _, out int sizePos5, out int valueOff5, out int tagEnd5))
                return p;
            int oldCount5 = BitConverter.ToInt32(p, valueOff5);
            var add5 = new byte[newPkgs.Count * 4];
            for (int i = 0; i < newPkgs.Count; i++) BitConverter.GetBytes(newPkgs[i]).CopyTo(add5, i * 4);
            var outp5 = new byte[p.Length + add5.Length];
            Array.Copy(p, 0, outp5, 0, tagEnd5);
            add5.CopyTo(outp5, tagEnd5);
            Array.Copy(p, tagEnd5, outp5, tagEnd5 + add5.Length, p.Length - tagEnd5);
            BitConverter.GetBytes(oldCount5 + newPkgs.Count).CopyTo(outp5, valueOff5);
            BitConverter.GetBytes(BitConverter.ToInt32(outp5, sizePos5) + add5.Length).CopyTo(outp5, sizePos5);
            return outp5;
        }
        var (start, end) = NodePayloadWalker.FindPropertySpan(p, 0, noneIdx, propNameIdx);
        if (start < 0) return p;
        int sizeOff = start + 16;                 // after Name(8) + Type(8)
        int valStart = start + 33;                // + Size(4) + ArrayIndex(4) + InnerType(8) + HasPropertyGuid(1)
        int oldCount = BitConverter.ToInt32(p, valStart);
        var add = new byte[newPkgs.Count * 4];
        for (int i = 0; i < newPkgs.Count; i++) BitConverter.GetBytes(newPkgs[i]).CopyTo(add, i * 4);
        var outp = new byte[p.Length + add.Length];
        Array.Copy(p, 0, outp, 0, end);           // FindPropertySpan end = right after the existing entries
        add.CopyTo(outp, end);
        Array.Copy(p, end, outp, end + add.Length, p.Length - end);
        BitConverter.GetBytes(oldCount + newPkgs.Count).CopyTo(outp, valStart);
        BitConverter.GetBytes(BitConverter.ToInt32(outp, sizeOff) + add.Length).CopyTo(outp, sizeOff);
        return outp;
    }

    /// <summary>Byte-based core: place a cooked map's reliably-loadable actors onto a template map, writing to <paramref name="outFile"/>.
    /// Filters out plugin/BP-class actors (e.g. /CustomMapTools/) so the result opens without missing-import crashes.</summary>
    private static int ChildBpRejectShown;
    private static readonly Dictionary<string, (string pkg, string name)?> BpCdoMeshCache = new(StringComparer.Ordinal);
    /// <summary>Mesh package+name -> default material slots (StaticMaterials), loaded once per mesh from the
    /// source package and cached for the run. Lets placed components that carry no per-instance overrides
    /// show the mesh's real materials instead of the import's gray fallback.</summary>
    private static readonly Dictionary<string, List<(string? pkg, string? name, string? cls)>?> MeshMatsCache = new(StringComparer.Ordinal);
    public static void PlaceActorsCore(CUE4Parse.UE4.Assets.IPackage src, byte[] templateData, string outFile,
        string targetShort, string targetPackagePath, string? cubePath = null, string? contentRoot = null,
        Action<string, string, string>? onGameClass = null, bool isUe5 = false,
        CUE4Parse.FileProvider.AbstractFileProvider? provider = null)
    {
        // Gather from the already-LOADED source package (uniform for legacy Package AND Zen IoPackage — Zen raw
        // bytes can't be re-parsed standalone). Exports carry Outer/Class ResolvedObjects + parsed properties.
        ChildBpRejectShown = 0;
        var srcExports = src.GetExports().ToList();

        // Gather placed actors (outer=PersistentLevel, actor-ish class) + their root component transform.
        var skip = new HashSet<string> { "Model", "Brush", "Polys", "Level", "World", "WorldSettings",
            "NavigationSystemModuleConfig", "BlueprintGeneratedClass", "None", "RecastNavMesh",
            "PhononProbeVolume", "NavLinkProxy" };   // plugin/custom-component classes that fail to load
        // Place actors from any /Script/ module. Game-native actor classes (/Script/Pavlov.*) get a stub, and we
        // report each as needing an AActor base (its component -> USceneComponent) via onGameClass so the stub is
        // actually spawnable — placing one whose stub defaulted to UObject is what crashed the editor before.
        var place = new List<(string actorPkg, string actorClass, string compPkg, string compClass, string compName, string label, float[] loc, float[] rot, float[] scale, string? meshPkg, string? meshName, string? worldAsset, List<(string? pkg, string? name, string? cls)> overrideMats, bool actorHidden, bool compVisible, bool compHiddenInGame, bool editorOnly, bool absLoc, bool absRot, bool absScale, string? textValue, float worldSize, (string? pkg, string? name, string? cls) decalMat, float[] decalSize, CUE4Parse.UE4.Objects.Core.Misc.FGuid mapBuildId, CUE4Parse.UE4.Assets.Exports.UObject sourceComp, string? childBp)>();
        foreach (var e in srcExports)
        {
            // Top-level actor = export whose Outer is the PersistentLevel.
            if (e.Outer?.Name.Text != "PersistentLevel") continue;
            var cls = e.ExportType;
            if (skip.Contains(cls) || cls.EndsWith("Component")) continue;
            var actorPkg = ScriptPackageOf(e.Class);                     // "/Script/Engine" / "/Script/A2" / null (BP class)
            var bpClass = actorPkg == null ? ObjectPackageAndName(e.Class) : null;
            bool isBp = bpClass is { pkg: not null, name: not null };     // BlueprintGeneratedClass actor (a placed BP prop)
            var actorCls = cls;
            // This actor's component children (names are unique within the level).
            var comps = srcExports.Where(c => c.Outer?.Name.Text == e.Name && c.ExportType.EndsWith("Component")).ToList();
            // Use the actor's ACTUAL RootComponent — its RelativeLocation/Rotation/Scale IS the world placement
            // transform (a root has no parent). FirstOrDefault() can return a non-root child (which sits at its local
            // origin), which is why BP actors kept their rotation but landed at 0,0,0.
            var rootRef = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("RootComponent")?.ResolvedObject;
            var rootComp = (rootRef != null ? comps.FirstOrDefault(c => c.Name == rootRef.Name) : null)
                ?? comps.FirstOrDefault();
            if (rootComp == null) continue;
            // The mesh often lives on a child StaticMeshComponent (esp. for BP actors), not the root. Prefer one that
            // actually has a StaticMesh so BP props place their real mesh instead of an empty StaticMeshActor.
            var meshComp = comps.FirstOrDefault(c => c.ExportType.Contains("StaticMesh")
                && c.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh")?.ResolvedObject != null) ?? rootComp;
            var compPkg = ScriptPackageOf(rootComp.Class);
            var compCls = rootComp.ExportType;
            // LevelInstance actors embed a sublevel via a WorldAsset soft-pointer; capture it so we can re-assign the
            // level on the placed actor (otherwise the LevelInstance is empty). Read off the actor (not the component).
            string? worldAsset = null;
            try
            {
                // Cooked LevelInstances store the level ref in CookedWorldAsset (the editor WorldAsset is stripped on
                // cook); we read that but WRITE it back as the editor's WorldAsset property below.
                var wa = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath>("CookedWorldAsset").ToString();
                if (string.IsNullOrEmpty(wa) || wa == "None")
                    wa = e.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath>("WorldAsset").ToString();
                if (!string.IsNullOrEmpty(wa) && wa != "None") worldAsset = wa;
            }
            catch { }
            // Still record the real base (helps stub generation for refs elsewhere)... (skip BP/unresolved classes).
            if (actorPkg != null) onGameClass?.Invoke(actorPkg, actorCls, "AActor");
            if (isBp) ReportPlacedBlueprintNativeSuper(e.Class, onGameClass);
            if (compPkg != null) onGameClass?.Invoke(compPkg, compCls, "USceneComponent");
            // ...but for PLACEMENT, substitute a guaranteed-loaded engine class for any game-native actor/component.
            // Placing a /Script/Pavlov actor requires its stub module to be COMPILED; if the user hasn't rebuilt the
            // C++ project the class is unresolved and the editor crashes spawning it. A StaticMeshActor placeholder
            // (keeping transform + mesh + original name as label) spawns unconditionally — no recompile needed.
            if (worldAsset != null)
            {
                // Level-instance-like actor: keep it as a plain engine LevelInstance (root = SceneComponent) so its
                // WorldAsset stays meaningful — substituting to StaticMeshActor would drop the embedded level.
                actorPkg = "/Script/Engine"; actorCls = "LevelInstance";
                if (compPkg != "/Script/Engine") { compPkg = "/Script/Engine"; compCls = "SceneComponent"; }
            }
            else if (actorCls.EndsWith("ReflectionCapture", StringComparison.Ordinal) ||
                     compCls.EndsWith("ReflectionCaptureComponent", StringComparison.Ordinal))
            {
                actorPkg = "/Script/Engine"; actorCls = "Actor";
                compPkg = "/Script/Engine"; compCls = "SceneComponent";
            }
            else if (isBp && bpClass is { pkg: { } bpPkg, name: { } bpName })
            {
                if (isUe5)
                {
                    // UE5 v1: substitute by native Engine super (mesh fallback), like game-native actors.
                    // Real BP placement needs SCS-shape serialization the UE5 path doesn't model yet.
                    var ns = FindEngineNativeSuper(e.Class);
                    var nsCls = ns != null && ns.LastIndexOf('.') > 0 ? ns.Substring(ns.LastIndexOf('.') + 1) : null;
                    bool nsLight = nsCls?.EndsWith("Light", StringComparison.Ordinal) == true;
                    if (nsLight && compPkg == "/Script/Engine" && compCls.EndsWith("LightComponent", StringComparison.Ordinal))
                    { actorPkg = "/Script/Engine"; actorCls = nsCls!; }
                    else if (nsLight)
                    { actorPkg = "/Script/Engine"; actorCls = nsCls!; compPkg = "/Script/Engine"; compCls = nsCls + "Component"; }
                    else { actorPkg = "/Script/Engine"; actorCls = "StaticMeshActor"; compPkg = "/Script/Engine"; compCls = "StaticMeshComponent"; }
                }
                else
                {
                    actorPkg = bpPkg;
                    actorCls = bpName;
                    if (compPkg != "/Script/Engine") { compPkg = "/Script/Engine"; compCls = "SceneComponent"; }
                }
            }
            else if (actorCls.EndsWith("TextRenderActor", StringComparison.Ordinal) || compCls.Contains("TextRenderComponent", StringComparison.Ordinal))
            {
                // Game text actors (AxTextRenderActor) subclass the engine ATextRenderActor (root = UTextRenderComponent
                // named "NewTextRenderComponent"). Substitute to the engine TextRenderActor and re-emit the Text +
                // WorldSize so the floating text renders, instead of a stray cube/empty StaticMeshActor.
                actorPkg = "/Script/Engine"; actorCls = "TextRenderActor";
                compPkg = "/Script/Engine"; compCls = "TextRenderComponent";
            }
            else if (actorCls.EndsWith("DecalActor", StringComparison.Ordinal) || compCls.Contains("DecalComponent", StringComparison.Ordinal))
            {
                // DecalActor's root is a UDecalComponent ("NewDecalComponent"). Keep it a DecalComponent (NOT downgraded
                // to SceneComponent) and re-emit DecalMaterial + DecalSize so the decal actually projects its material.
                actorPkg = "/Script/Engine"; actorCls = "DecalActor";
                compPkg = "/Script/Engine"; compCls = "DecalComponent";
            }
            else if (actorCls == "LODActor" || actorCls.EndsWith("PremergedMeshActor", StringComparison.Ordinal))
            {
                // Cooked merged geometry: ALODActor / game premerged actors carry the VISIBLE level mesh
                // (the original props are hidden behind them). Place as a plain mesh actor.
                actorPkg = "/Script/Engine"; actorCls = "StaticMeshActor"; compPkg = "/Script/Engine"; compCls = "StaticMeshComponent";
            }
            else if (actorPkg != "/Script/Engine") { actorPkg = "/Script/Engine"; actorCls = "StaticMeshActor"; compPkg = "/Script/Engine"; compCls = "StaticMeshComponent"; }
            else if (compPkg != "/Script/Engine") { compPkg = "/Script/Engine"; compCls = "SceneComponent"; }
            try
            {
                var label = e.Name;
                var loc = ReadVec(rootComp, "RelativeLocation", 0);
                var rot = ReadVec(rootComp, "RelativeRotation", 0);
                var scl = ReadVec(rootComp, "RelativeScale3D", 1);
                // Visibility / enabled state — a source actor disabled/hidden in the cooked world must not come back
                // fully visible. AActor.bHidden (not rendered in game) + bIsEditorOnlyActor; the rendering component's
                // bVisible (editor "eye" + render) and bHiddenInGame. Read the mesh-bearing component (== root when no
                // separate mesh) since that's what actually draws.
                bool actorHidden = false, compVisible = true, compHiddenInGame = false, editorOnly = false;
                try
                {
                    actorHidden = e.GetOrDefault<bool>("bHidden", false);
                    editorOnly  = e.GetOrDefault<bool>("bIsEditorOnlyActor", false);
                    compVisible = meshComp.GetOrDefault<bool>("bVisible", true);
                    compHiddenInGame = meshComp.GetOrDefault<bool>("bHiddenInGame", false);
                }
                catch { }
                // Absolute (world-space) transform flags. USceneComponent always serializes the value under the
                // "Relative*" names, but when bAbsolute* is set the value is interpreted as WORLD, not relative-to-parent.
                // We read off the SAME component whose transform we emit (rootComp) and pass the flags through so a
                // world-anchored actor isn't silently re-parented into relative space.
                bool absLoc = false, absRot = false, absScale = false;
                try
                {
                    absLoc   = rootComp.GetOrDefault<bool>("bAbsoluteLocation", false);
                    absRot   = rootComp.GetOrDefault<bool>("bAbsoluteRotation", false);
                    absScale = rootComp.GetOrDefault<bool>("bAbsoluteScale", false);
                }
                catch { }
                // Built-lighting key: the mesh component's LODData[0].MapBuildDataId is what the UMapBuildDataRegistry
                // is keyed by. Carry it onto the synthesized component so the recovered lightmaps actually bind.
                CUE4Parse.UE4.Objects.Core.Misc.FGuid mapBuildId = default;
                try
                {
                    if (meshComp is CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UStaticMeshComponent usmc
                        && usmc.LODData is { Length: > 0 })
                        mapBuildId = usmc.LODData[0].MapBuildDataId;
                }
                catch { }
                string? meshPkg = null, meshName = null;
                List<(string? pkg, string? name, string? cls)>? meshDefaultMats = null;
                string? textValue = null; float worldSize = 100f;
                (string? pkg, string? name, string? cls) decalMat = (null, null, null); float[] decalSize = { 128f, 256f, 256f };
                bool isTextRender = actorCls == "TextRenderActor";
                bool isDecal = actorCls == "DecalActor";
                if (isTextRender)
                {
                    // Text actor: capture the displayed string + size off the (Ax)TextRenderComponent; no mesh.
                    try
                    {
                        textValue = meshComp.GetOrDefault<CUE4Parse.UE4.Objects.Core.i18N.FText>("Text")?.Text;
                        worldSize = meshComp.GetOrDefault<float>("WorldSize", 100f);
                    }
                    catch { }
                    compPkg = "/Script/Engine"; compCls = "TextRenderComponent";
                }
                else if (isDecal)
                {
                    // Decal actor: capture DecalMaterial (the projected material) + DecalSize off the DecalComponent; no mesh.
                    try
                    {
                        var dm = meshComp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("DecalMaterial")?.ResolvedObject;
                        if (dm != null) decalMat = (PackagePathOfResolved(dm), dm.Name.Text, dm.Class?.Name.Text);
                        var ds = meshComp.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("DecalSize");
                        if (ds.X != 0 || ds.Y != 0 || ds.Z != 0) decalSize = new[] { (float)ds.X, (float)ds.Y, (float)ds.Z };
                    }
                    catch { }
                    compPkg = "/Script/Engine"; compCls = "DecalComponent";
                }
                else
                {
                // Resolve the StaticMesh objref from the mesh-bearing component (works for Zen + legacy). For BP actors
                // this is a child StaticMeshComponent; for a plain StaticMeshActor it's the root.
                var mi = meshComp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh")?.ResolvedObject;
                if (mi != null)
                {
                    meshName = mi.Name.Text;
                    meshPkg = PackagePathOfResolved(mi);
                    // Meshes are re-imported into a package named after the mesh itself; an inner mesh of a
                    // multi-asset package (HLOD: geometry inside the material package) therefore lives in a
                    // DIFFERENT package than the cooked reference. Point at where the import actually lands.
                    if (!string.IsNullOrEmpty(meshPkg) && !string.IsNullOrEmpty(meshName))
                    {
                        var slash = meshPkg.LastIndexOf('/');
                        if (slash > 0 && !meshPkg.EndsWith("/" + meshName, StringComparison.Ordinal))
                            meshPkg = meshPkg.Substring(0, slash + 1) + meshName;
                    }
                // Capture the mesh's own default materials (StaticMaterials slots). Most map instances carry
                // NO per-instance overrides (rely on mesh defaults); without these the placed component shows
                // the import's gray fallback. Same-package refs are dropped (self-import corrupts).
                // DIAG counters (UE4D_LOGMESHLESS=1): why the fill does/doesn't fire.
                int mdDiag = 0; int mdSlots = -1; int mdResolved = -1;
                try
                {
                    if (!mi.TryLoad<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>(out var usmDbg)) mdDiag = 1;
                    else if (usmDbg?.StaticMaterials == null) mdDiag = 2;
                    else
                    {
                        mdSlots = usmDbg.StaticMaterials.Length;
                        mdResolved = 0;
                        foreach (var ss in usmDbg.StaticMaterials)
                            if (ss.MaterialInterface != null) mdResolved++;
                    }
                }
                catch { mdDiag = 3; }
                if (Environment.GetEnvironmentVariable("UE4D_LOGMESHLESS") == "1")
                    Log.Information("  meshdef {Actor} diag={D} slots={S} resolved={R}", e.Name, mdDiag, mdSlots, mdResolved);
                try
                {
                    if (mi.TryLoad<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>(out var usm)
                        && usm?.StaticMaterials != null)
                        {
                            var defs = new List<(string? pkg, string? name, string? cls)>();
                            foreach (var s in usm.StaticMaterials)
                            {
                                var mro = s.MaterialInterface;
                                if (mro == null) { defs.Add((null, null, null)); continue; }
                                var mp = PackagePathOfResolved(mro);
                                if (string.IsNullOrEmpty(mp) || string.Equals(mp, meshPkg, StringComparison.OrdinalIgnoreCase))
                                { defs.Add((null, null, null)); continue; }
                                // Inner materials (HLOD flattened MIs live inside the cluster package) are emitted
                                // as standalone dir/<MIName> assets — UNLESS the package exists on disk with its
                                // inners intact (uncooked fallback). Point at where the material actually lands.
                                var mn = mro.Name.Text;
                                if (!mp.EndsWith("/" + mn, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(contentRoot))
                                {
                                    var pkgFile = mp.StartsWith("/Game/", StringComparison.Ordinal)
                                        ? Path.Combine(contentRoot, mp.Substring("/Game/".Length) + ".uasset") : null;
                                    if (pkgFile == null || !File.Exists(pkgFile))
                                    {
                                        var dslash = mp.LastIndexOf('/');
                                        if (dslash > 0) mp = mp.Substring(0, dslash + 1) + mn;
                                    }
                                }
                                defs.Add((mp, mro.Name.Text, mro.Class?.Name.Text));
                            }
                            while (defs.Count > 0 && defs[^1].pkg == null) defs.RemoveAt(defs.Count - 1);
                            if (!defs.All(m => m.pkg == null)) meshDefaultMats = defs;
                        }
                    }
                    catch { }
                }
                if (IsHlodOrStandinPath(meshPkg)) continue;
                if (meshName == null && Environment.GetEnvironmentVariable("UE4D_LOGMESHLESS") == "1")
                {
                    // Categorize WHY this placed actor ends up mesh-less: skeletal mesh? child-actor BP?
                    // no mesh-bearing component at all? (Volumes/targets are legitimately mesh-less.)
                    bool hasSkel = false, hasChild = false;
                    string? skelRef = null, childRef = null;
                    try
                    {
                        foreach (var c in comps)
                        {
                            var ct = c.ExportType ?? "";
                            if (ct.Contains("SkeletalMeshComponent") || ct.Contains("SkeletalMesh"))
                            {
                                hasSkel = true;
                                skelRef ??= c.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("SkeletalMesh")?.ResolvedObject?.GetPathName();
                            }
                            if (ct.Contains("ChildActorComponent"))
                            {
                                hasChild = true;
                                childRef ??= c.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("ChildActorClass")?.ResolvedObject?.GetPathName();
                            }
                        }
                    }
                    catch { }
                    Log.Information("  meshless {Actor} root={Root} meshcomp={MC} skel={S} skelref={SR} child={C} childref={CR} rawSM={RAW} ncomps={NC} rootref={RR}",
                        e.Name, rootComp.ExportType, meshComp.ExportType, hasSkel, skelRef, hasChild, childRef,
                        meshComp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh") is { } raws
                            ? $"idx={raws.Index} name={raws.Name} resolved={(raws.ResolvedObject == null ? "null" : raws.ResolvedObject.GetPathName())}" : "<no-prop>",
                        comps.Count, rootRef?.Name.Text);
                    if (e.Name.StartsWith("StaticMeshActor_10", StringComparison.Ordinal))
                        foreach (var cc in comps)
                            Log.Information("    comp {CN} outer={CO} type={CT} sm={SM}",
                                cc.Name, cc.Outer?.Name.Text, cc.ExportType,
                                cc.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh") is { } r2
                                    ? $"idx={r2.Index} resolved={(r2.ResolvedObject == null ? "null" : r2.ResolvedObject.GetPathName())}" : "<no-prop>");
                }
                // Fallback: the instance carries no mesh override, but the visual usually lives on the BP's CDO
                // (class-built StaticMeshComponent). Scan the source BP package (cached per class) for the first
                // convertible StaticMesh so the wrapper shows the prop instead of staying empty. The ChildActor
                // wrapper (below) still spawns the BP for logic/volumes/child content.
                if (meshName == null && !isTextRender && !isDecal && provider != null
                    && ObjectPackageAndName(e.Class) is { pkg: string cbpp } && cbpp.StartsWith("/Game/", StringComparison.Ordinal))
                {
                    try
                    {
                        if (!BpCdoMeshCache.TryGetValue(cbpp, out var cached))
                        {
                            cached = null;
                            try
                            {
                                // Provider keys are pak-relative ("A2/Content/..."), not "/Game/..." — match by suffix.
                                var rel = cbpp.Substring("/Game/".Length);
                                var key = provider.Files.Keys.FirstOrDefault(k =>
                                    k.EndsWith("/" + rel + ".uasset", StringComparison.OrdinalIgnoreCase));
                                if (key != null)
                                {
                                    var bpPack = provider.LoadPackage(key);
                                    foreach (var o in bpPack.GetExports())
                                    {
                                        if (!(o.ExportType?.Contains("Component") ?? false)) continue;
                                        var cmi = o.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh")?.ResolvedObject;
                                        if (cmi == null) continue;
                                        var cmName = cmi.Name.Text;
                                        var cmPkg = PackagePathOfResolved(cmi);
                                        if (string.IsNullOrEmpty(cmPkg) || string.IsNullOrEmpty(cmName)) continue;
                                        var cslash = cmPkg.LastIndexOf('/');
                                        if (cslash > 0 && !cmPkg.EndsWith("/" + cmName, StringComparison.Ordinal))
                                            cmPkg = cmPkg.Substring(0, cslash + 1) + cmName;
                                        cached = (cmPkg, cmName);
                                        // Also lift the CDO component's material overrides (the class-authored look).
                                        try
                                        {
                                            var comArr = o.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OverrideMaterials");
                                            if (comArr != null)
                                            {
                                                var cdefs = new List<(string? pkg, string? name, string? cls)>();
                                                foreach (var com in comArr)
                                                {
                                                    var cro = com?.ResolvedObject;
                                                    cdefs.Add(cro != null ? (PackagePathOfResolved(cro), cro.Name.Text, cro.Class?.Name.Text) : (null, null, null));
                                                }
                                                while (cdefs.Count > 0 && cdefs[^1].pkg == null) cdefs.RemoveAt(cdefs.Count - 1);
                                                if (!cdefs.All(m => m.pkg == null)) meshDefaultMats = cdefs;
                                            }
                                        }
                                        catch { }
                                        break;
                                    }
                                }
                            }
                            catch { }
                            BpCdoMeshCache[cbpp] = cached;
                        }
                        if (cached is { } cm && !string.IsNullOrWhiteSpace(contentRoot))
                        {
                            // Same gate as placed meshes: the importable .glb sidecar must exist.
                            var cuasset = cm.pkg.StartsWith("/Game/", StringComparison.Ordinal)
                                ? Path.Combine(contentRoot, cm.pkg.Substring("/Game/".Length) + ".uasset") : null;
                            if ((cuasset != null && cm.pkg.StartsWith("/Engine/", StringComparison.Ordinal))
                                || (cuasset != null && File.Exists(cuasset) && File.Exists(Path.ChangeExtension(cuasset, ".glb"))))
                            {
                                meshPkg = cm.pkg; meshName = cm.name;
                                if (Environment.GetEnvironmentVariable("UE4D_LOGMESHLESS") == "1")
                                    Log.Information("  cdomesh {Actor} <- {M}", e.Name, cm.pkg + "." + cm.name);
                            }
                        }
                    }
                    catch { }
                }
                compPkg = "/Script/Engine";
                compCls = NormalizeSynthComponentClass(compCls, meshName != null);
                }
                // AStaticMeshActor's root is ALWAYS a UStaticMeshComponent (a native root subobject). If the actor has
                // no resolvable mesh, NormalizeSynthComponentClass downgrades the component to SceneComponent — but then
                // the editor deserializes AStaticMeshActor's StaticMeshComponent over our SceneComponent export, reads a
                // native count from the wrong bytes (a transform float), and floods reads to EOF on map load. Keep the
                // root a StaticMeshComponent (a mesh-less one is valid — empty StaticMesh). Fixes mesh-less
                // StaticMeshActors AND AxTextRenderActors (both substituted to the StaticMeshActor engine class).
                if (actorCls == "StaticMeshActor") compCls = "StaticMeshComponent";
                // Per-instance material overrides (UMeshComponent.OverrideMaterials): a world can re-skin a shared mesh
                // per placement. Capture the cooked component's array so the placed component shows the right material
                // instead of the mesh's defaults. Null/None slots stay null (= use the mesh default for that slot).
                var overrideMats = new List<(string? pkg, string? name, string? cls)>();
                try
                {
                    var omArr = meshComp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OverrideMaterials");
                    if (omArr != null)
                        foreach (var om in omArr)
                        {
                            var ro = om?.ResolvedObject;
                            overrideMats.Add(ro != null ? (PackagePathOfResolved(ro), ro.Name.Text, ro.Class?.Name.Text) : (null, null, null));
                        }
                }
                catch { }
                while (overrideMats.Count > 0 && overrideMats[^1].pkg == null) overrideMats.RemoveAt(overrideMats.Count - 1); // trim trailing default slots
                if (overrideMats.All(m => m.pkg == null)) overrideMats.Clear();
                // No per-instance overrides: fall back to the mesh's own default materials so the placed
                // component shows real materials instead of the import's gray fallback.
                // (UE4D_NO_MESHDEFAULTS=1 disables: bisection showed a crash signature correlating with this fill.)
                if (overrideMats.Count == 0 && meshDefaultMats is { Count: > 0 }
                    && Environment.GetEnvironmentVariable("UE4D_NO_MESHDEFAULTS") != "1")
                    overrideMats = new List<(string? pkg, string? name, string? cls)>(meshDefaultMats);
                // Mesh-less Blueprint actors: spawn the real BP as a ChildActorComponent so its class-built
                // content (child actors, skeletal meshes, BP visuals, volume logic) appears at runtime instead
                // of an empty placeholder. Only when the BP asset exists on disk (else the class ref dangles).
                // Applies whether or not the instance resolved a mesh: our BP assets carry no SCS meshes (UE5
                // templates skip SCS grafting), so the child never double-renders — it contributes logic,
                // volumes, triggers and child spawns while the wrapper carries the visual.
                // Skipped for the TextRender/Decal synth paths below (they have their own component shapes).
                string? childBp = null;
                // UE5 emission path only (it alone emits the 3rd export; the legacy 2-slot reservation
                // would misalign). Excludes LevelInstances (their branch emits exactly 2 exports).
                // Conservative gate (mesh-less only): mass-wrapping meshed BPs crashed the editor on load;
                // re-enable broadly only per-class after spawn-safety is proven (see ChildBpAllow).
                if (isUe5 && worldAsset == null && meshPkg == null && !isTextRender && !isDecal && !string.IsNullOrWhiteSpace(contentRoot))
                {
                    try
                    {
                        // ChildActorClass must be an AActor subclass: resolve the BP's engine-native super and
                        // only accept actor-like bases (widget/anim/object BPs would break the spawn).
                        static bool IsActorSuper(string? ns) => ns != null
                            && (ns.Contains("Actor", StringComparison.Ordinal) || ns.EndsWith("Pawn", StringComparison.Ordinal)
                                || ns.EndsWith("Character", StringComparison.Ordinal) || ns.Contains("Volume", StringComparison.Ordinal)
                                || ns.EndsWith("Trigger", StringComparison.Ordinal) || ns.EndsWith("Brush", StringComparison.Ordinal)
                                || ns.EndsWith("TargetPoint", StringComparison.Ordinal));
                        var bpn = ObjectPackageAndName(e.Class);
                        // Null super = the walk hit an unresolvable link, not evidence of non-actor: the source
                        // map spawned this class as an actor, so accept it (classpath + file gates still apply).
                        var bSuper = FindEngineNativeSuper(e.Class);
                        if (bpn is { pkg: string bpp, name: string bpn2 }
                            && bpp.StartsWith("/Game/", StringComparison.Ordinal)
                            && (bSuper == null || IsActorSuper(bSuper))
                            && File.Exists(Path.Combine(contentRoot, bpp.Substring("/Game/".Length) + ".uasset")))
                            childBp = bpp + "." + bpn2;
                        else if (System.Threading.Interlocked.Increment(ref ChildBpRejectShown) <= 12)
                            Log.Information("  childbp REJECT {Actor} classpath={CP} super={S}", e.Name,
                                bpn?.pkg + "." + bpn?.name, bSuper);
                    }
                    catch { }
                }
                place.Add((actorPkg, actorCls, compPkg, compCls, rootComp.Name, label, loc, rot, scl, meshPkg, meshName, worldAsset, overrideMats, actorHidden, compVisible, compHiddenInGame, editorOnly, absLoc, absRot, absScale, textValue, worldSize, decalMat, decalSize, mapBuildId, rootComp, childBp));
            }
            catch { /* skip actors whose component fails to parse (missing imports) */ }
        }
        Log.Information("Cooked actors to place: {N} -> {L}", place.Count, string.Join(", ", place.Select(p => $"{p.label}({p.actorClass})")));

        // Per-map GameMode recovery: the source World's WorldSettings names the game mode the map actually
        // runs (EntryLevel -> EntryGameMode). Without it every map falls back to GameModeBase.
        string? defaultGameMode = null;
        try
        {
            // Several exports can share the WorldSettings class name (CDO/redirects); take the first one
            // that actually carries a DefaultGameMode reference.
            foreach (var srcWs in srcExports.Where(x => x.ExportType == "WorldSettings" || x.ExportType == "AxWorldSettings"))
            {
                var gmPath = srcWs.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("DefaultGameMode")?.ResolvedObject?.GetPathName();
                if (Environment.GetEnvironmentVariable("UE4D_LOGGAMEMODE") == "1")
                    Log.Information("  gamemode scan {T}: ws={W} gm={G}", targetShort, srcWs.ExportType + "/" + srcWs.Name, gmPath);
                if (string.IsNullOrEmpty(gmPath)) continue;
                var dot = gmPath.LastIndexOf('.');
                if (dot <= 0) continue;
                var gpkg = gmPath.Substring(0, dot); var gname = gmPath.Substring(dot + 1);
                if (gpkg.StartsWith("/Game/", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(contentRoot)
                    && File.Exists(Path.Combine(contentRoot, gpkg.Substring("/Game/".Length) + ".uasset")))
                {
                    defaultGameMode = gpkg + "." + gname;
                    Log.Information("Map {T}: recovered DefaultGameMode {G}", targetShort, defaultGameMode);
                    break;
                }
            }
        }
        catch { }

        // DEV isolation: SKIP_ACTOR_CLASSES drops placed actors whose class CONTAINS a listed token (case-insensitive);
        // "*" drops them ALL (emit the template-only map). Used to bisect a map-load fault the editor won't name.
        var skipTokens = Environment.GetEnvironmentVariable("SKIP_ACTOR_CLASSES");
        if (!string.IsNullOrWhiteSpace(skipTokens))
        {
            var toks = skipTokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int before = place.Count;
            place = toks.Contains("*")
                ? new()
                : place.Where(p => !toks.Any(t => p.actorClass.Contains(t, StringComparison.OrdinalIgnoreCase)
                                                || p.label.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            Log.Warning("SKIP_ACTOR_CLASSES='{S}': dropped {N} placed actor(s) ({B} -> {A})", skipTokens, before - place.Count, before, place.Count);
        }

        // Gather streaming sublevels from the source UWorld so the reskinned persistent map preserves its world
        // composition. UWorld.StreamingLevels (native tail, after tagged-None) -> ULevelStreaming exports, each with a
        // WorldAsset soft path to a sublevel .umap. We re-emit them as LevelStreamingAlwaysLoaded so the sublevels both
        // register (Levels window) and load/show — volume-driven (Dynamic) wouldn't auto-load without the volume.
        var streamingAssets = new List<StreamingLevelRef>();
        if (Environment.GetEnvironmentVariable("UE4D_NOSTREAM") == "1") { streamingAssets.Clear(); }
        var srcWorld = srcExports.OfType<CUE4Parse.UE4.Objects.Engine.UWorld>().FirstOrDefault();
        if (Environment.GetEnvironmentVariable("UE4D_NOSTREAM") != "1" && srcWorld?.StreamingLevels != null)
        {
            foreach (var si in srcWorld.StreamingLevels)
            {
                try
                {
                    if (si == null || si.Index <= 0) continue;
                    var sl = srcExports.ElementAtOrDefault(si.Index - 1);
                    if (sl == null) continue;
                    var wa = sl.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath>("WorldAsset").ToString();
                    bool vis = sl.GetOrDefault<bool>("bShouldBeVisible");
                    bool ved = sl.GetOrDefault<bool>("bShouldBeVisibleInEditor");
                    bool ld = sl.GetOrDefault<bool>("bShouldBeLoaded");
                    bool st = sl.GetOrDefault<bool>("bIsStatic");
                    if (Environment.GetEnvironmentVariable("UE4D_LOGSTREAM") == "1")
                        Log.Information("  stream {Cls} vis={V} visEd={VE} load={L} static={S} asset={A}",
                            sl.Class?.Name.Text, vis, ved, ld, st, wa);
                    if (!string.IsNullOrEmpty(wa) && wa != "None")
                        streamingAssets.Add(new StreamingLevelRef(wa, sl.Class?.Name.Text ?? "LevelStreamingDynamic", vis, ved, ld, st));
                }
                catch { /* skip a streaming entry that fails to resolve */ }
            }
            if (streamingAssets.Count > 0)
                Log.Information("Streaming sublevels to preserve: {N} -> {L}", streamingAssets.Count, string.Join(", ", streamingAssets.Select(s => s.Asset)));
        }
        // NOTE: do NOT bail on place.Count == 0 — we must still emit a valid (empty) reskinned template map.
        // Bailing leaves a stale/old .umap in place (e.g. a prior UncookedPackageWriter dump with an unresolvable
        // cooked import table), which crashes the editor/content-browser with a 0x8 null-deref on scan/open.
        if (place.Count == 0) Log.Information("no placeable actors for {T}; writing empty map", targetShort);

        if (isUe5) { PlaceActorsUe5(templateData, outFile, targetShort, targetPackagePath, contentRoot, place, streamingAssets, defaultGameMode); return; }

        // Reskin template (clone + rename) into the writer.
        var data = templateData;
        Package tpkg;
        try
        {
            var ar = new FByteArchive("template", data, new VersionContainer(EGame.GAME_UE4_21));
            tpkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "template parse"); return; }

        int TIdx(string s) => Array.FindIndex(tpkg.NameMap, n => n.Name == s);
        NodePayloadWalker.StructPropertyIdx = TIdx("StructProperty"); NodePayloadWalker.BoolPropertyIdx = TIdx("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = TIdx("ByteProperty"); NodePayloadWalker.EnumPropertyIdx = TIdx("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = TIdx("ArrayProperty"); NodePayloadWalker.SetPropertyIdx = TIdx("SetProperty");
        NodePayloadWalker.MapPropertyIdx = TIdx("MapProperty");
        int noneIdx = TIdx("None");

        // The content-browser asset name of a map = the UWorld export's object name. Derive the template's short name
        // from the World export (NOT NameMap[0], which isn't the map name) so we rename "Template_Default" -> target;
        // otherwise every reskinned map shows as "Template_Default" in the content browser.
        int worldExport = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "World");
        var oldShort = worldExport >= 0 ? tpkg.ExportMap[worldExport].ObjectName.Text
                       : ((tpkg.NameMap[0].Name ?? "").Contains('/') ? (tpkg.NameMap[0].Name ?? "")[((tpkg.NameMap[0].Name ?? "").LastIndexOf('/') + 1)..] : (tpkg.NameMap[0].Name ?? ""));
        // The package-path name entry (e.g. "/Game/Maps/Template_Default") -> the target package path.
        var oldPath = tpkg.NameMap.Select(n => n.Name).FirstOrDefault(s => s != null && s.Contains('/') && s.EndsWith("/" + oldShort, StringComparison.Ordinal)) ?? oldShort;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [oldPath] = targetPackagePath, [oldPath + "." + oldShort] = targetPackagePath + "." + targetShort,
            [oldPath + "." + oldShort + "_C"] = targetPackagePath + "." + targetShort + "_C",
            [oldShort] = targetShort, [oldShort + "_C"] = targetShort + "_C",
            ["Default__" + oldShort + "_C"] = "Default__" + targetShort + "_C",
        };

        int lvlExport = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "Level");
        int lvlPkg = lvlExport + 1;
        var lvlPayload = new byte[(int)tpkg.ExportMap[lvlExport].SerialSize];
        Array.Copy(data, (int)tpkg.ExportMap[lvlExport].SerialOffset, lvlPayload, 0, lvlPayload.Length);
        int lvlPostNone = NodePayloadWalker.SkipTaggedProperties(lvlPayload, 0, noneIdx);

        // Emit a World asset-registry record (with the import-count-sized dependency section SynthPackageWriter now
        // writes) so the editor's on-disk scan INDEXES the map and it shows in the content browser without loading it.
        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath)
        { PrimaryArAsset = (targetShort, "/Script/Engine.World") };
        spw.PackageFlags = (uint)tpkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = tpkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in tpkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));

        // Plan new export indices: per actor -> [component, actor].
        int baseExport = tpkg.ExportMap.Length;
        var newActorPkgs = new List<int>();
        for (int i = 0; i < place.Count; i++) newActorPkgs.Add(baseExport + i * 2 + 2);  // actor is 2nd of each pair
        // Keep the template WorldSettings referenced (see UE5 path: replace drops template demo actors).
        // Template FPIs are table+1 (no baseExport offset).
        int wsTplLegacy = Array.FindIndex(tpkg.ExportMap, e => e.ClassName == "WorldSettings");
        if (wsTplLegacy >= 0) newActorPkgs.Insert(0, wsTplLegacy + 1);
        // Plan ULevelStreaming export indices (appended AFTER the actor pairs); outer = World, and the World's native
        // StreamingLevels tail is rewritten to reference these so the persistent map preserves its sublevel composition.
        int streamingBaseExport = baseExport + place.Count * 2;
        var newStreamingPkgs = new List<int>();
        for (int i = 0; i < streamingAssets.Count; i++) newStreamingPkgs.Add(streamingBaseExport + i + 1);
        int worldPkgIdx = worldExport + 1;

        // Clone existing exports, patching ULevel.Actors to append the new actors.
        foreach (var imp in tpkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        // Built-lighting: reference the cooked map's UMapBuildDataRegistry from the reskinned ULevel.MapBuildData so the
        // recovered lightmaps bind to our placed components (the _BuiltData package itself is emitted verbatim by the
        // main dump). Read the authoritative ref off the SOURCE map's ULevel; skip maps with no built data.
        int mapBuildDataImp = 0;
        try
        {
            var srcLevelExp = srcExports.FirstOrDefault(e => e.ExportType == "Level");
            var mbd = srcLevelExp?.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("MapBuildData")?.ResolvedObject;
            if (mbd != null)
            {
                var regPkg = PackagePathOfResolved(mbd);
                var regName = mbd.Name.Text;
                if (!string.IsNullOrEmpty(regPkg) && !string.IsNullOrEmpty(regName))
                {
                    var pkgI = spw.AddImport("/Script/CoreUObject", "Package", 0, regPkg);
                    mapBuildDataImp = spw.AddImport("/Script/Engine", "MapBuildDataRegistry", pkgI, regName);
                    Log.Information("ULevel.MapBuildData -> {P}.{N}", regPkg, regName);
                }
            }
        }
        catch { }

        for (int i = 0; i < tpkg.ExportMap.Length; i++)
        {
            var e = tpkg.ExportMap[i];
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            if (i == lvlExport)
            {
                payload = PatchLevelActors(payload, lvlPostNone, newActorPkgs);
                // Inject MapBuildData BEFORE the level's None (PatchLevelActors only appended in the native region
                // after None, so lvlPostNone still marks the original tagged-prop terminator).
                if (mapBuildDataImp != 0) payload = PatchLevelMapBuildData(payload, lvlPostNone, mapBuildDataImp, spw);
            }
            else if (i == worldExport && newStreamingPkgs.Count > 0) payload = PatchWorldStreamingLevels(payload, noneIdx, newStreamingPkgs);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0,
                e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload, (uint)e.ObjectFlags, e.IsAsset);
        }

        var pkgImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var classImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
        int ClassImp(string pkgPath, string cls)
        {
            var key = pkgPath + "." + cls;
            if (classImpCache.TryGetValue(key, out var c)) return c;
            if (!pkgImpCache.TryGetValue(pkgPath, out var pImp))
            { pImp = FindPackageImport(tpkg, pkgPath); if (pImp == 0) pImp = spw.AddImport("/Script/CoreUObject", "Package", 0, pkgPath); pkgImpCache[pkgPath] = pImp; }
            if (pkgPath.StartsWith("/Game/", StringComparison.Ordinal) && cls.EndsWith("_C", StringComparison.Ordinal))
                c = spw.AddImport("/Script/Engine", "BlueprintGeneratedClass", pImp, cls);
            else
                c = spw.AddImport("/Script/CoreUObject", "Class", pImp, cls);
            classImpCache[key] = c; return c;
        }

        // Append synthesized component+actor pairs.
        for (int i = 0; i < place.Count; i++)
        {
            var a = place[i];
            int compPkg = baseExport + i * 2 + 1, actorPkg = baseExport + i * 2 + 2;
            // A placed BP-class actor instance (actorPkg is a /Game BP path, not a /Script engine class) must be
            // serialized like the editor saves one: its component is an SCS-created subobject (CreationMethod=SCS,
            // bNetAddressable), and the actor carries ActorGuid + BlueprintCreatedComponents listing that component.
            // Without these the editor treats the component as a free instance and reconciles it against the BP's
            // SCS, reading a native count from the wrong bytes -> 18GB runaway hang on map load. (Ground truth:
            // dummy_map.umap, a placed BP actor saved by UE5.1.)
            bool isBpActor = a.actorPkg.StartsWith("/Game", StringComparison.Ordinal);
            // component
            int meshObjImp = 0;
            if (a.meshPkg != null && a.meshName != null)
            {
                // package import for the mesh asset path, then the StaticMesh object import under it
                if (!pkgImpCache.TryGetValue(a.meshPkg, out var mp)) { mp = spw.AddImport("/Script/CoreUObject", "Package", 0, a.meshPkg); pkgImpCache[a.meshPkg] = mp; }
                meshObjImp = spw.AddImport("/Script/Engine", "StaticMesh", mp, a.meshName);
            }
            // Per-instance material overrides -> object imports (null slot = 0 = use mesh default for that slot).
            int[] overrideMatImps = System.Array.Empty<int>();
            if (a.compClass == "StaticMeshComponent" && a.overrideMats.Count > 0)
            {
                overrideMatImps = new int[a.overrideMats.Count];
                for (int mi = 0; mi < a.overrideMats.Count; mi++)
                {
                    var (mpkg, mname, mcls) = a.overrideMats[mi];
                    if (mpkg == null || mname == null) { overrideMatImps[mi] = 0; continue; }
                    if (!pkgImpCache.TryGetValue(mpkg, out var mp)) { mp = spw.AddImport("/Script/CoreUObject", "Package", 0, mpkg); pkgImpCache[mpkg] = mp; }
                    overrideMatImps[mi] = spw.AddImport("/Script/Engine", string.IsNullOrEmpty(mcls) ? "MaterialInstanceConstant" : mcls, mp, mname);
                }
            }
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name);
                if (meshObjImp != 0 && a.compClass == "StaticMeshComponent") t.Object("StaticMesh", meshObjImp);
                if (overrideMatImps.Length > 0) t.ObjectArray("OverrideMaterials", overrideMatImps);
                // TextRenderActor: emit the displayed string (Base-history FText — version-stable in our 4.21 maps,
                // unlike the None+culture-invariant form which is editor-version gated) + WorldSize.
                if (a.compClass == "TextRenderComponent")
                {
                    t.Text("Text", "", "", a.textValue ?? "");
                    t.Float("WorldSize", a.worldSize);
                }
                // DecalActor: bind the projected DecalMaterial (import) + DecalSize so the decal renders its material.
                if (a.compClass == "DecalComponent")
                {
                    if (a.decalMat.pkg != null && a.decalMat.name != null)
                    {
                        if (!pkgImpCache.TryGetValue(a.decalMat.pkg, out var dmp)) { dmp = spw.AddImport("/Script/CoreUObject", "Package", 0, a.decalMat.pkg); pkgImpCache[a.decalMat.pkg] = dmp; }
                        int dmImp = spw.AddImport("/Script/Engine", string.IsNullOrEmpty(a.decalMat.cls) ? "MaterialInstanceConstant" : a.decalMat.cls, dmp, a.decalMat.name);
                        t.Object("DecalMaterial", dmImp);
                    }
                    t.Struct("DecalSize", "Vector", () => { w.Write(a.decalSize[0]); w.Write(a.decalSize[1]); w.Write(a.decalSize[2]); });
                }
                // Preserve the cooked component's visibility (default bVisible=true / bHiddenInGame=false, so only
                // write when they differ — an actor hidden/disabled in-game must stay hidden here).
                if (!a.compVisible) t.Bool("bVisible", false);
                if (a.compHiddenInGame) t.Bool("bHiddenInGame", true);
                WriteLightComponentProperties(t, a.sourceComp, a.compClass);
                // Mark Movable so the editor never bakes static lighting for these synthesized components —
                // Lightmass derefs null on placed lights/meshes that lack full bake data (Build Lighting crash).
                t.ByteEnum("Mobility", "EComponentMobility::Type", "EComponentMobility::Movable");
                // Preserve world-space anchoring: when the cooked component marked a channel absolute, the value below
                // is a WORLD transform and must be flagged so the editor doesn't re-interpret it as relative-to-parent.
                if (a.absLoc) t.Bool("bAbsoluteLocation", true);
                if (a.absRot) t.Bool("bAbsoluteRotation", true);
                if (a.absScale) t.Bool("bAbsoluteScale", true);
                t.Struct("RelativeLocation", "Vector", () => { w.Write(a.loc[0]); w.Write(a.loc[1]); w.Write(a.loc[2]); });
                if (a.rot[0] != 0 || a.rot[1] != 0 || a.rot[2] != 0) t.Struct("RelativeRotation", "Rotator", () => { w.Write(a.rot[0]); w.Write(a.rot[1]); w.Write(a.rot[2]); });
                if (a.scale[0] != 1 || a.scale[1] != 1 || a.scale[2] != 1) t.Struct("RelativeScale3D", "Vector", () => { w.Write(a.scale[0]); w.Write(a.scale[1]); w.Write(a.scale[2]); });
                if (isBpActor)
                {
                    t.Bool("bNetAddressable", true);
                    t.Int("UCSSerializationIndex", 0);
                    t.Enum("CreationMethod", "EComponentCreationMethod", "EComponentCreationMethod::SimpleConstructionScript");
                }
                t.WriteNone();
                // Native tail after None — sizes confirmed by the editor's LOAD path (LinkerLoad serial-size asserts),
                // which is what matters (an editor-SAVED gt_onecube reads LONGER tails, but that's a save-side artifact,
                // not what load deserializes): SceneComponent = 4 bytes (1 int32 = UCSModifiedProperties), adding more
                // crashes "Got 210 Expected 214"; StaticMeshComponent = 8 bytes (UCSMod + LODData), adding more crashes
                // "Got 243 Expected 247". Do NOT add a trailing int32 here.
                w.Write(0);                                             // UActorComponent: UCSModifiedProperties count = 0
                // NOTE: do NOT write the legacy FStaticShadowDepthMapData here. ULightComponent reads it only when
                // FRenderingObjectVersion < MapBuildDataSeparatePackage; our package's Dev-Rendering custom version is
                // newer, so the editor/CUE4Parse do NOT read it — writing it desyncs the component (CUE4Parse: "Could
                // not read PointLightComponent correctly"). The original (no shadow map) tail is correct.
                if (a.compClass == "StaticMeshComponent")
                {
                    // UStaticMeshComponent: LODData array. Emit ONE FStaticMeshComponentLODInfo carrying the cooked
                    // MapBuildDataId so the recovered UMapBuildDataRegistry binds this component's lightmap. With our
                    // Dev-Rendering custom version (>= MapBuildDataSeparatePackage) the LODInfo is the separate-package
                    // form: FStripDataFlags(2) + FGuid(16) + bLoadVertexColorData(1=0) + PaintedVertices int32(0).
                    // GlobalStripFlags=0 => editor reads PaintedVertices (IsEditorDataStripped=false, build !IsFilterEditorOnly).
                    if (!a.mapBuildId.Equals(default(CUE4Parse.UE4.Objects.Core.Misc.FGuid)))
                    {
                        w.Write(1);                                     // LODData count = 1
                        w.Write((byte)0); w.Write((byte)0);             // FStripDataFlags{Global,Class}=0
                        w.Write(a.mapBuildId.A); w.Write(a.mapBuildId.B); w.Write(a.mapBuildId.C); w.Write(a.mapBuildId.D); // FGuid
                        w.Write((byte)0);                               // bLoadVertexColorData = 0
                        w.Write(0);                                     // PaintedVertices count = 0
                    }
                    else w.Write(0);                                    // LODData count = 0
                }
                w.Flush();
                spw.AddExportRaw(spw.Name(a.compName), 0, ClassImp(a.compPkg, a.compClass), 0, 0, actorPkg, ms.ToArray(), isBpActor ? 0u : 0x1u, false);
            }
            // actor (RootComponent -> component, ActorLabel)
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name);
                t.Object("RootComponent", compPkg);
                t.Str("ActorLabel", a.label);
                // Actor-level disabled/hidden state from the cooked world (defaults false -> only write when set).
                if (a.actorHidden) t.Bool("bHidden", true);
                if (a.editorOnly) t.Bool("bIsEditorOnlyActor", true);
                if (isBpActor)
                {
                    // Every placed actor needs an ActorGuid; a BP-class instance also needs BlueprintCreatedComponents
                    // listing its SCS subobjects so the editor matches (not reconciles) them against the class.
                    t.GuidStruct("ActorGuid", FGuid16.NewGuid());
                    t.ObjectArray("BlueprintCreatedComponents", new[] { compPkg });
                }
                if (a.worldAsset != null) t.SoftObject("WorldAsset", a.worldAsset);   // LevelInstance embedded level
                t.WriteNone();
                // Native tail = a single int32 (4 bytes) for ALL actors — confirmed by the editor LOAD path: a
                // StaticMeshActor with an 8-byte tail crashes "Got 93 Expected 97" (NetVarTriggerApplier_0), and a BP
                // actor over-adds to "Got 200 Expected 204". Do NOT add a trailing int32 here.
                w.Write(0);                                                      // AActor native tail (all actors)
                w.Flush();
                spw.AddExportRaw(spw.Name(a.label), 0, ClassImp(a.actorPkg, a.actorClass), 0, 0, lvlPkg, ms.ToArray(), isBpActor ? 0x8u : (0x1u | 0x4u), false);
            }
        }

        // Append ULevelStreaming exports (outer = World) for each preserved sublevel, preserving the cooked
        // class + load/visibility flags so hub maps stream on demand instead of force-loading every level.
        for (int i = 0; i < streamingAssets.Count; i++)
        {
            var sref = streamingAssets[i];
            using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            t.SoftObject("WorldAsset", sref.Asset);
            t.Bool("bShouldBeVisible", sref.Visible);
            t.Bool("bShouldBeVisibleInEditor", sref.VisibleInEditor);
            t.Bool("bShouldBeLoaded", sref.ShouldBeLoaded);
            t.Bool("bIsStatic", sref.IsStatic);
            t.WriteNone(); w.Write(0); w.Flush(); // UObject: bSerializeGuid = false
            spw.AddExportRaw(spw.Name("LevelStreamingDynamic_" + i), 0, ClassImp("/Script/Engine", "LevelStreamingDynamic"),
                0, 0, worldPkgIdx, ms.ToArray(), 0x8, false);
        }
        if (newStreamingPkgs.Count > 0) Log.Information("Preserved {N} streaming sublevel(s) in {T}", newStreamingPkgs.Count, targetShort);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        spw.Write(outFile);
        Log.Information("Placed {N} actors into {T} -> {Out}", place.Count, targetShort, outFile);

        // NOTE: do NOT generate placeholder cube meshes here. The main pipeline already dumps every referenced
        // StaticMesh (real geometry + multi-material). Writing cubes to the same /Game paths from this (parallel)
        // map pass RACES the pipeline's real-mesh write and can clobber it with a 1-slot WorldGridMaterial cube.
        _ = cubePath; _ = contentRoot;
    }

    private static string StripNum(string s) => s;
    /// <summary>Resolve an export's class to (packagePath, className) from the cooked import table.</summary>
    private static (string? pkg, string cls) CookedClassPath(Package pkg, CUE4Parse.UE4.Objects.UObject.FObjectExport e)
    {
        var ci = e.ClassIndex?.Index ?? 0;
        if (ci >= 0) return (null, "None");                 // class is an export (BP) — unresolvable here
        var imp = pkg.ImportMap[-ci - 1];
        var cls = imp.ObjectName.Text;
        var outer = imp.OuterIndex?.Index ?? 0;
        var pkgPath = outer < 0 ? pkg.ImportMap[-outer - 1].ObjectName.Text : null;
        return (pkgPath, cls);
    }
    /// <summary>The "/Script/&lt;Module&gt;" package of a resolved class, or null if it's not a native /Script class
    /// (e.g. a BlueprintGeneratedClass under /Game). Works for both legacy and Zen by walking GetPathName.</summary>
    private static string? ScriptPackageOf(CUE4Parse.UE4.Assets.ResolvedObject? cls)
    {
        var path = cls?.GetPathName();                       // e.g. "/Script/Engine.StaticMeshActor" or "/Game/BP/BP_X.BP_X_C"
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/Script/", StringComparison.Ordinal)) return null;
        var dot = path.LastIndexOf('.');
        return dot > 0 ? path.Substring(0, dot) : path;       // -> "/Script/Engine"
    }

    private static (string pkg, string name)? ScriptPackageAndName(CUE4Parse.UE4.Assets.ResolvedObject? cls)
    {
        var path = cls?.GetPathName();
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/Script/", StringComparison.Ordinal)) return null;
        var dot = path.LastIndexOf('.');
        if (dot <= "/Script/".Length || dot + 1 >= path.Length) return null;
        return (path[..dot], path[(dot + 1)..]);
    }

    private static void ReportPlacedBlueprintNativeSuper(CUE4Parse.UE4.Assets.ResolvedObject? bpClass,
        Action<string, string, string>? onGameClass)
    {
        if (onGameClass is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = bpClass; cur != null && seen.Count < 32; )
        {
            var path = cur.GetPathName();
            if (!seen.Add(path)) return;
            if (ScriptPackageAndName(cur) is { } scriptClass)
            {
                onGameClass(scriptClass.pkg, scriptClass.name, "AActor");
                return;
            }

            if (cur.TryLoad<CUE4Parse.UE4.Objects.UObject.UStruct>(out var s))
            {
                cur = s.SuperStruct?.ResolvedObject;
                continue;
            }
            cur = cur.Super;
        }
    }

    /// <summary>First /Script/Engine.* class walking up a cooked class's super chain (BP -> native base).
    /// Null when the chain never enters /Script/Engine (game-native or unresolvable).</summary>
    private static string? FindEngineNativeSuper(CUE4Parse.UE4.Assets.ResolvedObject? cls)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var cur = cls; cur != null && seen.Count < 32;)
        {
            var path = cur.GetPathName();
            if (string.IsNullOrEmpty(path) || !seen.Add(path)) return null;
            if (path.StartsWith("/Script/Engine.", StringComparison.Ordinal)) return path;
            if (ScriptPackageAndName(cur) is { }) return null;   // a different /Script module (game-native)
            if (cur.TryLoad<CUE4Parse.UE4.Objects.UObject.UStruct>(out var s))
            {
                cur = s.SuperStruct?.ResolvedObject;
                continue;
            }
            cur = cur.Super;
        }
        return null;
    }

    private static (string? pkg, string? name)? ObjectPackageAndName(CUE4Parse.UE4.Assets.ResolvedObject? obj)
    {
        var path = obj?.GetPathName();                        // e.g. "/Game/BP/BP_X.BP_X_C"
        if (string.IsNullOrEmpty(path)) return null;
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        if (dot <= slash || dot + 1 >= path.Length) return null;
        return (path[..dot], path[(dot + 1)..]);
    }

    /// <summary>Package path of a resolved object (e.g. a referenced StaticMesh): "/Game/Meshes/SM_Foo" from
    /// "/Game/Meshes/SM_Foo.SM_Foo".</summary>
    private static string? PackagePathOfResolved(CUE4Parse.UE4.Assets.ResolvedObject obj)
    {
        var path = obj.GetPathName();
        if (string.IsNullOrEmpty(path)) return null;
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        return dot > slash && dot > 0 ? path.Substring(0, dot) : path;
    }

    private static bool IsLightComponent(string cls) =>
        cls is "LightComponent" or "PointLightComponent" or "SpotLightComponent" or "RectLightComponent" or
            "DirectionalLightComponent" or "SkyLightComponent" ||
        cls.EndsWith("LightComponent", StringComparison.Ordinal);

    public static string NormalizeSynthComponentClass(string cls, bool hasStaticMesh)
    {
        if (hasStaticMesh) return "StaticMeshComponent";
        if (cls is "SceneComponent" or "ArrowComponent") return cls;
        if (IsLightComponent(cls)) return cls;
        return "SceneComponent";
    }

    private static void WriteEmptyLegacyStaticShadowDepthMap(FArchiveWriter w)
    {
        // UE4.21 light components serialize legacy FStaticShadowDepthMapData natively after tagged properties:
        // FMatrix WorldToLight (16 floats) + SizeX + SizeY + DepthSamples TArray count. Empty data is enough for
        // UE5's upgrader to stay inside the export boundary.
        for (var i = 0; i < 16; i++) w.Write(i is 0 or 5 or 10 or 15 ? 1f : 0f);
        w.Write(0);
        w.Write(0);
        w.Write(0);
    }

    private static void WriteLightComponentProperties(TaggedPropertyWriter t, CUE4Parse.UE4.Assets.Exports.UObject src, string compClass)
    {
        if (!IsLightComponent(compClass)) return;

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.ULightComponentBase lcb)
        {
            t.Float("Intensity", lcb.Intensity);
            t.ColorStruct("LightColor", lcb.LightColor);
            t.Bool("CastShadows", lcb.CastShadows != 0);
        }
        else
        {
            t.Float("Intensity", src.GetOrDefault("Intensity", src.GetOrDefault("Brightness", MathF.PI)));
            t.ColorStruct("LightColor", src.GetOrDefault("LightColor", new CUE4Parse.UE4.Objects.Core.Math.FColor(255, 255, 255, 255)));
            t.Bool("CastShadows", src.GetOrDefault("CastShadows", 1u) != 0);
        }

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.ULightComponent lc)
        {
            t.Float("Temperature", lc.Temperature);
            t.Bool("bUseTemperature", lc.bUseTemperature != 0);
            t.Float("MaxDrawDistance", lc.MaxDrawDistance);
            t.Float("MaxDistanceFadeRange", lc.MaxDistanceFadeRange);
            t.Float("IESBrightnessScale", lc.IESBrightnessScale);
            t.Bool("bUseIESBrightness", lc.bUseIESBrightness != 0);
        }

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.ULocalLightComponent local)
        {
            t.Float("AttenuationRadius", local.AttenuationRadius);
            t.ByteEnum("IntensityUnits", "ELightUnits", "ELightUnits::" + local.IntensityUnits);
        }

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.UPointLightComponent point)
        {
            t.Float("LightFalloffExponent", point.LightFalloffExponent);
            t.Float("SourceRadius", point.SourceRadius);
            t.Float("SoftSourceRadius", point.SoftSourceRadius);
            t.Float("SourceLength", point.SourceLength);
            t.Bool("bUseInverseSquaredFalloff", point.bUseInverseSquaredFalloff);
        }

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.USpotLightComponent spot)
        {
            t.Float("InnerConeAngle", spot.InnerConeAngle);
            t.Float("OuterConeAngle", spot.OuterConeAngle);
        }

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.URectLightComponent rect)
        {
            t.Float("SourceWidth", rect.SourceWidth);
            t.Float("SourceHeight", rect.SourceHeight);
            t.Float("BarnDoorAngle", rect.BarnDoorAngle);
            t.Float("BarnDoorLength", rect.BarnDoorLength);
            t.Float("LightFunctionConeAngle", rect.LightFunctionConeAngle);
        }

        if (src is CUE4Parse.UE4.Assets.Exports.Component.Lights.UDirectionalLightComponent dir)
        {
            t.Float("LightSourceAngle", dir.LightSourceAngle);
            t.Float("LightSourceSoftAngle", dir.LightSourceSoftAngle);
        }
    }

    private static float[] ReadVec(CUE4Parse.UE4.Assets.Exports.UObject obj, string prop, float dflt)
    {
        if (prop == "RelativeRotation")
        {
            var r = obj.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FRotator>(prop);
            return new[] { r.Pitch, r.Yaw, r.Roll };
        }
        var v = obj.GetOrDefault(prop, new CUE4Parse.UE4.Objects.Core.Math.FVector(dflt, dflt, dflt));
        return new[] { v.X, v.Y, v.Z };
    }

    private static bool IsHlodPlacedActor(string actorClass, string actorName)
        => actorName.StartsWith("StandInMeshActor", StringComparison.OrdinalIgnoreCase);

    private static bool IsHlodOrStandinPath(string? path)
    {
        // Stand-in meshes are placed-actor geometry (proven by map references); only skip when asked
        // via UE4D_SKIP_STANDIN=1. (HLOD/premerged paths were already handled by callers.)
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Environment.GetEnvironmentVariable("UE4D_SKIP_STANDIN") != "1") return false;
        return path.Replace('\\', '/').Contains("/Simplygon/Standins/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splice a DefaultGameMode ObjectProperty tag into a WorldSettings payload just before its
    /// None terminator (stripping any template default first so the tag is never duplicated). Returns the
    /// new payload and how many bytes the script region grew (caller adds it to ScriptSerializationEndOffset).
    /// Failure returns the input untouched (map keeps the template default game mode).</summary>
    private static (byte[] payload, int grown) PatchWorldSettingsGameMode(byte[] p, int noneIdx, SynthPackageWriter spw, int gmClsImp)
    {
        try
        {
            int nameCount = NodePayloadWalker.NameCount;
            int start = NodePayloadWalker.FindPayloadStart(p, nameCount);
            int dmIdx = spw.Name("DefaultGameMode");
            byte[] q = p;
            var (s, e2) = NodePayloadWalker.FindPropertySpan(q, start, noneIdx, dmIdx);
            if (s >= 0 && e2 > s)
            {
                q = new byte[p.Length - (e2 - s)];
                Array.Copy(p, 0, q, 0, s);
                Array.Copy(p, e2, q, s, p.Length - e2);
            }
            int start2 = NodePayloadWalker.FindPayloadStart(q, nameCount);
            int postNone = NodePayloadWalker.SkipTaggedProperties(q, start2, noneIdx);
            if (postNone < 8 || postNone > q.Length) return (p, 0);
            byte[] tag;
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name, true);
                t.Object("DefaultGameMode", gmClsImp);
                w.Flush();
                tag = ms.ToArray();
            }
            if (tag.Length == 0) return (p, 0);
            var outp = new byte[q.Length + tag.Length];
            Array.Copy(q, 0, outp, 0, postNone - 8);
            tag.CopyTo(outp, postNone - 8);
            Array.Copy(q, postNone - 8, outp, postNone - 8 + tag.Length, q.Length - (postNone - 8));
            Log.Information("WorldSettings: spliced DefaultGameMode tag ({B}B)", tag.Length);
            return (outp, outp.Length - p.Length);
        }
        catch (Exception ex) { Log.Debug(ex, "WorldSettings GameMode patch failed"); return (p, 0); }
    }

    /// <summary>Replace ULevel.Actors with exactly the placed actors (dropping the template's demo actors:
    /// Floor/light/sky/PlayerStart clones that otherwise pollute every map with content the source never had).
    /// The Actors array lives in the native region (post-None), so shrinking it keeps script offsets valid.</summary>
    private static byte[] PatchLevelActors(byte[] p, int postNone, IReadOnlyList<int> newActors)
    {
        int countOff = postNone + 4;
        int count = BitConverter.ToInt32(p, countOff);
        if (count < 0 || count > 100000) { Log.Warning("ULevel.Actors count {C} insane; leaving level actors untouched", count); return p; }
        int insertAt = countOff + 4 + count * 4;
        if (insertAt < 0 || insertAt > p.Length) { Log.Warning("ULevel.Actors array OOB; leaving level actors untouched"); return p; }
        var add = new byte[newActors.Count * 4];
        for (int i = 0; i < newActors.Count; i++) BitConverter.GetBytes(newActors[i]).CopyTo(add, i * 4);
        var outp = new byte[p.Length - count * 4 + add.Length];
        Array.Copy(p, 0, outp, 0, countOff);
        BitConverter.GetBytes(newActors.Count).CopyTo(outp, countOff);
        add.CopyTo(outp, countOff + 4);
        Array.Copy(p, insertAt, outp, countOff + 4 + add.Length, p.Length - insertAt);
        Log.Information("Replaced ULevel.Actors: {A} template actor(s) -> {B} placed", count, newActors.Count);
        return outp;
    }

    /// <summary>Splice a MapBuildData ObjectProperty (pointing at the recovered UMapBuildDataRegistry import) into the
    /// ULevel's tagged-property stream, immediately before its terminating None tag. <paramref name="postNone"/> is the
    /// offset just past that None (so the None FName occupies [postNone-8, postNone)).</summary>
    private static byte[] PatchLevelMapBuildData(byte[] p, int postNone, int regImp, SynthPackageWriter spw)
    {
        int noneStart = postNone - 8;                                     // None FName = (int32 index, int32 number)
        if (noneStart < 0 || noneStart > p.Length) { Log.Warning("Level None offset OOB; skipping MapBuildData wire"); return p; }
        byte[] prop;
        using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            t.Object("MapBuildData", regImp);
            w.Flush(); prop = ms.ToArray();
        }
        var outp = new byte[p.Length + prop.Length];
        Array.Copy(p, 0, outp, 0, noneStart);
        prop.CopyTo(outp, noneStart);
        Array.Copy(p, noneStart, outp, noneStart + prop.Length, p.Length - noneStart);
        Log.Information("Wired ULevel.MapBuildData (import {I})", regImp);
        return outp;
    }

    /// <summary>Rewrite UWorld's native StreamingLevels list so the persistent map references our synthesized
    /// ULevelStreaming exports. UWorld native tail (after tagged-None, per CUE4Parse UWorld.Deserialize):
    /// PersistentLevel (FPackageIndex) + ExtraReferencedObjects (TArray&lt;FPackageIndex&gt;) + StreamingLevels
    /// (TArray&lt;FPackageIndex&gt;). We replace only the StreamingLevels array, preserving everything around it.</summary>
    private static byte[] PatchWorldStreamingLevels(byte[] p, int noneIdx, IReadOnlyList<int> streamingPkgs)
    {
        try
        {
            // UE5 payloads carry a version preamble before the tag stream; strip it for the walk, rejoin after.
            int start = 0;
            if (NodePayloadWalker.IsUe5)
            {
                start = NodePayloadWalker.FindPayloadStart(p, NodePayloadWalker.NameCount);
                if (start <= 0 || start >= p.Length) { Log.Warning("World preamble not found; skipping streaming patch"); return p; }
            }
            var body = new byte[p.Length - start];
            Array.Copy(p, start, body, 0, body.Length);
            int pos = NodePayloadWalker.SkipTaggedProperties(body, 0, noneIdx);   // right after the None tag
            pos += 4;                                                          // leading field (same 4-byte lead PatchLevelActors uses)
            pos += 4;                                                          // PersistentLevel FPackageIndex
            if (pos + 4 > body.Length) { Log.Warning("World tail parse OOB at ExtraRef; skipping streaming patch"); return p; }
            int extraCount = BitConverter.ToInt32(body, pos);
            pos += 4 + extraCount * 4;   // ExtraReferencedObjects[]
            if (extraCount < 0 || pos + 4 > body.Length) { Log.Warning("World tail parse OOB at StreamingLevels (extra={E}); skipping streaming patch", extraCount); return p; }
            int slCountOff = pos;
            int oldSl = BitConverter.ToInt32(body, pos); int afterSl = pos + 4 + oldSl * 4;  // old StreamingLevels[]
            if (oldSl < 0 || afterSl > body.Length) { Log.Warning("World tail parse OOB (oldSl={S}); skipping streaming patch", oldSl); return p; }
            using var ms = new MemoryStream();
            ms.Write(body, 0, slCountOff);                                   // up to (not incl) StreamingLevels count
            ms.Write(BitConverter.GetBytes(streamingPkgs.Count), 0, 4);
            foreach (var pk in streamingPkgs) ms.Write(BitConverter.GetBytes(pk), 0, 4);
            ms.Write(body, afterSl, body.Length - afterSl);                   // trailing native bytes after old array
            var patched = ms.ToArray();
            var outp = new byte[start + patched.Length];
            Array.Copy(p, 0, outp, 0, start);
            Array.Copy(patched, 0, outp, start, patched.Length);
            Log.Information("Patched UWorld.StreamingLevels: {Old} -> {New}", oldSl, streamingPkgs.Count);
            return outp;
        }
        catch (Exception ex) { Log.Warning(ex, "Streaming patch failed; leaving World untouched"); return p; }
    }

    /// <summary>Clone a known-loadable editor StaticMesh (the engine Cube) renamed to a target mesh, copying
    /// its bulk-data region VERBATIM (FByteBulkData offsets are relative to BulkDataStartOffset, so a whole-
    /// region copy stays valid regardless of our rebuilt header). Produces a loadable placeholder mesh so
    /// map StaticMeshActor refs resolve and render. Returns true on success.</summary>
    public static bool CloneMesh(string cubePath, string outFile, string targetShort, string targetPackagePath,
        byte[]? realFRawMesh = null, IReadOnlyList<(string pkg, string name, string slot)>? materials = null,
        MeshWriter.MeshBounds? bounds = null)
    {
        byte[] data;
        try { data = File.ReadAllBytes(cubePath); } catch { return false; }
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(cubePath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "cube parse failed"); return false; }

        var oldPath = pkg.NameMap[0].Name ?? "";                       // /Engine/BasicShapes/Cube
        var oldShort = oldPath.Contains('/') ? oldPath[(oldPath.LastIndexOf('/') + 1)..] : oldPath;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [oldPath] = targetPackagePath,
            [oldPath + "." + oldShort] = targetPackagePath + "." + targetShort,
            [oldShort] = targetShort,
        };

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);
        // Cube bulk region = from BulkDataStartOffset to EOF (strip a trailing package tag if present).
        int bulkStart = (int)pkg.Summary.BulkDataStartOffset;
        int bulkEnd = data.Length;
        if (bulkEnd - bulkStart >= 4 && BitConverter.ToUInt32(data, bulkEnd - 4) == 0x9E2A83C1u) bulkEnd -= 4;
        long cubeBulkLen = (bulkStart > 0 && bulkEnd > bulkStart) ? bulkEnd - bulkStart : 0;

        int CubeName(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
        foreach (var e in pkg.ExportMap)
        {
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            // Real geometry: re-point the StaticMesh's FRawMesh bulk header at the appended real blob (offset
            // = after the cube bulk) + new size + new source Guid (forces RenderData rebuild from real mesh).
            if (realFRawMesh != null && e.ClassName == "StaticMesh")
                MeshWriter.PatchFRawMeshHeader(payload, realFRawMesh.Length, cubeBulkLen);
            // Do not splice ExtendedBounds into cloned meshes. UE5 LWC expects nested BoxSphereBounds vectors as
            // doubles and malformed stale bounds here can crash map load before the editor rebuilds the mesh.
            // Replace the cube's single WorldGridMaterial slot with the mesh's real N material slots, so each
            // section renders its own material (FaceMaterialIndices in the FRawMesh select the slot).
            if (e.ClassName == "StaticMesh" && materials is { Count: > 0 })
            {
                // UStaticMesh serializes StaticMaterials NATIVELY (Ar << StaticMaterials), not as a tagged property,
                // so the cube's native 1-slot WorldGridMaterial array is what the editor actually reads. Patch it.
                payload = PatchNativeStaticMaterials(payload, materials, spw);
                // Rewrite SectionInfoMap/OriginalSectionInfoMap (section i -> material i; these ARE tagged UPROPERTYs
                // at this version) so the build maps each render section to its own slot.
                payload = InjectSectionInfoMaps(payload, materials.Count, spw, CubeName);
            }
            if (realFRawMesh != null && e.ClassName == "StaticMesh")
                payload = InjectStaticMeshLightmapOverrides(payload, spw, CubeName);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0,
                e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload, (uint)e.ObjectFlags, e.IsAsset);
        }

        if (cubeBulkLen > 0)
        {
            var bulk = new byte[cubeBulkLen];
            Array.Copy(data, bulkStart, bulk, 0, bulk.Length);
            spw.AddBulk(bulk);                       // cube bulk first (keeps its internal offsets valid)
        }
        if (realFRawMesh != null) spw.AddBulk(realFRawMesh);   // appended at relative offset == cubeBulkLen
        spw.Write(outFile);
        Log.Information("Cloned mesh {Short} -> {Out} (bulk {B}B)", targetShort, outFile, bulkEnd - bulkStart);
        return true;
    }

    public sealed record MeshCloneSpec(
        string Name,
        byte[]? RealFRawMesh = null,
        IReadOnlyList<(string pkg, string name, string slot)>? Materials = null,
        MeshWriter.MeshBounds? Bounds = null);

    public static bool CloneMeshPackage(string cubePath, string outFile, string targetPackagePath, IReadOnlyList<MeshCloneSpec> meshes)
    {
        if (meshes.Count == 0) return false;
        byte[] data;
        try { data = File.ReadAllBytes(cubePath); } catch { return false; }
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(cubePath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "cube parse failed"); return false; }

        var staticMeshExport = Array.FindIndex(pkg.ExportMap, e => e.ClassName == "StaticMesh");
        if (staticMeshExport < 0) return false;

        var oldPath = pkg.NameMap[0].Name ?? "";
        var oldShort = oldPath.Contains('/') ? oldPath[(oldPath.LastIndexOf('/') + 1)..] : oldPath;
        var firstName = meshes[0].Name;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [oldPath] = targetPackagePath,
            [oldPath + "." + oldShort] = targetPackagePath + "." + firstName,
            [oldShort] = firstName,
        };

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        int bulkStart = (int)pkg.Summary.BulkDataStartOffset;
        int bulkEnd = data.Length;
        if (bulkEnd - bulkStart >= 4 && BitConverter.ToUInt32(data, bulkEnd - 4) == 0x9E2A83C1u) bulkEnd -= 4;
        long cubeBulkLen = (bulkStart > 0 && bulkEnd > bulkStart) ? bulkEnd - bulkStart : 0;

        int CubeName(string s) => Array.FindIndex(pkg.NameMap, n => n.Name == s);
        byte[] ExportPayload(int i)
        {
            var e = pkg.ExportMap[i];
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            return payload;
        }

        for (var i = 0; i < pkg.ExportMap.Length; i++)
        {
            if (i == staticMeshExport) continue;
            var e = pkg.ExportMap[i];
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0,
                e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, ExportPayload(i), (uint)e.ObjectFlags, e.IsAsset);
        }

        long realBulkOffset = cubeBulkLen;
        var cubeStatic = pkg.ExportMap[staticMeshExport];
        foreach (var mesh in meshes)
        {
            var payload = ExportPayload(staticMeshExport);
            if (mesh.RealFRawMesh != null)
                MeshWriter.PatchFRawMeshHeader(payload, mesh.RealFRawMesh.Length, realBulkOffset);
            // Let Unreal rebuild ExtendedBounds from the RawMesh instead of carrying generated bounds through load.
            if (mesh.Materials is { Count: > 0 })
            {
                payload = PatchNativeStaticMaterials(payload, mesh.Materials, spw);
                payload = InjectSectionInfoMaps(payload, mesh.Materials.Count, spw, CubeName);
            }
            if (mesh.RealFRawMesh != null)
                payload = InjectStaticMeshLightmapOverrides(payload, spw, CubeName);
            spw.AddExportRaw(spw.Name(mesh.Name), 0, cubeStatic.ClassIndex?.Index ?? 0, cubeStatic.SuperIndex?.Index ?? 0,
                cubeStatic.TemplateIndex?.Index ?? 0, cubeStatic.OuterIndex?.Index ?? 0, payload, (uint)cubeStatic.ObjectFlags, true);
            if (mesh.RealFRawMesh != null) realBulkOffset += mesh.RealFRawMesh.Length;
        }

        if (cubeBulkLen > 0)
        {
            var bulk = new byte[cubeBulkLen];
            Array.Copy(data, bulkStart, bulk, 0, bulk.Length);
            spw.AddBulk(bulk);
        }
        foreach (var mesh in meshes)
            if (mesh.RealFRawMesh != null) spw.AddBulk(mesh.RealFRawMesh);
        spw.Write(outFile);
        Log.Information("Cloned mesh package {Pkg} -> {Out} ({N} StaticMesh export(s))", targetPackagePath, outFile, meshes.Count);
        return true;
    }

    /// <summary>Replace the cloned cube's single-entry StaticMaterials with an N-slot array (one FStaticMaterial
    /// per material the source mesh uses). Each slot's MaterialInterface points at a freshly-added import for the
    /// dumped /Game material (our materials are always written as UMaterial, so import class = "Material").</summary>
    private static byte[] InjectStaticMaterials(byte[] payload, IReadOnlyList<(string pkg, string name, string slot)> mats,
        SynthPackageWriter spw, Func<string, int> cubeName)
    {
        NodePayloadWalker.StructPropertyIdx = cubeName("StructProperty");
        NodePayloadWalker.BoolPropertyIdx = cubeName("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = cubeName("ByteProperty");
        NodePayloadWalker.EnumPropertyIdx = cubeName("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = cubeName("ArrayProperty");
        NodePayloadWalker.SetPropertyIdx = cubeName("SetProperty");
        NodePayloadWalker.MapPropertyIdx = cubeName("MapProperty");
        int noneIdx = cubeName("None"), smIdx = cubeName("StaticMaterials");
        if (noneIdx < 0 || smIdx < 0) { Log.Warning("InjectStaticMaterials: cube lacks None/StaticMaterials name"); return payload; }

        int start, end;
        try { (start, end) = NodePayloadWalker.FindPropertySpan(payload, 0, noneIdx, smIdx); }
        catch (Exception ex) { Log.Warning(ex, "InjectStaticMaterials: walk failed"); return payload; }
        if (start < 0) { Log.Warning("InjectStaticMaterials: StaticMaterials property not found"); return payload; }

        var pkgCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var slots = new List<(int matImport, string slot)>(mats.Count);
        foreach (var (mpkg, mname, slot) in mats)
        {
            if (!pkgCache.TryGetValue(mpkg, out var pimp)) { pimp = spw.AddImport("/Script/CoreUObject", "Package", 0, mpkg); pkgCache[mpkg] = pimp; }
            int mimp = spw.AddImport("/Script/Engine", "Material", pimp, mname);
            slots.Add((mimp, string.IsNullOrEmpty(slot) ? mname : slot));
        }

        byte[] newBytes;
        using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
            new TaggedPropertyWriter(w, spw.Name).StaticMaterialsArray(slots); w.Flush(); newBytes = ms.ToArray(); }

        var outp = new byte[start + newBytes.Length + (payload.Length - end)];
        Array.Copy(payload, 0, outp, 0, start);
        newBytes.CopyTo(outp, start);
        Array.Copy(payload, end, outp, start + newBytes.Length, payload.Length - end);
        Log.Information("StaticMaterials: {N} slot(s) [{S}]", slots.Count, string.Join(", ", mats.Select(m => m.name)));
        return outp;
    }

    /// <summary>Rewrite the cloned cube's NATIVE StaticMaterials array (UStaticMesh serializes it via Ar &lt;&lt;
    /// StaticMaterials, after the source models + bHasSpeedTreeWind — NOT as a tagged property). It is the last
    /// native field (only zero padding follows in 4.21), so it spans from its int32 count to the end of the payload.
    /// 4.21 editor FStaticMaterial = MaterialInterface(FPackageIndex,4) + MaterialSlotName(FName,8) +
    /// ImportedMaterialSlotName(FName,8) + FMeshUVChannelInfo(bInitialized i32 + bOverrideDensities i32 + 4 floats =24)
    /// = 44 bytes. UVChannelData is left uninitialised; the editor recomputes it on build.</summary>
    private static byte[] PatchNativeStaticMaterials(byte[] payload, IReadOnlyList<(string pkg, string name, string slot)> mats,
        SynthPackageWriter spw)
    {
        const int ELEM = 44;
        // Locate the cube's native array at the tail: an int32 count C for which C*44+4 == bytes-to-end and the first
        // element's MaterialInterface is a negative FPackageIndex (an import).
        int smStart = -1, found = 0;
        for (int c = 1; c <= 64; c++)
        {
            int pos = payload.Length - (c * ELEM + 4);
            if (pos < 0) break;
            if (BitConverter.ToInt32(payload, pos) != c) continue;
            if (BitConverter.ToInt32(payload, pos + 4) >= 0) continue;   // MaterialInterface must be an import (<0)
            smStart = pos; found = c; break;
        }
        if (smStart < 0) { Log.Warning("PatchNativeStaticMaterials: native StaticMaterials not found at payload tail"); return payload; }

        var pkgCache = new Dictionary<string, int>(StringComparer.Ordinal);
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        w.Write(mats.Count);
        foreach (var (mpkg, mname, slot) in mats)
        {
            if (!pkgCache.TryGetValue(mpkg, out var pimp)) { pimp = spw.AddImport("/Script/CoreUObject", "Package", 0, mpkg); pkgCache[mpkg] = pimp; }
            int mimp = spw.AddImport("/Script/Engine", "Material", pimp, mname);
            var sname = string.IsNullOrEmpty(slot) ? mname : slot;
            w.Write(mimp);                                  // MaterialInterface (FPackageIndex)
            w.Write(spw.Name(sname)); w.Write(0);           // MaterialSlotName
            w.Write(spw.Name(sname)); w.Write(0);           // ImportedMaterialSlotName (editor-only)
            w.Write(0); w.Write(0);                         // UVChannelData: bInitialized + bOverrideDensities
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); // LocalUVDensities[4] (recomputed on build)
        }
        w.Flush();
        var nbytes = ms.ToArray();

        var outp = new byte[smStart + nbytes.Length];
        Array.Copy(payload, 0, outp, 0, smStart);
        nbytes.CopyTo(outp, smStart);
        Log.Information("Native StaticMaterials: cube {C}-slot -> {N} slot(s) [{S}] @tail {Off}",
            found, mats.Count, string.Join(", ", mats.Select(m => m.name)), smStart);
        return outp;
    }

    /// <summary>Inject a fresh SectionInfoMap + OriginalSectionInfoMap (each an N-entry TMap mapping render
    /// section i -> material slot i) just before the terminating None. They appear after the cube's stale 1-entry
    /// SectionInfoMap, so UE's last-property-wins deserialization uses ours — without this the editor's static-mesh
    /// build resets StaticMaterials back to the cube's single WorldGridMaterial slot.</summary>
    private static byte[] InjectSectionInfoMaps(byte[] payload, int sectionCount, SynthPackageWriter spw, Func<string, int> cubeName)
    {
        NodePayloadWalker.StructPropertyIdx = cubeName("StructProperty");
        NodePayloadWalker.BoolPropertyIdx = cubeName("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = cubeName("ByteProperty");
        NodePayloadWalker.EnumPropertyIdx = cubeName("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = cubeName("ArrayProperty");
        NodePayloadWalker.SetPropertyIdx = cubeName("SetProperty");
        NodePayloadWalker.MapPropertyIdx = cubeName("MapProperty");
        int noneIdx = cubeName("None");
        if (noneIdx < 0) { Log.Warning("InjectSectionInfoMaps: no None in name table"); return payload; }

        int afterNone;
        try { afterNone = NodePayloadWalker.SkipTaggedProperties(payload, 0, noneIdx); }
        catch (Exception ex) { Log.Warning(ex, "InjectSectionInfoMaps: tagged-prop walk failed"); return payload; }
        int noneStart = afterNone - 8;            // None FName is 8 bytes (idx + number)
        if (noneStart < 0) return payload;

        var identity = Enumerable.Range(0, sectionCount).ToList();   // section i -> material i
        byte[] inject;
        using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
            var tpw = new TaggedPropertyWriter(w, spw.Name);
            tpw.SectionInfoMap("SectionInfoMap", identity);
            tpw.SectionInfoMap("OriginalSectionInfoMap", identity);
            w.Flush(); inject = ms.ToArray(); }

        var outp = new byte[payload.Length + inject.Length];
        Array.Copy(payload, 0, outp, 0, noneStart);
        inject.CopyTo(outp, noneStart);
        Array.Copy(payload, noneStart, outp, noneStart + inject.Length, payload.Length - noneStart);
        Log.Information("Injected SectionInfoMap+OriginalSectionInfoMap ({N} section(s), {B}B)", sectionCount, inject.Length);
        return outp;
    }

    private static byte[] InjectStaticMeshLightmapOverrides(byte[] payload, SynthPackageWriter spw, Func<string, int> cubeName)
    {
        NodePayloadWalker.StructPropertyIdx = cubeName("StructProperty");
        NodePayloadWalker.BoolPropertyIdx = cubeName("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = cubeName("ByteProperty");
        NodePayloadWalker.EnumPropertyIdx = cubeName("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = cubeName("ArrayProperty");
        NodePayloadWalker.SetPropertyIdx = cubeName("SetProperty");
        NodePayloadWalker.MapPropertyIdx = cubeName("MapProperty");
        int noneIdx = cubeName("None");
        if (noneIdx < 0) { Log.Warning("InjectStaticMeshLightmapOverrides: no None in name table"); return payload; }

        int afterNone;
        try { afterNone = NodePayloadWalker.SkipTaggedProperties(payload, 0, noneIdx); }
        catch (Exception ex) { Log.Warning(ex, "InjectStaticMeshLightmapOverrides: tagged-prop walk failed"); return payload; }
        int noneStart = afterNone - 8;
        if (noneStart < 0) return payload;

        byte[] inject;
        using (var ms = new MemoryStream())
        {
            using var w = new FArchiveWriter(ms);
            var tpw = new TaggedPropertyWriter(w, spw.Name);
            tpw.Int("LightMapCoordinateIndex", 0);
            tpw.Int("LightMapResolution", 0);
            tpw.Float("LightmapUVDensity", 0);
            w.Flush();
            inject = ms.ToArray();
        }

        var outp = new byte[payload.Length + inject.Length];
        Array.Copy(payload, 0, outp, 0, noneStart);
        inject.CopyTo(outp, noneStart);
        Array.Copy(payload, noneStart, outp, noneStart + inject.Length, payload.Length - noneStart);
        Log.Information("Injected StaticMesh lightmap overrides (coord=0, res=0, density=0)");
        return outp;
    }

    private static byte[] ReplaceExtendedBounds(byte[] payload, MeshWriter.MeshBounds bounds, SynthPackageWriter spw,
        Func<string, int> cubeName)
    {
        NodePayloadWalker.StructPropertyIdx = cubeName("StructProperty");
        NodePayloadWalker.BoolPropertyIdx = cubeName("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = cubeName("ByteProperty");
        NodePayloadWalker.EnumPropertyIdx = cubeName("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = cubeName("ArrayProperty");
        NodePayloadWalker.SetPropertyIdx = cubeName("SetProperty");
        NodePayloadWalker.MapPropertyIdx = cubeName("MapProperty");
        int noneIdx = cubeName("None");
        if (noneIdx < 0) { Log.Warning("ReplaceExtendedBounds: no None in name table"); return payload; }

        int start = -1, end = -1;
        var ebIdx = cubeName("ExtendedBounds");
        try
        {
            if (ebIdx >= 0) (start, end) = NodePayloadWalker.FindPropertySpan(payload, 0, noneIdx, ebIdx);
            if (start < 0)
            {
                var afterNone = NodePayloadWalker.SkipTaggedProperties(payload, 0, noneIdx);
                start = end = afterNone - 8;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "ReplaceExtendedBounds: tagged-prop walk failed"); return payload; }
        if (start < 0 || end < start) return payload;

        byte[] replacement;
        using (var ms = new MemoryStream())
        {
            using var w = new FArchiveWriter(ms);
            new TaggedPropertyWriter(w, spw.Name).BoxSphereBounds("ExtendedBounds", bounds);
            w.Flush();
            replacement = ms.ToArray();
        }

        var outp = new byte[start + replacement.Length + (payload.Length - end)];
        Array.Copy(payload, 0, outp, 0, start);
        replacement.CopyTo(outp, start);
        Array.Copy(payload, end, outp, start + replacement.Length, payload.Length - end);
        Log.Information("ExtendedBounds: origin ({OX:F2},{OY:F2},{OZ:F2}) extent ({EX:F2},{EY:F2},{EZ:F2}) radius {R:F2}",
            bounds.OriginX, bounds.OriginY, bounds.OriginZ, bounds.ExtentX, bounds.ExtentY, bounds.ExtentZ, bounds.SphereRadius);
        return outp;
    }

    /// <summary>Splice a BoolProperty (value lives in the tag) into a cloned export's tagged-property block,
    /// just before the terminating None so it overrides any earlier/default value. Name indices resolve in the
    /// synth writer (appended after the verbatim cube names, so existing payload indices stay valid).</summary>
    private static byte[] InjectBoolProp(byte[] payload, string propName, bool value, SynthPackageWriter spw, Func<string, int> cubeName)
    {
        NodePayloadWalker.StructPropertyIdx = cubeName("StructProperty");
        NodePayloadWalker.BoolPropertyIdx = cubeName("BoolProperty");
        NodePayloadWalker.BytePropertyIdx = cubeName("ByteProperty");
        NodePayloadWalker.EnumPropertyIdx = cubeName("EnumProperty");
        NodePayloadWalker.ArrayPropertyIdx = cubeName("ArrayProperty");
        NodePayloadWalker.SetPropertyIdx = cubeName("SetProperty");
        NodePayloadWalker.MapPropertyIdx = cubeName("MapProperty");
        int noneIdx = cubeName("None");
        if (noneIdx < 0) { Log.Warning("InjectBoolProp: no None in name table"); return payload; }

        int afterNone;
        try { afterNone = NodePayloadWalker.SkipTaggedProperties(payload, 0, noneIdx); }
        catch (Exception ex) { Log.Warning(ex, "InjectBoolProp: tagged-prop walk failed; skipping {P}", propName); return payload; }
        int noneStart = afterNone - 8;            // None FName is 8 bytes (idx + number)
        if (noneStart < 0) return payload;

        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        w.Write(spw.Name(propName)); w.Write(0);          // Name FName
        w.Write(spw.Name("BoolProperty")); w.Write(0);    // Type FName
        w.Write(0);                                       // Size (bool value is in tag)
        w.Write(0);                                       // ArrayIndex
        w.WriteByteBool(value);                           // BoolVal
        w.WriteByteBool(false);                           // HasPropertyGuid
        w.Flush();
        var inject = ms.ToArray();

        var outp = new byte[payload.Length + inject.Length];
        Array.Copy(payload, 0, outp, 0, noneStart);
        inject.CopyTo(outp, noneStart);
        Array.Copy(payload, noneStart, outp, noneStart + inject.Length, payload.Length - noneStart);
        Log.Information("Injected {P}={V} into StaticMesh ({N}B)", propName, value, inject.Length);
        return outp;
    }

    /// <summary>Clone an engine editor material (template: /Engine/EngineMaterials/EmissiveTexturedMaterial — an
    /// unlit material that samples one texture) renamed to the target, and redirect its texture import to
    /// <paramref name="texturePackagePath"/>.<paramref name="textureName"/>. The editor recompiles shaders from the
    /// (verbatim-cloned) expression graph on load, so the texture displays. Returns true on success.</summary>
    public static bool CloneMaterial(string templatePath, string outFile, string targetShort, string targetPackagePath,
        string texturePackagePath, string textureName)
    {
        byte[] data;
        try { data = File.ReadAllBytes(templatePath); } catch (Exception ex) { Log.Error(ex, "material template read"); return false; }
        Package pkg;
        try
        {
            var ar = new FByteArchive(Path.GetFileNameWithoutExtension(templatePath), data, new VersionContainer(EGame.GAME_UE4_21));
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        }
        catch (Exception ex) { Log.Error(ex, "material template parse"); return false; }

        var oldPath = pkg.NameMap[0].Name ?? "";                       // /Engine/EngineMaterials/EmissiveTexturedMaterial
        var oldShort = oldPath.Contains('/') ? oldPath[(oldPath.LastIndexOf('/') + 1)..] : oldPath;
        const string texPkgOld = "/Engine/EngineMaterials/DefaultDiffuse";  // template's texture package import
        const string texObjOld = "DefaultDiffuse";                          // template's texture object import
        var rename = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [oldPath] = targetPackagePath,
            [oldPath + "." + oldShort] = targetPackagePath + "." + targetShort,
            [oldShort] = targetShort,
            [texPkgOld] = texturePackagePath,
            [texObjOld] = textureName,
        };

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        spw.PackageFlags = (uint)pkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = pkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in pkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        int bulkStart = (int)pkg.Summary.BulkDataStartOffset;
        int bulkEnd = data.Length;
        if (bulkEnd - bulkStart >= 4 && BitConverter.ToUInt32(data, bulkEnd - 4) == 0x9E2A83C1u) bulkEnd -= 4;
        long bulkLen = (bulkStart > 0 && bulkEnd > bulkStart) ? bulkEnd - bulkStart : 0;

        foreach (var e in pkg.ExportMap)
        {
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number, e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0,
                e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0, payload, (uint)e.ObjectFlags, e.IsAsset);
        }
        if (bulkLen > 0) { var bulk = new byte[bulkLen]; Array.Copy(data, bulkStart, bulk, 0, bulk.Length); spw.AddBulk(bulk); }
        spw.Write(outFile);
        Log.Information("Cloned material {Short} -> {Out} (tex {T})", targetShort, outFile, texturePackagePath + "." + textureName);
        return true;
    }

    public static void Build(string cookedPath, string outDir)
    {
        var data = File.ReadAllBytes(cookedPath);
        var bpName = Path.GetFileNameWithoutExtension(cookedPath);             // e.g. BP_HandProxyExample
        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, bpName + ".uasset");
        // Dev path: a local provider with ReadScriptData so the ubergraph bytecode is available for graph recovery.
        CUE4Parse.FileProvider.IFileProvider? provider = null;
        try
        {
            var p = new CUE4Parse.FileProvider.DefaultFileProvider(
                Path.GetDirectoryName(Path.GetFullPath(cookedPath))!, System.IO.SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_UE4_21));
            p.ReadScriptData = true;
            provider = p;
        }
        catch { /* graph recovery degrades to the bare event node */ }
        BuildCore(data, bpName, "/Game/" + bpName, outFile, provider);
    }

    /// <summary>Reconstruct an editor-openable blueprint from the COMBINED cooked bytes (.uasset+.uexp): clone the
    /// cooked exports verbatim (zeroing bCooked on UClass-family payloads) and append the editor exports the cooker
    /// stripped — UBlueprint (the browsable asset) + EventGraph + a BeginPlay K2Node_Event. Returns false on any
    /// failure so the pipeline can fall back to the plain uncooked write. <paramref name="packagePath"/> is the real
    /// "/Game/..." path so the asset lands at the right place in the content browser.</summary>
    public static bool BuildCore(byte[] data, string bpName, string packagePath, string outFile,
        CUE4Parse.FileProvider.IFileProvider? provider = null)
    {
        Package pkg;
        try
        {
            var ar = new FByteArchive(bpName, data, new VersionContainer(EGame.GAME_UE4_21));
            // provider (with ReadScriptData=true) enables UFunction bytecode -> ubergraph call-chain recovery.
            pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, provider, false);
        }
        catch (Exception ex) { Log.Warning(ex, "BuildCore parse failed for {Bp}", bpName); return false; }

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, packagePath);

        // 1) Pre-add cooked names in exact order so indices match the verbatim payloads.
        foreach (var n in pkg.NameMap) spw.AddRawName(n.Name ?? "None");

        // 2) Re-add cooked imports preserving name index+number and outer FPackageIndex.
        foreach (var imp in pkg.ImportMap)
            spw.AddImportRaw(imp.ClassPackage.Index, imp.ClassPackage.Number, imp.ClassName.Index, imp.ClassName.Number,
                imp.OuterIndex?.Index ?? 0, imp.ObjectName.Index, imp.ObjectName.Number);

        // bCooked patch signature: [bDeprecatedForceScriptOrder=0][Dummy FName None][bCooked=1] -> zero bCooked.
        var noneIdx = Array.FindIndex(pkg.NameMap, n => string.Equals(n.Name, "None", StringComparison.Ordinal));

        // 3) Re-add cooked exports verbatim (payload sliced from file).
        int bgcPkg = 0, bgcSuper = 0, scsPkg = 0;
        for (var i = 0; i < pkg.ExportMap.Length; i++)
        {
            var e = pkg.ExportMap[i];
            var off = (int)e.SerialOffset; var size = (int)e.SerialSize;
            var payload = new byte[size];
            Array.Copy(data, off, payload, 0, size);
            // Editor must NOT take the cooked class path: zero bCooked in UClass-family payloads.
            if (noneIdx >= 0 && (e.ClassName == "BlueprintGeneratedClass" || e.ClassName == "Class"))
                PatchBCooked(payload, noneIdx);
            var pkgIdx = spw.AddExportRaw(e.ObjectName.Index, e.ObjectName.Number,
                e.ClassIndex?.Index ?? 0, e.SuperIndex?.Index ?? 0, e.TemplateIndex?.Index ?? 0, e.OuterIndex?.Index ?? 0,
                payload, (uint)e.ObjectFlags, e.IsAsset);
            if (e.ClassName == "BlueprintGeneratedClass") { bgcPkg = pkgIdx; bgcSuper = e.SuperIndex?.Index ?? 0; }
            if (e.ClassName == "SimpleConstructionScript") scsPkg = pkgIdx;   // cooked BPs keep the SCS; wire UBlueprint to it
        }
        if (bgcPkg == 0) { Log.Warning("BuildCore: no BlueprintGeneratedClass export in {Bp}", bpName); return false; }

        // 4) Imports we need for the editor exports (appended).
        int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        int bgPkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/BlueprintGraph");
        int impBlueprint = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Blueprint");
        int impEdGraph = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "EdGraph");
        int impActor = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "Actor");
        int impK2Event = spw.AddImport("/Script/CoreUObject", "Class", bgPkg, "K2Node_Event");
        int impSchema = spw.AddImport("/Script/CoreUObject", "Class", bgPkg, "EdGraphSchema_K2");

        // 5) Recover the event-graph call chain from the cooked ubergraph bytecode (ordered impure /Script/ calls).
        //    Needs the package parsed with ReadScriptData=true; degrades to a bare BeginPlay node otherwise.
        var chain = ExtractExecChainCore(pkg);
        const int MaxChain = 400;                   // show every called function; cap only guards pathological ubergraphs
        if (chain.Count > MaxChain) { Log.Information("Ubergraph chain truncated {N} -> {M} for {Bp}", chain.Count, MaxChain, bpName); chain = chain.Take(MaxChain).ToList(); }

        // 6) Plan export indices (appended after cooked exports): UBlueprint, EventGraph, K2Node_Event, then one
        //    K2Node_CallFunction per recovered call.
        int next = pkg.ExportMap.Length;            // current export count
        int ubPkg = next + 1, egPkg = next + 2, evPkg = next + 3;
        var callPkgs = Enumerable.Range(0, chain.Count).Select(i => next + 4 + i).ToArray();
        var execIds = chain.Select(_ => FGuid16.NewGuid()).ToArray();   // each call's "execute" pin id
        var thenIds = chain.Select(_ => FGuid16.NewGuid()).ToArray();   // each call's "then" pin id
        var evThenId = FGuid16.NewGuid();                               // the BeginPlay event's "then" pin id

        // 7) Build payloads.
        var eventGuid = FGuid16.NewGuid();
        var recoveredVars = RecoverSimpleVariables(pkg, bgcPkg);
        if (recoveredVars.Count > 0) Log.Information("Recovered {N} variable(s) for {Bp}: {V}", recoveredVars.Count, bpName,
            string.Join(", ", recoveredVars.Select(v => $"{v.name}:{v.category}")));
        var ubPayload = BuildBlueprint(spw, classGeneratedBy: bgcPkg, parentClass: bgcSuper, generatedClass: bgcPkg, eventGraph: egPkg, vars: recoveredVars, scs: scsPkg);
        var egPayload = BuildEdGraph(spw, schema: impSchema, nodes: new[] { evPkg }.Concat(callPkgs).ToArray());
        var evPayload = BuildEventNode(spw, ownerPkg: evPkg, actorClass: impActor, nodeGuid: eventGuid,
            thenPinId: evThenId, thenLink: chain.Count > 0 ? (callPkgs[0], execIds[0]) : null);

        // 8) Append editor exports. UBlueprint is the asset (RF_Public|RF_Standalone).
        spw.AddExport(bpName, impBlueprint, 0, 0, ubPayload, objectFlags: 0x1 | 0x2, isAsset: true);   // Blueprint
        spw.AddExport("EventGraph", impEdGraph, 0, ubPkg, egPayload, objectFlags: 0x1);                 // EdGraph (outer=Blueprint)
        spw.AddExport("K2Node_Event_0", impK2Event, 0, egPkg, evPayload, objectFlags: 0x1);             // K2Node_Event (outer=EventGraph)

        // 9) Append the recovered chain as wired K2Node_CallFunction nodes: BeginPlay.then -> call0 -> call1 -> …
        //    (same synth-node format ReconstructChain proved in-editor). The editor regenerates data pins from the
        //    FunctionReference on load; unresolvable game functions show as standard "missing function" error nodes.
        if (chain.Count > 0)
        {
            int impCallFunc = spw.AddImport("/Script/CoreUObject", "Class", bgPkg, "K2Node_CallFunction");
            var pkgImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
            var classImpCache = new Dictionary<string, int>(StringComparer.Ordinal);
                int ClassImp(string scriptPkg, string cls)
                {
                    if (string.IsNullOrWhiteSpace(scriptPkg) || string.IsNullOrWhiteSpace(cls)) return 0;
                    var key = scriptPkg + "." + cls;
                    if (classImpCache.TryGetValue(key, out var c)) return c;
                    if (!pkgImpCache.TryGetValue(scriptPkg, out var pImp))
                    {
                        pImp = FindPackageImport(pkg, scriptPkg);
                        if (pImp == 0) pImp = spw.AddImport("/Script/CoreUObject", "Package", 0, scriptPkg);
                        pkgImpCache[scriptPkg] = pImp;
                    }
                    c = FindTemplateImport(pkg, "/Script/CoreUObject", "Class", pImp, cls);
                    if (c == 0) c = spw.AddImport("/Script/CoreUObject", "Class", pImp, cls);
                    classImpCache[key] = c; return c;
                }
            for (int i = 0; i < chain.Count; i++)
            {
                var (scriptPkg, cls, func) = chain[i];
                int classImp = ClassImp(scriptPkg, cls);
                var execPin = new SynthPin { OwningNodePkg = callPkgs[i], PinId = execIds[i], PinName = "execute", Category = "exec", SubCategory = "None", Direction = 0 };
                execPin.LinkedTo.Add(i == 0 ? (evPkg, evThenId) : (callPkgs[i - 1], thenIds[i - 1]));
                var thenOut = new SynthPin { OwningNodePkg = callPkgs[i], PinId = thenIds[i], PinName = "then", Category = "exec", SubCategory = "None", Direction = 1 };
                if (i + 1 < chain.Count) thenOut.LinkedTo.Add((callPkgs[i + 1], execIds[i + 1]));

                using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name);
                t.Struct("FunctionReference", "MemberReference", () =>
                {
                    var inner = new TaggedPropertyWriter(w, spw.Name);
                    WriteMemberReference(inner, classImp, func);
                });
                t.Int("NodePosX", 360 + i * 300);
                t.Int("NodePosY", 48);
                t.GuidStruct("NodeGuid", FGuid16.NewGuid());
                t.WriteNone();
                new PinSerializer(w, spw.Name).WriteOwningPins(new[] { execPin, thenOut });
                w.Flush();
                spw.AddExportRaw(spw.Name("K2Node_CallFunction"), 7 + i, impCallFunc, 0, 0, egPkg, ms.ToArray(), 0x1, false);
            }
            Log.Information("Recovered ubergraph chain for {Bp}: {N} call(s): {C}", bpName, chain.Count,
                string.Join(" -> ", chain.Select(c => c.func)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        spw.Write(outFile);
        Log.Information("Built editor blueprint -> {Out} (BGC pkgidx {B}, +UBlueprint/EventGraph/K2Node_Event{Chain})",
            outFile, bgcPkg, chain.Count > 0 ? $"/+{chain.Count} CallFunction" : "");
        return true;
    }

    /// <summary>Zero bCooked in a UClass-family payload so the editor takes the non-cooked load path.
    /// 4.21 UClass::Serialize writes after the tagged-prop stream: bDeprecatedForceScriptOrder(int32=0),
    /// Dummy FName None(index+number), bCooked(int32=1). We find [00000000][noneIdx][00000000][01000000].</summary>
    private static void PatchBCooked(byte[] p, int noneIndex)
    {
        Span<byte> sig = stackalloc byte[16];
        BitConverter.TryWriteBytes(sig.Slice(4, 4), noneIndex);
        sig[12] = 0x01;
        int found = -1, count = 0;
        for (int i = 0; i + 16 <= p.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < 16; j++) if (p[i + j] != sig[j]) { ok = false; break; }
            if (ok) { found = i; count++; }
        }
        if (count == 1) { var at = found + 12; p[at] = p[at + 1] = p[at + 2] = p[at + 3] = 0; }
        else Log.Warning("bCooked patch skipped (matches={N})", count);
    }

    private static byte[] BuildBlueprint(SynthPackageWriter spw, int classGeneratedBy, int parentClass, int generatedClass, int eventGraph,
        IReadOnlyList<(string name, string category)>? vars = null, int scs = 0)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Object("ParentClass", parentClass);
        t.Object("GeneratedClass", generatedClass);
        t.Int("BlueprintSystemVersion", 2);
        t.GuidStruct("BlueprintGuid", FGuid16.NewGuid());
        t.ObjectArray("UbergraphPages", new[] { eventGraph });
        if (vars is { Count: > 0 }) t.NewVariables(vars);              // editor My-Blueprint variable list
        if (scs != 0) t.Object("SimpleConstructionScript", scs);       // surface the cloned component tree (Components panel)
        t.WriteNone();
        w.Flush(); return ms.ToArray();
    }

    /// <summary>Recover simple value-type variables from a cooked BlueprintGeneratedClass: its class properties are
    /// exports whose outer is the BGC. Map the cooked property class to a K2 pin category; skip non-POD types
    /// (object/struct/array/…) and the component object-properties, which need sub-references we don't synthesize yet.</summary>
    private static List<(string name, string category)> RecoverSimpleVariables(Package pkg, int bgcPkgIndex)
    {
        var cat = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BoolProperty"] = "bool", ["IntProperty"] = "int", ["FloatProperty"] = "float",
            ["StrProperty"] = "string", ["TextProperty"] = "text", ["NameProperty"] = "name", ["ByteProperty"] = "byte",
        };
        var vars = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in pkg.ExportMap)
        {
            if ((e.OuterIndex?.Index ?? 0) != bgcPkgIndex) continue;   // only the class's own properties
            if (!cat.TryGetValue(e.ClassName, out var c)) continue;    // simple value types only
            var n = e.ObjectName.Text;
            if (seen.Add(n)) vars.Add((n, c));
        }
        return vars;
    }

    private static byte[] BuildEdGraph(SynthPackageWriter spw, int schema, IReadOnlyList<int> nodes)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        t.Object("Schema", schema);
        t.ObjectArray("Nodes", nodes);
        t.GuidStruct("GraphGuid", FGuid16.NewGuid());
        t.WriteNone();
        w.Flush(); return ms.ToArray();   // UEdGraph has no native pin section
    }

    private static byte[] BuildEventNode(SynthPackageWriter spw, int ownerPkg, int actorClass, FGuid16 nodeGuid,
        FGuid16 thenPinId = default, (int nodePkg, FGuid16 pinId)? thenLink = null)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, spw.Name);
        // EventReference (FMemberReference struct): MemberParent=Actor, MemberName=ReceiveBeginPlay
        t.Struct("EventReference", "MemberReference", () =>
        {
            var inner = new TaggedPropertyWriter(w, spw.Name);
            inner.Object("MemberParent", actorClass);
            inner.Name("MemberName", "ReceiveBeginPlay");
            inner.WriteNone();
        });
        t.Bool("bOverrideFunction", true);
        t.GuidStruct("NodeGuid", nodeGuid);
        t.WriteNone();
        // UEdGraphNode::Serialize -> SerializeAsOwningNode(Pins): one output exec pin "then", optionally wired
        // to the first recovered CallFunction node.
        var thenPin = new SynthPin { OwningNodePkg = ownerPkg, PinName = "then", Category = "exec", Direction = 1 /*Output*/ };
        if (!thenPinId.Equals(default(FGuid16))) thenPin.PinId = thenPinId;
        if (thenLink is { } link) thenPin.LinkedTo.Add(link);
        new PinSerializer(w, spw.Name).WriteOwningPins(new[] { thenPin });
        w.Flush(); return ms.ToArray();
    }
}

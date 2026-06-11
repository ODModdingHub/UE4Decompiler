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
    private static byte[] PatchNodesArray(byte[] p, int nodesNameIdx, int arrayPropNameIdx, int newElemPkgIdx)
    {
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
                inner.Object("MemberParent", classImp);
                inner.Name("MemberName", func);
                inner.WriteNone();
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

    /// <summary>Core of the ubergraph decompile: walk the (already parsed, ReadScriptData=true) package's
    /// ExecuteUbergraph_* bytecode and return the ordered impure /Script/ function calls. Exports are loaded
    /// one at a time so a single broken export can't wipe the whole chain.</summary>
    private static List<(string scriptPkg, string cls, string func)> ExtractExecChainCore(Package pkg)
    {
        var result = new List<(string, string, string)>();
        CUE4Parse.UE4.Objects.UObject.UFunction? fn = null;
        for (int i = 0; i < pkg.ExportsLazy.Length && fn is null; i++)
        {
            if (!pkg.ExportMap[i].ObjectName.Text.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)) continue;
            try { fn = pkg.ExportsLazy[i].Value as CUE4Parse.UE4.Objects.UObject.UFunction; } catch { }
        }
        if (fn?.ScriptBytecode is not { Length: > 0 }) return result;

        foreach (var expr in fn.ScriptBytecode)
        {
            var node = KismetWalker.WalkOne(expr);
            var op = node.TryGetValue("Opcode", out var ov) ? ov as string : null;
            string? fref = null;
            if (op is "EX_Context" or "EX_Context_FailSilent")
            {
                if (node.TryGetValue("ContextExpression", out var ce) && ce is Dictionary<string, object?> cd)
                    fref = cd.TryGetValue("FunctionRef", out var fr) ? fr as string : null;
            }
            else if (op is "EX_FinalFunction" or "EX_VirtualFunction" or "EX_LocalFinalFunction" or "EX_LocalVirtualFunction")
                fref = node.TryGetValue("FunctionRef", out var fr) ? fr as string : null;
            // EX_Let*/EX_BindDelegate/EX_Jump/EX_Return/EX_ComputedJump => pure/control, skip.
            if (fref == null) continue;
            var parsed = ParseFunctionRef(fref);
            if (parsed != null && parsed.Value.scriptPkg.StartsWith("/Script/")) result.Add(parsed.Value);
        }
        return result;
    }

    /// <summary>"/Script/Engine.KismetSystemLibrary:PrintString" -> (/Script/Engine, KismetSystemLibrary, PrintString).</summary>
    private static (string scriptPkg, string cls, string func)? ParseFunctionRef(string fref)
    {
        var colon = fref.LastIndexOf(':');
        if (colon < 0) return null;
        var func = fref[(colon + 1)..];
        var left = fref[..colon];
        var dot = left.LastIndexOf('.');
        if (dot < 0) return null;
        return (left[..dot], left[(dot + 1)..], func);
    }

    /// <summary>Place a cooked map's actors into an editor map by SYNTHESIZING each actor + its root
    /// component fresh from CUE4Parse-parsed transforms (no byte-graft/remap). Actor = {RootComponent,
    /// ActorLabel} + None + int32 0; Component = {RelativeLocation/Rotation/Scale} + None + int32 0; both
    /// outers/refs assigned to new indices. Patches ULevel.Actors. No-mesh actors only for now (mesh actors
    /// need StaticMesh asset resolution).</summary>
    public static void PlaceActors(string cookedPath, string templatePath, string outDir, string targetShort, string targetPackagePath,
        string? cubePath = null, string? contentRoot = null)
    {
        var outFile = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, targetShort + ".uasset");
        // Dev path: re-parse the local cooked file as a 4.21 Package (which IS an IPackage) and feed it in.
        var ar = new FByteArchive(Path.GetFileNameWithoutExtension(cookedPath), File.ReadAllBytes(cookedPath), new VersionContainer(EGame.GAME_UE4_21));
        var pkg = new Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, (CUE4Parse.FileProvider.IFileProvider?)null, false);
        PlaceActorsCore(pkg, File.ReadAllBytes(templatePath), outFile, targetShort, targetPackagePath, cubePath, contentRoot);
    }

    /// <summary>Byte-based core: place a cooked map's reliably-loadable actors onto a template map, writing to <paramref name="outFile"/>.
    /// Filters out plugin/BP-class actors (e.g. /CustomMapTools/) so the result opens without missing-import crashes.</summary>
    public static void PlaceActorsCore(CUE4Parse.UE4.Assets.IPackage src, byte[] templateData, string outFile,
        string targetShort, string targetPackagePath, string? cubePath = null, string? contentRoot = null,
        Action<string, string, string>? onGameClass = null)
    {
        // Gather from the already-LOADED source package (uniform for legacy Package AND Zen IoPackage — Zen raw
        // bytes can't be re-parsed standalone). Exports carry Outer/Class ResolvedObjects + parsed properties.
        var srcExports = src.GetExports().ToList();

        // Gather placed actors (outer=PersistentLevel, actor-ish class) + their root component transform.
        var skip = new HashSet<string> { "Model", "Brush", "Polys", "Level", "World", "WorldSettings",
            "NavigationSystemModuleConfig", "BlueprintGeneratedClass", "None", "RecastNavMesh",
            "PhononProbeVolume", "NavLinkProxy" };   // plugin/custom-component classes that fail to load
        // Place actors from any /Script/ module. Game-native actor classes (/Script/Pavlov.*) get a stub, and we
        // report each as needing an AActor base (its component -> USceneComponent) via onGameClass so the stub is
        // actually spawnable — placing one whose stub defaulted to UObject is what crashed the editor before.
        var place = new List<(string actorPkg, string actorClass, string compPkg, string compClass, string compName, string label, float[] loc, float[] rot, float[] scale, string? meshPkg, string? meshName, CUE4Parse.UE4.Assets.Exports.UObject sourceComp)>();
        foreach (var e in srcExports)
        {
            // Top-level actor = export whose Outer is the PersistentLevel.
            if (e.Outer?.Name.Text != "PersistentLevel") continue;
            var cls = e.ExportType;
            if (skip.Contains(cls) || cls.EndsWith("Component")) continue;
            var actorPkg = ScriptPackageOf(e.Class);                     // "/Script/Engine" / "/Script/A2" / null
            if (actorPkg == null) continue;                              // BP-class / unresolved -> skip
            var actorCls = cls;
            // Root component = a loaded export whose Outer is this actor (names are unique within the level).
            var rootComp = srcExports.FirstOrDefault(c => c.Outer?.Name.Text == e.Name && c.ExportType.EndsWith("Component"));
            if (rootComp == null) continue;
            var compPkg = ScriptPackageOf(rootComp.Class);
            if (compPkg == null) continue;
            var compCls = rootComp.ExportType;
            // Still record the real base (helps stub generation for refs elsewhere)...
            onGameClass?.Invoke(actorPkg, actorCls, "AActor");
            onGameClass?.Invoke(compPkg, compCls, "USceneComponent");
            // ...but for PLACEMENT, substitute a guaranteed-loaded engine class for any game-native actor/component.
            // Placing a /Script/Pavlov actor requires its stub module to be COMPILED; if the user hasn't rebuilt the
            // C++ project the class is unresolved and the editor crashes spawning it. A StaticMeshActor placeholder
            // (keeping transform + mesh + original name as label) spawns unconditionally — no recompile needed.
            if (actorPkg != "/Script/Engine") { actorPkg = "/Script/Engine"; actorCls = "StaticMeshActor"; compPkg = "/Script/Engine"; compCls = "StaticMeshComponent"; }
            else if (compPkg != "/Script/Engine") { compPkg = "/Script/Engine"; compCls = "SceneComponent"; }
            try
            {
                var label = e.Name;
                var loc = ReadVec(rootComp, "RelativeLocation", 0);
                var rot = ReadVec(rootComp, "RelativeRotation", 0);
                var scl = ReadVec(rootComp, "RelativeScale3D", 1);
                string? meshPkg = null, meshName = null;
                if (compCls == "StaticMeshComponent")
                {
                    // Resolve the StaticMesh objref via the loaded property's ResolvedObject (works for Zen + legacy).
                    var mi = rootComp.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>("StaticMesh")?.ResolvedObject;
                    if (mi != null) { meshName = mi.Name.Text; meshPkg = PackagePathOfResolved(mi); }
                }
                place.Add((actorPkg, actorCls, compPkg, compCls, rootComp.Name, label, loc, rot, scl, meshPkg, meshName, rootComp));
            }
            catch { /* skip actors whose component fails to parse (missing imports) */ }
        }
        Log.Information("Cooked actors to place: {N} -> {L}", place.Count, string.Join(", ", place.Select(p => $"{p.label}({p.actorClass})")));

        // Gather streaming sublevels from the source UWorld so the reskinned persistent map preserves its world
        // composition. UWorld.StreamingLevels (native tail, after tagged-None) -> ULevelStreaming exports, each with a
        // WorldAsset soft path to a sublevel .umap. We re-emit them as LevelStreamingAlwaysLoaded so the sublevels both
        // register (Levels window) and load/show — volume-driven (Dynamic) wouldn't auto-load without the volume.
        var streamingAssets = new List<string>();
        var srcWorld = srcExports.OfType<CUE4Parse.UE4.Objects.Engine.UWorld>().FirstOrDefault();
        if (srcWorld?.StreamingLevels != null)
        {
            foreach (var si in srcWorld.StreamingLevels)
            {
                try
                {
                    if (si == null || si.Index <= 0) continue;
                    var sl = srcExports.ElementAtOrDefault(si.Index - 1);
                    if (sl == null) continue;
                    var wa = sl.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FSoftObjectPath>("WorldAsset").ToString();
                    if (!string.IsNullOrEmpty(wa) && wa != "None") streamingAssets.Add(wa);
                }
                catch { /* skip a streaming entry that fails to resolve */ }
            }
            if (streamingAssets.Count > 0)
                Log.Information("Streaming sublevels to preserve: {N} -> {L}", streamingAssets.Count, string.Join(", ", streamingAssets));
        }
        // NOTE: do NOT bail on place.Count == 0 — we must still emit a valid (empty) reskinned template map.
        // Bailing leaves a stale/old .umap in place (e.g. a prior UncookedPackageWriter dump with an unresolvable
        // cooked import table), which crashes the editor/content-browser with a 0x8 null-deref on scan/open.
        if (place.Count == 0) Log.Information("no placeable actors for {T}; writing empty map", targetShort);

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

        var spw = new SynthPackageWriter(EGame.GAME_UE4_21, targetPackagePath);
        spw.PackageFlags = (uint)tpkg.Summary.PackageFlags;
        spw.CustomVersionsOverride = tpkg.Summary.CustomVersionContainer?.Versions?.Select(v => (v.Key, v.Version)).ToList();
        foreach (var n in tpkg.NameMap) spw.AddRawName(rename.TryGetValue(n.Name ?? "", out var rn) ? rn : (n.Name ?? "None"));

        // Plan new export indices: per actor -> [component, actor].
        int baseExport = tpkg.ExportMap.Length;
        var newActorPkgs = new List<int>();
        for (int i = 0; i < place.Count; i++) newActorPkgs.Add(baseExport + i * 2 + 2);  // actor is 2nd of each pair
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
        for (int i = 0; i < tpkg.ExportMap.Length; i++)
        {
            var e = tpkg.ExportMap[i];
            var payload = new byte[(int)e.SerialSize];
            Array.Copy(data, (int)e.SerialOffset, payload, 0, payload.Length);
            if (i == lvlExport) payload = PatchLevelActors(payload, lvlPostNone, newActorPkgs);
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
            c = spw.AddImport("/Script/CoreUObject", "Class", pImp, cls); classImpCache[key] = c; return c;
        }

        // Append synthesized component+actor pairs.
        for (int i = 0; i < place.Count; i++)
        {
            var a = place[i];
            int compPkg = baseExport + i * 2 + 1, actorPkg = baseExport + i * 2 + 2;
            // component
            int meshObjImp = 0;
            if (a.meshPkg != null && a.meshName != null)
            {
                // package import for the mesh asset path, then the StaticMesh object import under it
                if (!pkgImpCache.TryGetValue(a.meshPkg, out var mp)) { mp = spw.AddImport("/Script/CoreUObject", "Package", 0, a.meshPkg); pkgImpCache[a.meshPkg] = mp; }
                meshObjImp = spw.AddImport("/Script/Engine", "StaticMesh", mp, a.meshName);
            }
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name);
                if (meshObjImp != 0) t.Object("StaticMesh", meshObjImp);
                WriteLightComponentProperties(t, a.sourceComp, a.compClass);
                // Mark Movable so the editor never bakes static lighting for these synthesized components —
                // Lightmass derefs null on placed lights/meshes that lack full bake data (Build Lighting crash).
                t.ByteEnum("Mobility", "EComponentMobility::Type", "EComponentMobility::Movable");
                t.Struct("RelativeLocation", "Vector", () => { w.Write(a.loc[0]); w.Write(a.loc[1]); w.Write(a.loc[2]); });
                if (a.rot[0] != 0 || a.rot[1] != 0 || a.rot[2] != 0) t.Struct("RelativeRotation", "Rotator", () => { w.Write(a.rot[0]); w.Write(a.rot[1]); w.Write(a.rot[2]); });
                if (a.scale[0] != 1 || a.scale[1] != 1 || a.scale[2] != 1) t.Struct("RelativeScale3D", "Vector", () => { w.Write(a.scale[0]); w.Write(a.scale[1]); w.Write(a.scale[2]); });
                t.WriteNone(); w.Write(0);                              // UActorComponent: UCSModifiedProperties count
                if (a.compClass == "StaticMeshComponent") w.Write(0);   // UStaticMeshComponent: extra native int32 (LODData)
                w.Flush();
                spw.AddExportRaw(spw.Name(a.compName), 0, ClassImp(a.compPkg, a.compClass), 0, 0, actorPkg, ms.ToArray(), 0x1, false);
            }
            // actor (RootComponent -> component, ActorLabel)
            using (var ms = new MemoryStream()) { using var w = new FArchiveWriter(ms);
                var t = new TaggedPropertyWriter(w, spw.Name);
                t.Object("RootComponent", compPkg);
                t.Str("ActorLabel", a.label);
                t.WriteNone(); w.Write(0); w.Flush();
                spw.AddExportRaw(spw.Name(a.label), 0, ClassImp(a.actorPkg, a.actorClass), 0, 0, lvlPkg, ms.ToArray(), 0x1 | 0x4, false);
            }
        }

        // Append ULevelStreaming exports (outer = World) for each preserved sublevel. Emitted as
        // LevelStreamingAlwaysLoaded so the sublevel registers AND loads (the World tail now references these).
        for (int i = 0; i < streamingAssets.Count; i++)
        {
            using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
            var t = new TaggedPropertyWriter(w, spw.Name);
            t.SoftObject("WorldAsset", streamingAssets[i]);
            t.WriteNone(); w.Write(0); w.Flush(); // UObject: bSerializeGuid = false
            spw.AddExportRaw(spw.Name("LevelStreamingAlwaysLoaded_" + i), 0, ClassImp("/Script/Engine", "LevelStreamingAlwaysLoaded"),
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

    /// <summary>Append actor FPackageIndices to ULevel.Actors: [int32 0][int32 count][count refs][URL...].</summary>
    private static byte[] PatchLevelActors(byte[] p, int postNone, IReadOnlyList<int> newActors)
    {
        int countOff = postNone + 4;
        int count = BitConverter.ToInt32(p, countOff);
        int insertAt = countOff + 4 + count * 4;
        var add = new byte[newActors.Count * 4];
        for (int i = 0; i < newActors.Count; i++) BitConverter.GetBytes(newActors[i]).CopyTo(add, i * 4);
        var outp = new byte[p.Length + add.Length];
        Array.Copy(p, 0, outp, 0, insertAt);
        add.CopyTo(outp, insertAt);
        Array.Copy(p, insertAt, outp, insertAt + add.Length, p.Length - insertAt);
        BitConverter.GetBytes(count + newActors.Count).CopyTo(outp, countOff);
        Log.Information("Patched ULevel.Actors: {A} -> {B}", count, count + newActors.Count);
        return outp;
    }

    /// <summary>Rewrite UWorld's native StreamingLevels list so the persistent map references our synthesized
    /// ULevelStreaming exports. UWorld native tail (after tagged-None, per CUE4Parse UWorld.Deserialize):
    /// PersistentLevel (FPackageIndex) + ExtraReferencedObjects (TArray&lt;FPackageIndex&gt;) + StreamingLevels
    /// (TArray&lt;FPackageIndex&gt;). We replace only the StreamingLevels array, preserving everything around it.</summary>
    private static byte[] PatchWorldStreamingLevels(byte[] p, int noneIdx, IReadOnlyList<int> streamingPkgs)
    {
        int pos = NodePayloadWalker.SkipTaggedProperties(p, 0, noneIdx);   // right after the None tag
        pos += 4;                                                          // leading field (same 4-byte lead PatchLevelActors uses)
        pos += 4;                                                          // PersistentLevel FPackageIndex
        if (pos + 4 > p.Length) { Log.Warning("World tail parse OOB at ExtraRef; skipping streaming patch"); return p; }
        int extraCount = BitConverter.ToInt32(p, pos);
        pos += 4 + extraCount * 4;   // ExtraReferencedObjects[]
        if (extraCount < 0 || pos + 4 > p.Length) { Log.Warning("World tail parse OOB at StreamingLevels (extra={E}); skipping streaming patch", extraCount); return p; }
        int slCountOff = pos;
        int oldSl = BitConverter.ToInt32(p, pos); int afterSl = pos + 4 + oldSl * 4;  // old StreamingLevels[]
        if (oldSl < 0 || afterSl > p.Length) { Log.Warning("World tail parse OOB (oldSl={S}); skipping streaming patch", oldSl); return p; }
        using var ms = new MemoryStream();
        ms.Write(p, 0, slCountOff);                                        // up to (not incl) StreamingLevels count
        ms.Write(BitConverter.GetBytes(streamingPkgs.Count), 0, 4);
        foreach (var pk in streamingPkgs) ms.Write(BitConverter.GetBytes(pk), 0, 4);
        ms.Write(p, afterSl, p.Length - afterSl);                          // trailing native bytes after old array
        Log.Information("Patched UWorld.StreamingLevels: {Old} -> {New}", oldSl, streamingPkgs.Count);
        return ms.ToArray();
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
            if (e.ClassName == "StaticMesh" && bounds is { } meshBounds)
                payload = ReplaceExtendedBounds(payload, meshBounds, spw, CubeName);
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
        const int MaxChain = 48;                    // keep huge ubergraphs readable (and the editor responsive)
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
                var key = scriptPkg + "." + cls;
                if (classImpCache.TryGetValue(key, out var c)) return c;
                if (!pkgImpCache.TryGetValue(scriptPkg, out var pImp))
                {
                    pImp = FindPackageImport(pkg, scriptPkg);
                    if (pImp == 0) pImp = spw.AddImport("/Script/CoreUObject", "Package", 0, scriptPkg);
                    pkgImpCache[scriptPkg] = pImp;
                }
                c = spw.AddImport("/Script/CoreUObject", "Class", pImp, cls);
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
                    inner.Object("MemberParent", classImp);
                    inner.Name("MemberName", func);
                    inner.WriteNone();
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

using CUE4Parse.UE4.Assets;
using Serilog;
using UE4Decompiler.Output.Writer;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Emits decompiled <see cref="FunctionGraph"/> nodes as real editor exports
/// (<c>K2Node_Event / CustomEvent / CallFunction / VariableGet / VariableSet /
/// IfThenElse / SwitchInteger / FunctionResult / DynamicCast / MakeArray</c>)
/// into a reskinned template blueprint's <c>EventGraph</c>.
///
/// Crash-safety rules (learned from the cooked-guts path): only well-understood node classes with
/// exactly-modelled payloads are emitted; anything exotic is skipped and the consumer pin keeps its
/// literal <c>DefaultValue</c>. Every new export is appended (never reorders cooked indices) and all
/// <c>LinkedTo</c> refs are wired both directions with consistent fresh <c>PinId</c>s. Emission is
/// three passes (assign ids → collect links → build payloads) so wiring is order-independent.
/// </summary>
internal static class BlueprintNodeEmitter
{
    private const int MaxNodes = 800;

    internal sealed class Ctx
    {
        public required SynthPackageWriter Spw;
        public required Package Tpl;
        public int EgPkg;
        public int BgcPkg;
        public string TargetShort = "";
        public bool IsUe5;
        public readonly Dictionary<string, int> PkgImpCache = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ClassImpCache = new(StringComparer.Ordinal);
        public int BgPkgImp;
        public int ImpCallFunc, ImpCustomEvent, ImpEvent, ImpVarGet, ImpVarSet;
        public int ImpBranch, ImpSwitch, ImpReturn, ImpCast, ImpMakeArray;
        public int ImpParentCall;
        public int ActorClassImp;
        public readonly Dictionary<(int node, string pin, byte dir), FGuid16> PinIds = new();
        public readonly Dictionary<int, int> NodePkg = new();   // flat node idx -> FPackageIndex
        public readonly Dictionary<(int node, string pin, byte dir), List<(int pkg, FGuid16 id)>> Links = new();
        /// <summary>Template UBlueprint NewVariables: VarName -> VarGuid. The editor matches
        /// VariableGet/Set refs by name AND member guid; a zero guid reads as "variable missing".</summary>
        public readonly Dictionary<string, FGuid16> VarGuids = new(StringComparer.Ordinal);
        /// <summary>Grafted variable types (VarName -> category, subcategory, object path). Used to align
        /// VarGet/Set value pins to the declared type so reconciliation can't drop them.</summary>
        public readonly Dictionary<string, (string Cat, string Sub, string? Obj)> VarCats = new(StringComparer.Ordinal);
        /// <summary>Source BP identity (package path + generated class) for self-call routing: a call
        /// whose FuncPkg/FuncClass match points at the node being built, so its MemberParent is the
        /// local BGC instead of a dangling import of the cooked game package.</summary>
        public string SelfPkg = "";
        public string SelfClass = "";
    }

    /// <summary>One built node export, ready to append (in emission order; pkgs are dense).</summary>
    internal sealed class BuiltNode
    {
        public string K2Class = "";
        public int NameNum;
        public int ClassImp;
        public byte[] Payload = Array.Empty<byte>();
        public int Pkg;
        public uint Flags = 0x1;
        /// <summary>ScriptSerializationEndOffset: payload length through the tagged-property None
        /// (pins excluded), EXCLUDING the version preamble (caller adds it back).</summary>
        public int ScriptEnd;
    }

    /// <summary>
    /// Flatten graphs (cap <see cref="MaxNodes"/>), lay out, and build every node payload.
    /// Returns built nodes in emission order with dense <see cref="BuiltNode.Pkg"/> values starting
    /// at <paramref name="firstNewExportIdx"/>+1, so the caller can patch referrers (EventGraph.Nodes)
    /// BEFORE appending — keeping cross-references exact even when individual nodes are skipped.
    /// </summary>
    public static List<BuiltNode> Build(SynthPackageWriter spw, Package tpl, int egExportIdx, string targetShort,
        IReadOnlyList<FunctionGraph> graphs, int firstNewExportIdx, bool isUe5, byte[] preamble,
        IReadOnlyDictionary<string, FGuid16>? varGuids = null, string selfPkg = "", string selfClass = "",
        string parentClassPath = "", IReadOnlyDictionary<string, (string Cat, string Sub, string? Obj)>? varCats = null)
    {
        var flat = new List<(FunctionGraph g, GraphNode n)>();
        foreach (var g in graphs)
            foreach (var n in g.Nodes)
            {
                if (flat.Count >= MaxNodes) break;
                flat.Add((g, n));
            }
        if (flat.Count == 0) return new List<BuiltNode>();

        var ctx = new Ctx { Spw = spw, Tpl = tpl, EgPkg = egExportIdx + 1, TargetShort = targetShort, IsUe5 = isUe5 };
        if (varGuids != null) foreach (var kv in varGuids) ctx.VarGuids[kv.Key] = kv.Value;
        if (varCats != null) foreach (var kv in varCats) ctx.VarCats[kv.Key] = kv.Value;
        ctx.SelfPkg = selfPkg ?? ""; ctx.SelfClass = selfClass ?? "";
        EnsureImports(ctx, flat);
        // The generated-class export the template's own event/delegate member refs point at (ground truth:
        // a CustomEvent's OutputDelegate references (BGC, EventName, node guid)). 0 when absent.
        for (int ei = 0; ei < tpl.ExportMap.Length; ei++)
            if (tpl.ExportMap[ei].ClassName.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal))
            { ctx.BgcPkg = ei + 1; break; }

        // Which flat nodes survive (emitter supports their class)? Computed first so export
        // indices stay dense and no surviving pin links a skipped export.
        var willEmit = new bool[flat.Count];
        for (int i = 0; i < flat.Count; i++)
            willEmit[i] = ClassImpFor(flat[i].n.K2Class, ctx) != 0;

        // Passes 1-3 run inside BuildAttempt so a per-node build failure deterministically
        // re-runs with that node excluded — keeping pkgs dense and every LinkedTo exact.
        // (BuildNode only throws on internal error; willEmit already filtered classes.)
        var skip = new HashSet<int>();
        while (true)
        {
            var attempt = BuildAttempt(spw, tpl, ctx, flat, graphs, firstNewExportIdx, skip, preamble, parentClassPath);
            if (attempt.Failed.Count == 0) return FinishBuild(targetShort, flat, attempt.Built);
            foreach (var f in attempt.Failed) skip.Add(f);
            if (skip.Count > flat.Count / 2)
                return FinishBuild(targetShort, flat, attempt.Built); // pathological: keep survivors
        }
    }

    private sealed class Attempt
    {
        public readonly List<BuiltNode> Built = new();
        public readonly List<int> Failed = new();
    }

    private static Attempt BuildAttempt(SynthPackageWriter spw, Package tpl, Ctx ctx,
        List<(FunctionGraph g, GraphNode n)> flat, IReadOnlyList<FunctionGraph> graphs,
        int firstNewExportIdx, HashSet<int> skip, byte[] preamble, string parentClassPath = "")
    {
        var attempt = new Attempt();
        ctx.NodePkg.Clear(); ctx.PinIds.Clear(); ctx.Links.Clear();

        var willEmit = new bool[flat.Count];
        for (int i = 0; i < flat.Count; i++)
            willEmit[i] = !skip.Contains(i) && ClassImpFor(flat[i].n.K2Class, ctx) != 0;

        // Reference validation: drop nodes whose function/variable/event references cannot resolve.
        // Unresolvable refs make the editor rebuild pins wrong ("In use pin no longer exists") or fail
        // the compile outright ("Could not find function/variable"). Dropping is safe: Pass 2 re-derives
        // every wire from survivors, so dangling exec/data pins simply stay unconnected (legal).
        //
        // Event survivors: only functions that will REALLY exist post-emit can be called. That's
        // CustomEvent entries and override Event entries (first-seen per name, never the Construction
        // Script which collides with the template's own). UFunction graphs emit no UFunction export,
        // so calls to them can never resolve ("Could not find function").
        var survivorPins = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var selfFuncs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int gi = 0; gi < graphs.Count; gi++)
        {
            var g = graphs[gi];
            var entry = g.Nodes.FirstOrDefault(n => n.Id == g.EntryNode);
            if (entry is null) continue;
            if (entry.K2Class is not ("K2Node_CustomEvent" or "K2Node_Event")) continue;
            // Skip-aware: an entry dropped by the retry loop (build failure) defines no function —
            // calls to it would dangle ("Could not find function" + pin errors). Exclude it so the
            // same validation pass drops its callers too.
            int eflat = -1;
            for (int i = 0; i < flat.Count; i++)
                if (ReferenceEquals(flat[i].g, g) && ReferenceEquals(flat[i].n, entry)) { eflat = i; break; }
            if (eflat < 0 || skip.Contains(eflat)) continue;
            var ename = entry.EventName ?? "";
            if (string.IsNullOrWhiteSpace(ename)) continue;
            if (ename.Equals("UserConstructionScript", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.K2Class == "K2Node_Event" && !KismetGraphDecompiler.OverrideEvents.Contains(ename)) continue;
            if (!survivorPins.TryGetValue(ename, out var pins))
            {
                pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in entry.Inputs.Concat(entry.Outputs))
                {
                    if (p.Category == "exec" || p.Category == "delegate") continue;
                    if (p.Name is "self" or "execute" or "then" or "OutputDelegate") continue;
                    pins.Add(p.Name);
                }
                survivorPins[ename] = pins;
            }
            selfFuncs.Add(ename);
            if (!string.IsNullOrWhiteSpace(g.FunctionName)) selfFuncs.Add(g.FunctionName);
        }
        // Entry user-pin sweep: CustomEvent/Event entry user pins don't survive serialization (the
        // created function lacks params), so downstream links to them dangle ("In use pin X no longer exists
        // on node <Event>") and the pins themselves mismatch ("doesn't match any parameters"). Remove consumer
        // links targeting entry user pins AND the pins themselves (unlinked pins are legal; functions become
        // param-less, matching what the compiler actually creates). Standard pins (exec/delegate/fixed event
        // pins) are untouched — only IsUserPin ones.
        foreach (var g in graphs)
        {
            var entry = g.Nodes.FirstOrDefault(n => n.Id == g.EntryNode);
            if (entry is null) continue;
            if (entry.K2Class is not ("K2Node_CustomEvent" or "K2Node_Event")) continue;
            var userPins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in entry.Outputs) if (p.IsUserPin) userPins.Add(p.Name);
            if (userPins.Count == 0) continue;
            foreach (var m in g.Nodes)
            {
                if (ReferenceEquals(m, entry)) continue;
                var dead = m.DataLinks
                    .Where(kv => kv.Value.Node == entry.Id && userPins.Contains(kv.Value.Pin))
                    .Select(kv => kv.Key).ToList();
                foreach (var k in dead) m.DataLinks.Remove(k);
            }
            entry.Outputs.RemoveAll(p => p.IsUserPin && userPins.Contains(p.Name));
        }
        for (int i = 0; i < flat.Count; i++)
        {
            if (!willEmit[i]) continue;
            SanitizePins(flat[i].n, ctx, survivorPins);
        }
        // Link type-compatibility: drop wires whose ends can never connect ("Can't connect pins ...
        // not compatible"). Only clear-cut category mismatches (scalar vs object/class/struct, differing
        // structs); polymorphism (object-object), numerics, and wildcards are left for the reconciler.
        for (int i = 0; i < flat.Count; i++)
        {
            if (!willEmit[i]) continue;
            var n = flat[i].n;
            var dropKeys = new List<string>();
            foreach (var kv in n.DataLinks)
            {
                var cpin = n.Inputs.FirstOrDefault(p => p.Name == kv.Key);
                if (cpin is null) continue;
                int src = ToFlat(flat, i, kv.Value.Node);
                if (src < 0 || src >= flat.Count || !willEmit[src]) continue;
                var spin = flat[src].n.Outputs.FirstOrDefault(p => p.Name == kv.Value.Pin);
                if (spin is null) continue;
                if (!PinsCompatible(cpin, spin)) dropKeys.Add(kv.Key);
            }
            foreach (var k in dropKeys) n.DataLinks.Remove(k);
        }
        int nPrunedRefs = 0;
        bool logVar = Environment.GetEnvironmentVariable("UE4D_LOGVAR") == "1";
        for (int i = 0; i < flat.Count; i++)
        {
            if (!willEmit[i]) continue;
            if (!NodeRefsValid(flat[i].n, ctx, selfFuncs, survivorPins, parentClassPath))
            {
                willEmit[i] = false;
                nPrunedRefs++;
                if (logVar && flat[i].n.K2Class is "K2Node_VariableGet" or "K2Node_VariableSet")
                    Log.Information("PRUNEVAR {C} {N} (declared={D})", flat[i].n.K2Class, flat[i].n.VarName,
                        ctx.VarGuids.ContainsKey(flat[i].n.VarName ?? ""));
            }
            else if (logVar && flat[i].n.K2Class is "K2Node_VariableGet" or "K2Node_VariableSet")
            {
                var vn = flat[i].n;
                var pinStr = "in=[" + string.Join(",", vn.Inputs.Select(p => p.Name + ":" + p.Category + ":" + p.Direction)) + "] out=["
                    + string.Join(",", vn.Outputs.Select(p => p.Name + ":" + p.Category + ":" + p.Direction)) + "] links=["
                    + string.Join(",", vn.DataLinks.Select(kv => kv.Key + "->" + kv.Value.Node + "." + kv.Value.Pin)) + "]";
                Log.Information("KEEPVAR {C} {N} {P}", vn.K2Class, vn.VarName, pinStr);
            }
        }
        if (nPrunedRefs > 0)
            Log.Information("Pruned {N} unresolvable-ref nodes for {Bp}", nPrunedRefs, ctx.TargetShort);

        // Pass 1: layout + dense export indices + pin ids.
        int bandY = 0, lastGi = -1, col = 0, nextExp = firstNewExportIdx;
        for (int i = 0; i < flat.Count; i++)
        {
            var (g, n) = flat[i];
            int thisGi = IndexOfGraph(graphs, g);
            if (thisGi != lastGi) { lastGi = thisGi; bandY = thisGi * 900; col = 0; }
            n.PosX = 360 + (col % 12) * 300;
            n.PosY = bandY + 48 + (col / 12) * 170;
            col++;
            if (!willEmit[i]) continue;
            nextExp++;
            ctx.NodePkg[i] = nextExp;
            foreach (var p in n.Inputs) ctx.PinIds[(i, p.Name, (byte)0)] = FGuid16.NewGuid();
            foreach (var p in n.Outputs) ctx.PinIds[(i, p.Name, (byte)1)] = FGuid16.NewGuid();
        }

        // Pass 2: collect every wire (both directions) into the link map.
        for (int i = 0; i < flat.Count; i++)
            if (willEmit[i]) CollectLinks(ctx, flat, willEmit, i);

        // Pass 2b: prune isolated variable nodes (e.g. ubergraph EntryPoint params, orphaned temps):
        // a VarGet/Set with no links in either direction can never bind — fail it so the retry
        // loop re-runs denser without it instead of emitting a "variable missing" red node.
        for (int i = 0; i < flat.Count; i++)
        {
            if (!willEmit[i]) continue;
            var k2 = flat[i].n.K2Class;
            if (k2 is not ("K2Node_VariableGet" or "K2Node_VariableSet")) continue;
            bool linked = false;
            foreach (var key in ctx.Links.Keys)
                if (key.node == i) { linked = true; break; }
            if (!linked) attempt.Failed.Add(i);
        }
        if (attempt.Failed.Count > 0) return attempt;

        // Pass 3: build payloads.
        for (int i = 0; i < flat.Count; i++)
        {
            if (!willEmit[i]) continue;
            var n = flat[i].n;
            try
            {
                int classImp = ClassImpFor(n.K2Class, ctx);
                var (payload, scriptEnd) = BuildNode(ctx, i, n);
                // Mirror the template's version preamble (UE5: 1 byte; 4.21: none).
                if (preamble.Length > 0) payload = preamble.Concat(payload).ToArray();
                // Object flags mirror editor-saved K2 nodes (RF_Transactional), like the template's own.
                attempt.Built.Add(new BuiltNode { K2Class = n.K2Class, NameNum = 2000 + i, ClassImp = classImp, Payload = payload, Pkg = ctx.NodePkg[i], ScriptEnd = scriptEnd, Flags = 0x8 });
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Node emit skipped ({K2} {Ref})", n.K2Class, n.FunctionRef ?? n.VarName ?? n.EventName);
                attempt.Failed.Add(i);
            }
        }
        return attempt;
    }

    private static List<BuiltNode> FinishBuild(string targetShort, List<(FunctionGraph g, GraphNode n)> flat, List<BuiltNode> built)
    {
        Log.Information("Built {N}/{T} recovered graph nodes for {Bp}", built.Count, flat.Count, targetShort);
        return built;
    }

    private static int IndexOfGraph(IReadOnlyList<FunctionGraph> graphs, FunctionGraph g)
    {
        for (int i = 0; i < graphs.Count; i++)
            if (ReferenceEquals(graphs[i], g)) return i;
        return 0;
    }

    private static void AddLink(Ctx ctx, int node, string pin, byte dir, int peerPkg, FGuid16 peerId)
    {
        var key = (node, pin, dir);
        if (!ctx.Links.TryGetValue(key, out var list)) ctx.Links[key] = list = new();
        list.Add((peerPkg, peerId));
    }

    /// <summary>Decompiler-local node ids are per-function; map to flat indices.</summary>
    private static int ToFlat(List<(FunctionGraph g, GraphNode n)> flat, int consumerFlat, int local)
    {
        var g = flat[consumerFlat].g;
        int baseIdx = -1;
        for (int i = 0; i < flat.Count; i++)
            if (ReferenceEquals(flat[i].g, g)) { baseIdx = i; break; }
        if (baseIdx < 0) return -1;
        int target = baseIdx + local;
        if (target >= 0 && target < flat.Count && ReferenceEquals(flat[target].g, g))
            return target;
        return -1;
    }

    private static void CollectLinks(Ctx ctx, List<(FunctionGraph g, GraphNode n)> flat, bool[] willEmit, int i)
    {
        var n = flat[i].n;
        // Exec: k-th ExecNext target links the k-th exec output.
        var execOuts = n.Outputs.Where(p => p.Category == "exec").ToList();
        for (int k = 0; k < n.ExecNext.Count && k < execOuts.Count; k++)
        {
            int target = ToFlat(flat, i, n.ExecNext[k]);
            if (target < 0 || target >= flat.Count || !willEmit[target]) continue;
            var targetNode = flat[target].n;
            var targetIn = targetNode.Inputs.FirstOrDefault(p => p.Category == "exec" && p.Direction == 0);
            if (targetIn is null) continue;
            AddLink(ctx, i, execOuts[k].Name, 1, ctx.NodePkg[target], ctx.PinIds[(target, targetIn.Name, (byte)0)]);
            AddLink(ctx, target, targetIn.Name, 0, ctx.NodePkg[i], ctx.PinIds[(i, execOuts[k].Name, (byte)1)]);
        }
        // Data: consumer input <-> producer output.
        foreach (var kv in n.DataLinks)
        {
            if (!n.Inputs.Any(p => p.Name == kv.Key)) continue;
            int src = ToFlat(flat, i, kv.Value.Node);
            if (src < 0 || src >= flat.Count || !willEmit[src]) continue;
            var srcNode = flat[src].n;
            var srcOut = srcNode.Outputs.FirstOrDefault(p => p.Name == kv.Value.Pin)
                ?? srcNode.Outputs.FirstOrDefault(p => p.Category != "exec");
            if (srcOut is null) continue;
            AddLink(ctx, i, kv.Key, 0, ctx.NodePkg[src], ctx.PinIds[(src, srcOut.Name, (byte)1)]);
            AddLink(ctx, src, srcOut.Name, 1, ctx.NodePkg[i], ctx.PinIds[(i, kv.Key, (byte)0)]);
        }
    }

    private static bool IsSelfCall(GraphNode n, Ctx ctx)
        => (ctx.BgcPkg != 0 && !string.IsNullOrEmpty(ctx.SelfPkg)
                && string.Equals(n.FuncPkg, ctx.SelfPkg, StringComparison.Ordinal)
                && string.Equals(n.FuncClass, ctx.SelfClass, StringComparison.Ordinal))
            || (string.IsNullOrWhiteSpace(n.FuncPkg) && string.IsNullOrWhiteSpace(n.FuncClass));

    private static bool IsSynthArgPin(string name)
        => name == "<Unnamed>" || string.IsNullOrEmpty(name)
            || BlueprintGraphBuilder.IsBogusMemberName(name)
            || (name.Length > 3 && name.StartsWith("Arg", StringComparison.Ordinal)
                && name[3..].All(char.IsDigit));

    /// <summary>Drop pins that can never match a live signature (see above). Runs before validation
    /// and link collection, so pruned pins leave no wires behind. Structural pins (exec/self/then)
    /// are never touched. Dropping a call's unmatched data pins is safe: the callee still compiles
    /// with defaults; dangling downstream links simply stay unconnected (legal).</summary>
    private static void SanitizePins(GraphNode n, Ctx ctx, Dictionary<string, HashSet<string>> survivorPins)
    {
        if (n.K2Class is "K2Node_VariableGet" or "K2Node_VariableSet")
        {
            // Normalize the value pin to VarName FIRST: cooked pin names can carry spaces/node-titles
            // ("KNNode Dynamic Cast as BP VRPawn") while the grafted member uses the sanitized VarName.
            // A mismatched pin never binds ("no longer exists" errors) even though the variable exists.
            if (!string.IsNullOrWhiteSpace(n.VarName))
            {
                bool isGet = n.K2Class == "K2Node_VariableGet";
                // Only rename when no correctly-named value pin exists (else we'd duplicate it).
                bool hasGood = isGet
                    ? n.Outputs.Any(p => p.Name == n.VarName)
                    : n.Inputs.Any(p => p.Name == n.VarName);
                if (!hasGood)
                {
                    GraphPin? vpin = isGet
                        ? n.Outputs.FirstOrDefault(p => p.Category != "exec")
                        : n.Inputs.FirstOrDefault(p => p.Category != "exec" && p.Name != "self" && p.Direction == 0);
                    if (vpin != null && vpin.Name != n.VarName)
                    {
                        var old = vpin.Name;
                        vpin.Name = n.VarName;
                        if (n.DataLinks.TryGetValue(old, out var dl)) { n.DataLinks.Remove(old); n.DataLinks[n.VarName] = dl; }
                    }
                }
            }
            var keepIn = n.K2Class == "K2Node_VariableGet"
                ? new HashSet<string>(StringComparer.Ordinal) { "self" }
                : new HashSet<string>(StringComparer.Ordinal) { "execute", "self", n.VarName ?? "Var" };
            var keepOut = n.K2Class == "K2Node_VariableGet"
                ? new HashSet<string>(StringComparer.Ordinal) { n.VarName ?? "Var" }
                : new HashSet<string>(StringComparer.Ordinal) { "then" };
            n.Inputs.RemoveAll(p => !keepIn.Contains(p.Name));
            n.Outputs.RemoveAll(p => !keepOut.Contains(p.Name));
            var dead = n.DataLinks.Keys.Where(k => !keepIn.Contains(k)).ToList();
            foreach (var k in dead) n.DataLinks.Remove(k);
            // Align the value pin to the grafted declaration (when known and concrete): a pin whose
            // category disagrees with the declared variable type is dropped by the reconciler even
            // though the variable exists. Overwriting is safe — it's exactly what the editor expects.
            if (!string.IsNullOrWhiteSpace(n.VarName) && ctx.VarCats.TryGetValue(n.VarName, out var vt)
                && vt.Cat != "wildcard" && (vt.Cat != "object" || vt.Obj != null))
            {
                var vpin = n.K2Class == "K2Node_VariableGet"
                    ? n.Outputs.FirstOrDefault(p => p.Name == n.VarName)
                    : n.Inputs.FirstOrDefault(p => p.Name == n.VarName);
                if (vpin != null && vpin.Category != vt.Cat)
                {
                    vpin.Category = vt.Cat;
                    vpin.SubCategory = vt.Sub ?? "None";
                    vpin.SubCategoryObjPath = vt.Obj;
                }
            }
        }
        else if (n.K2Class is "K2Node_CallFunction" or "K2Node_CallParentFunction")
        {
            bool self = IsSelfCall(n, ctx);
            // Custom self-calls: entry user pins don't survive (stripped above), so the created function
            // has no params — any data on the call dangles. Gut to exec+self skeleton (fires correctly
            // with defaults). Engine calls keep real-named pins (live signatures restore them).
            if (self && !string.IsNullOrWhiteSpace(n.FuncName) && survivorPins.ContainsKey(n.FuncName))
            {
                var gutKeys = n.DataLinks.Keys.Where(k => k != "self").ToList();
                foreach (var k in gutKeys) n.DataLinks.Remove(k);
                n.Inputs.RemoveAll(p => p.Category != "exec" && p.Name != "self");
                n.Outputs.RemoveAll(p => p.Category != "exec");
                return;
            }
            HashSet<string>? allowed = null;
            if (self && !string.IsNullOrWhiteSpace(n.FuncName) && survivorPins.TryGetValue(n.FuncName, out var ap))
                allowed = ap;
            // Drop data inputs that can't bind: cooked Arg/<Unnamed> on foreign calls, anything outside
            // the callee's declared interface on self calls, and wildcard-typed pins (unbindable).
            // Exec + self are structural and always kept.
            var deadKeys = new List<string>();
            foreach (var k in n.DataLinks.Keys)
            {
                if (k == "self") continue;
                if (allowed != null) { if (BlueprintGraphBuilder.IsBogusMemberName(k) || !allowed.Contains(k)) deadKeys.Add(k); continue; }
                if (IsSynthArgPin(k)) { deadKeys.Add(k); continue; }
                var pin = n.Inputs.FirstOrDefault(p => p.Name == k);
                if (pin != null && pin.Category == "wildcard") deadKeys.Add(k);
            }
            foreach (var k in deadKeys) n.DataLinks.Remove(k);
            // Remove the pruned input pins themselves (exec/self structural pins are never touched).
            // Unlinked pins are left alone: without a wire they can't produce "no longer exists" errors.
            n.Inputs.RemoveAll(p => p.Category != "exec" && p.Name != "self" && deadKeys.Contains(p.Name));
            // Outputs: only strip a wildcard when it's the SOLE data output (no fallback target exists,
            // so no miswiring risk). Otherwise leave them; the reconciler rebinds typed ones.
            var dataOuts = n.Outputs.Where(p => p.Category != "exec").ToList();
            if (dataOuts.Count == 1 && dataOuts[0].Category == "wildcard")
            {
                n.Outputs.Remove(dataOuts[0]);
            }
        }
    }

    private static bool IsNumericPinCat(string c)
        => c is "int" or "float" or "real" or "double" or "byte";
    private static bool IsTextPinCat(string c)
        => c is "string" or "name" or "text";

    /// <summary>Conservative wire compatibility: only obvious mismatches return false (scalar vs
    /// object/class/struct, differing structs). Same-category (incl. object-object polymorphism),
    /// numerics, text, exec, and wildcards are left for the reconciler.</summary>
    private static bool PinsCompatible(GraphPin consumer, GraphPin producer)
    {
        var cc = consumer.Category;
        var sc = producer.Category;
        if (cc == "wildcard" || sc == "wildcard" || cc == "exec" || sc == "exec") return true;
        if (cc == sc)
        {
            if (cc == "struct" || cc == "class" || cc == "softclass")
            {
                var co = consumer.SubCategoryObjPath;
                var so = producer.SubCategoryObjPath;
                if (!string.IsNullOrEmpty(co) && !string.IsNullOrEmpty(so)
                    && !string.Equals(co, so, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        if (IsNumericPinCat(cc) && IsNumericPinCat(sc)) return true;
        if (IsTextPinCat(cc) && IsTextPinCat(sc)) return true;
        if (cc == "enum" && sc == "enum") return true;
        if ((cc == "enum" && sc == "byte") || (cc == "byte" && sc == "enum")) return true;
        return false;
    }

    /// <summary>Stock engine script modules whose API exists in any stock editor (safe call targets).
    /// Game modules (/Script/Ax*, project BPs under /Game) have partial or stub members here — calling
    /// them produces "Could not find function" + pin-mismatch compile errors.</summary>
    internal static bool IsStockEnginePackage(string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg) || !pkg.StartsWith("/Script/", StringComparison.Ordinal)) return false;
        var mod = pkg.Substring("/Script/".Length);
        int cut = mod.IndexOfAny(new[] { '.', '/' });
        if (cut >= 0) mod = mod[..cut];
        return mod is "Engine" or "CoreUObject" or "UMG" or "Slate" or "SlateCore" or "InputCore"
            or "EnhancedInput" or "PhysicsCore" or "Chaos" or "ChaosSolverEngine" or "Niagara"
            or "GameplayTags" or "AIModule" or "NavigationSystem" or "MovieScene" or "LevelSequence"
            or "AudioMixer" or "MediaAssets" or "EngineSettings" or "DeveloperSettings";
    }

    /// <summary>Classes whose member functions accept implicit-self as target: the actor hierarchy
    /// (self IS an actor) and static libraries (no target needed). Anything else (components handled
    /// above, widgets, MID, ...) errors "self is not compatible" without a wired target.</summary>
    private static bool IsSelfCallableClass(string? cls)
    {
        if (string.IsNullOrWhiteSpace(cls)) return true;
        if (cls.EndsWith("Library", StringComparison.Ordinal)
            || cls.EndsWith("Statics", StringComparison.Ordinal)
            || cls.EndsWith("Utilities", StringComparison.Ordinal)
            || cls.EndsWith("Helpers", StringComparison.Ordinal)) return true;
        return cls is "Actor" or "Pawn" or "Character" or "Controller" or "PlayerController"
            or "AIController" or "GameModeBase" or "GameStateBase" or "PlayerState" or "HUD"
            or "SpectatorPawn" or "DefaultPawn";
    }

    /// <summary>True when the node's member references can resolve at compile time. Self calls must hit
    /// a reconstructed function; foreign calls must target stock engine API; variables must be declared
    /// (template or grafted); events must be real overrides and unique per graph.</summary>
    private static bool NodeRefsValid(GraphNode n, Ctx ctx, HashSet<string> selfFuncs,
        Dictionary<string, HashSet<string>> survivorPins, string parentClassPath)
    {
        switch (n.K2Class)
        {
            case "K2Node_VariableGet":
            case "K2Node_VariableSet":
                // Must bind a declared variable (template or grafted); else "variable missing" red node.
                // "Self" is implicit (never declared) and always valid.
                if (n.VarName is "Self" or "self") return true;
                return !string.IsNullOrWhiteSpace(n.VarName) && ctx.VarGuids.ContainsKey(n.VarName);
            case "K2Node_CallFunction":
            {
                if (string.IsNullOrWhiteSpace(n.FuncName)) return false;
                // Generic container libraries are structurally unrecoverable (cooked generics lose element
                // types; every pin is wildcard → "undetermined" errors). Drop them outright.
                var fc = n.FuncClass ?? "";
                if (fc.Contains("ArrayLibrary", StringComparison.Ordinal)
                    || fc.Contains("MapLibrary", StringComparison.Ordinal)
                    || fc.Contains("SetLibrary", StringComparison.Ordinal)) return false;
                if (IsSelfCall(n, ctx))
                {
                    if (!selfFuncs.Contains(n.FuncName)) return false;
                    // Custom-function data pins never restore (entry user pins don't survive serialization),
                    // so any data beyond the self target dangles. Keep pure-exec skeleton calls only: they
                    // fire events correctly with defaults. (Engine-called data pins are handled by sanitize.)
                    bool hasData = n.Inputs.Any(p => p.Category != "exec" && p.Name != "self")
                        || n.Outputs.Any(p => p.Category != "exec");
                    if (hasData) return false;
                    return true;
                }
                if (!IsStockEnginePackage(n.FuncPkg)) return false;
                // Component-member calls with no wired target implicitly target self, which is an
                // actor — "self is not compatible" errors. (Wired ones are reconciled normally.)
                if ((n.FuncClass ?? "").EndsWith("Component", StringComparison.Ordinal)
                    && !n.DataLinks.ContainsKey("self")) return false;
                // Same for impure member calls on non-actor classes (widgets, MID, ...): implicit self
                // can't satisfy them. Pure calls are exempt (defaults are legal, reconciler rebinds).
                // Static libraries never need a target. Actor-hierarchy self calls are always fine.
                if (!n.IsPure && !n.DataLinks.ContainsKey("self") && !IsSelfCallableClass(n.FuncClass)) return false;
                return true;
            }
            case "K2Node_CallParentFunction":
            {
                if (string.IsNullOrWhiteSpace(n.FuncName)) return false;
                // Stock override events always exist on the parent chain; other parent calls need an
                // engine parent (game parents have partial members here).
                if (KismetGraphDecompiler.OverrideEvents.Contains(n.FuncName)) return true;
                var pp = parentClassPath ?? "";
                return pp.StartsWith("/Script/Engine", StringComparison.Ordinal)
                    || pp.StartsWith("/Script/CoreUObject", StringComparison.Ordinal);
            }
            case "K2Node_Event":
            {
                if (string.IsNullOrWhiteSpace(n.EventName)) return false;
                // Must be a surviving event (real override, first-seen, never the Construction Script
                // which collides with the template's own). Survivors were precomputed above.
                return survivorPins.ContainsKey(n.EventName);
            }
            case "K2Node_CustomEvent":
            {
                if (string.IsNullOrWhiteSpace(n.EventName)) return false;
                return survivorPins.ContainsKey(n.EventName);
            }
            default:
                return true; // IfThenElse/Switch/Return/Cast/MakeArray: self-contained
        }
    }

    /// <summary>Legacy flat-chain entry check (same rule as graph CallFunction nodes): self calls must
    /// hit a reconstructed function; foreign calls must target stock engine API. Exec-only chain nodes
    /// can't produce pin errors, but unresolvable FunctionReferences still fail the compile.</summary>
    internal static bool ChainCallResolves(string scriptPkg, string cls, string func,
        string selfPkg, string selfClass, HashSet<string> selfFuncs)
    {
        if (string.IsNullOrWhiteSpace(func)) return false;
        bool selfCall = (!string.IsNullOrEmpty(selfPkg)
                && string.Equals(scriptPkg, selfPkg, StringComparison.Ordinal)
                && string.Equals(cls, selfClass, StringComparison.Ordinal))
            || (string.IsNullOrWhiteSpace(scriptPkg) && string.IsNullOrWhiteSpace(cls));
        if (selfCall) return selfFuncs.Contains(func);
        return IsStockEnginePackage(scriptPkg);
    }

    private static void EnsureImports(Ctx ctx, IReadOnlyList<(FunctionGraph g, GraphNode n)> flat)
    {
        var spw = ctx.Spw;
        ctx.BgPkgImp = FindPackageImport(ctx.Tpl, "/Script/BlueprintGraph");
        if (ctx.BgPkgImp == 0) ctx.BgPkgImp = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/BlueprintGraph");
        // Only import node classes actually emitted: every unused import is a load-time resolution
        // liability (a single unresolvable class import breaks LinkerLoad for the whole package).
        var used = new HashSet<string>(StringComparer.Ordinal);
        bool needActor = false;
        foreach (var (_, n) in flat)
        {
            used.Add(n.K2Class);
            if (n.K2Class == "K2Node_Event") needActor = true;
        }
        int Class(string name) => FindOrAddClass(ctx, "/Script/CoreUObject", "Class", ctx.BgPkgImp, name);
        int Maybe(string k2, string name) => used.Contains(k2) ? Class(name) : 0;
        ctx.ImpCallFunc = Maybe("K2Node_CallFunction", "K2Node_CallFunction");
        ctx.ImpCustomEvent = Maybe("K2Node_CustomEvent", "K2Node_CustomEvent");
        ctx.ImpEvent = Maybe("K2Node_Event", "K2Node_Event");
        ctx.ImpVarGet = Maybe("K2Node_VariableGet", "K2Node_VariableGet");
        ctx.ImpVarSet = Maybe("K2Node_VariableSet", "K2Node_VariableSet");
        ctx.ImpBranch = Maybe("K2Node_IfThenElse", "K2Node_IfThenElse");
        ctx.ImpSwitch = Maybe("K2Node_SwitchInteger", "K2Node_SwitchInteger");
        ctx.ImpReturn = Maybe("K2Node_FunctionResult", "K2Node_FunctionResult");
        ctx.ImpCast = Maybe("K2Node_DynamicCast", "K2Node_DynamicCast");
        ctx.ImpMakeArray = Maybe("K2Node_MakeArray", "K2Node_MakeArray");
        ctx.ImpParentCall = Maybe("K2Node_CallParentFunction", "K2Node_CallParentFunction");
        // EventReference parent for override events: engine Actor (guaranteed loaded).
        // Reuse the template's /Script/Engine package import so outer indices match template imports.
        int enginePkg = FindPackageImport(ctx.Tpl, "/Script/Engine");
        if (enginePkg == 0) enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
        ctx.ActorClassImp = needActor ? FindOrAddClass(ctx, "/Script/CoreUObject", "Class", enginePkg, "Actor") : 0;
    }

    private static int FindOrAddClass(Ctx ctx, string classPkg, string className, int outer, string obj)
    {
        int found = BlueprintGraphBuilder.FindTemplateImport(ctx.Tpl, classPkg, className, outer, obj);
        // NOTE: found is a template-space FPackageIndex; the writer re-added template imports
        // verbatim and in order, so template import #(−found−1) == writer import #(−found−1).
        if (found != 0) return found;
        return ctx.Spw.AddImport(classPkg, className, outer, obj);
    }

    private static int ClassImpFor(string k2class, Ctx ctx) => k2class switch
    {
        "K2Node_CallFunction" => ctx.ImpCallFunc,
        "K2Node_CustomEvent" => ctx.ImpCustomEvent,
        "K2Node_Event" => ctx.ImpEvent,
        "K2Node_VariableGet" => ctx.ImpVarGet,
        "K2Node_VariableSet" => ctx.ImpVarSet,
        "K2Node_IfThenElse" => ctx.ImpBranch,
        "K2Node_SwitchInteger" => ctx.ImpSwitch,
        "K2Node_FunctionResult" => ctx.ImpReturn,
        "K2Node_DynamicCast" => ctx.ImpCast,
        "K2Node_MakeArray" => ctx.ImpMakeArray,
        "K2Node_CallParentFunction" => ctx.ImpParentCall,
        _ => 0,   // K2Node_MakeStruct and anything exotic: skipped (consumer keeps DefaultValue)
    };

    private static int FindPackageImport(Package pkg, string pkgPath)
    {
        for (int i = 0; i < pkg.ImportMap.Length; i++)
            if (pkg.ImportMap[i].ObjectName.Text == pkgPath && pkg.ImportMap[i].ClassName.Text == "Package")
                return -(i + 1);
        return 0;
    }

    /// <summary>Import for a "/Script/Pkg.Class" path as a Class (or ScriptStruct for structs).</summary>
    private static int ImportForPath(Ctx ctx, string? path, bool isStruct)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/Script/", StringComparison.Ordinal)) return 0;
        var dot = path.LastIndexOf('.');
        if (dot <= "/Script/".Length || dot + 1 >= path.Length) return 0;
        var pkgPath = path[..dot];
        var cls = path[(dot + 1)..];
        if (cls.Contains(':')) return 0;
        var key = (isStruct ? "S:" : "C:") + pkgPath + "." + cls;
        if (ctx.ClassImpCache.TryGetValue(key, out var c)) return c;
        if (!ctx.PkgImpCache.TryGetValue(pkgPath, out var pImp))
        {
            pImp = FindPackageImport(ctx.Tpl, pkgPath);
            if (pImp == 0) pImp = ctx.Spw.AddImport("/Script/CoreUObject", "Package", 0, pkgPath);
            ctx.PkgImpCache[pkgPath] = pImp;
        }
        var kind = isStruct ? "ScriptStruct" : "Class";
        c = BlueprintGraphBuilder.FindTemplateImport(ctx.Tpl, "/Script/CoreUObject", kind, pImp, cls);
        if (c == 0) c = ctx.Spw.AddImport("/Script/CoreUObject", kind, pImp, cls);
        ctx.ClassImpCache[key] = c;
        return c;
    }

    private static (byte[] payload, int scriptEnd) BuildNode(Ctx ctx, int flatIdx, GraphNode n)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        var t = new TaggedPropertyWriter(w, ctx.Spw.Name, ctx.IsUe5);

        switch (n.K2Class)
        {
            case "K2Node_CallFunction":
            case "K2Node_CallParentFunction":   // same payload as a call (no extra UPROPERTYs), other class
            {
                int classImp = 0;
                // Self-call (target is the BP being built): point at the local BGC, never a game import.
                bool selfCall = ctx.BgcPkg != 0 && !string.IsNullOrEmpty(ctx.SelfPkg)
                    && string.Equals(n.FuncPkg, ctx.SelfPkg, StringComparison.Ordinal)
                    && string.Equals(n.FuncClass, ctx.SelfClass, StringComparison.Ordinal);
                if (!selfCall && !string.IsNullOrWhiteSpace(n.FuncPkg) && !string.IsNullOrWhiteSpace(n.FuncClass))
                    classImp = ImportForPath(ctx, n.FuncPkg + "." + n.FuncClass, false);
                if (selfCall) classImp = ctx.BgcPkg;
                if (ctx.IsUe5)
                    t.Struct("FunctionReference", "MemberReference", "/Script/Engine", () =>
                    {
                        var inner = new TaggedPropertyWriter(w, ctx.Spw.Name, true);
                        WriteMemberReference(inner, classImp, n.FuncName ?? "Execute", true);
                    });
                else
                    t.Struct("FunctionReference", "MemberReference", () =>
                    {
                        var inner = new TaggedPropertyWriter(w, ctx.Spw.Name);
                        WriteMemberReference(inner, classImp, n.FuncName ?? "Execute", ctx.IsUe5);
                    });
                break;
            }
            case "K2Node_CustomEvent":
                t.Name("CustomFunctionName", n.EventName ?? "RecoveredEvent");
                break;
            case "K2Node_Event":
                if (ctx.IsUe5)
                    t.Struct("EventReference", "MemberReference", "/Script/Engine", () =>
                    {
                        var inner = new TaggedPropertyWriter(w, ctx.Spw.Name, true);
                        inner.Object("MemberParent", ctx.ActorClassImp);
                        inner.Name("MemberName", n.EventName ?? "ReceiveBeginPlay");
                        inner.WriteNone();
                    });
                else
                    t.Struct("EventReference", "MemberReference", () =>
                    {
                        var inner = new TaggedPropertyWriter(w, ctx.Spw.Name);
                        inner.Object("MemberParent", ctx.ActorClassImp);
                        inner.Name("MemberName", n.EventName ?? "ReceiveBeginPlay");
                        if (ctx.IsUe5) inner.GuidStruct("MemberGuid", default);
                        inner.WriteNone();
                    });
                t.Bool("bOverrideFunction", true);
                break;
            case "K2Node_VariableGet":
            case "K2Node_VariableSet":
                // Observed 5.x shape: {MemberName, MemberGuid} (self members carry no parent/context).
                // MemberGuid must be the variable's real guid (template NewVariables); zero reads as missing.
                FGuid16 varGuid = ctx.VarGuids.TryGetValue(n.VarName ?? "", out var vg) ? vg : default;
                if (ctx.IsUe5)
                    t.Struct("VariableReference", "MemberReference", "/Script/Engine", () =>
                    {
                        var inner = new TaggedPropertyWriter(w, ctx.Spw.Name, true);
                        inner.Name("MemberName", n.VarName ?? "Var");
                        inner.GuidStruct("MemberGuid", varGuid);
                        inner.Bool("bSelfContext", true);
                        inner.WriteNone();
                    });
                else
                    t.Struct("VariableReference", "MemberReference", () =>
                    {
                        var inner = new TaggedPropertyWriter(w, ctx.Spw.Name);
                        inner.Bool("bSelfContext", true);
                        inner.Name("MemberName", n.VarName ?? "Var");
                        if (ctx.IsUe5) inner.GuidStruct("MemberGuid", default);
                        inner.WriteNone();
                    });
                break;
            case "K2Node_IfThenElse":
            case "K2Node_SwitchInteger":
            case "K2Node_FunctionResult":
            case "K2Node_DynamicCast":
            case "K2Node_MakeArray":
                break;
            default:
                throw new InvalidOperationException($"unsupported {n.K2Class}");
        }

        t.Int("NodePosX", n.PosX);
        t.Int("NodePosY", n.PosY);
        var nodeGuid = FGuid16.NewGuid();
        t.GuidStruct("NodeGuid", nodeGuid);
        t.WriteNone();

        var pins = new List<SynthPin>(n.Inputs.Count + n.Outputs.Count);
        foreach (var gp in n.Inputs) pins.Add(ToSynthPin(ctx, flatIdx, gp, 0));
        foreach (var gp in n.Outputs) pins.Add(ToSynthPin(ctx, flatIdx, gp, 1));
        // Ground truth (template bytes): an event node's OutputDelegate pin references its own
        // function (BGC export, event name, the node's own guid). Without it the pin is unbound.
        if ((n.K2Class is "K2Node_CustomEvent" or "K2Node_Event") && ctx.BgcPkg != 0)
        {
            var od = pins.FirstOrDefault(x => x.PinName == "OutputDelegate" && x.Direction == 1);
            if (od != null)
            {
                od.MemberParentPkg = ctx.BgcPkg;
                od.MemberName = n.EventName ?? "RecoveredEvent";
                od.MemberGuid = nodeGuid;
            }
        }
        // Ground truth: variable self pins carry the BGC as SubCategoryObject + BitField 1 (hidden).
        if (n.K2Class is "K2Node_VariableGet" or "K2Node_VariableSet")
            foreach (var sp in pins.Where(x => x.PinName == "self"))
            {
                if (ctx.BgcPkg != 0) sp.SubCategoryObjPkg = ctx.BgcPkg;
                sp.PinBitField = 1;
            }
        long pinStart = ms.Position;   // script region (tagged props) ends where pins begin
        new PinSerializer(w, ctx.Spw.Name, ctx.IsUe5).WriteOwningPins(pins);
        // UK2Node_EditablePinBase::Serialize writes UserDefinedPins natively right after the pins
        // (TArray count + FUserPinInfo entries; editor templates show int32 0). Only the EditablePinBase
        // subtree (Event/CustomEvent/FunctionEntry/FunctionResult) carries it — Call/Var/Branch/etc. do not.
        // Omitting the COUNT desyncs the linker by 4B (SerializeNum>=0 / LinkerLoad 5898 open crashes).
        // The entries themselves are ALWAYS written as count=0: FUserPinInfo uses NATIVE UStruct layout
        // (bitpacked flags, FEdGraphTerminalType member) while pins use the custom int32-bool layout, so our
        // pin-shaped entries desync the reader (Invalid boolean / LinkerLoad 4856 size-mismatch editor crash).
        // The pin array above already carries every pin; the editor reconciles user pins from it.
        if (n.K2Class is "K2Node_CustomEvent" or "K2Node_Event" or "K2Node_FunctionEntry" or "K2Node_FunctionResult")
        {
            w.Write(0);
        }
        w.Flush();
        return (ms.ToArray(), (int)pinStart);
    }

    private static SynthPin ToSynthPin(Ctx ctx, int flatIdx, GraphPin gp, byte dir)
    {
        int subObj = 0;
        // Engine sub-category objects resolve in a stock editor; game ones would dangle LinkerLoad
        // (their stub classes may not exist) — leave those generic, the reconciler tolerates it.
        if (!string.IsNullOrWhiteSpace(gp.SubCategoryObjPath)
            && (gp.SubCategoryObjPath.StartsWith("/Script/Engine.", StringComparison.Ordinal)
                || gp.SubCategoryObjPath.StartsWith("/Script/CoreUObject.", StringComparison.Ordinal)))
            subObj = ImportForPath(ctx, gp.SubCategoryObjPath, gp.Category == "struct");
        // UE5.5 writes float/double pins as category "real" (FUE5ReleaseStreamObjectVersion
        // BlueprintPinsUseRealNumbers); 4.x uses "float". Verified against template ground truth.
        string cat = gp.Category, sub = string.IsNullOrWhiteSpace(gp.SubCategory) ? "None" : gp.SubCategory;
        if (ctx.IsUe5 && (cat == "float" || cat == "double")) { sub = cat; cat = "real"; }
        // Map-container pins carry a trailing PinValueType struct the bytecode never recovers (key AND value
        // terminal types); emitting the container flag without it desyncs every reader (Invalid boolean /
        // Linker 135 editor crash). Demote to a plain pin: the wire (by PinId) and reconciler survive, and
        // the editor restores the true container from the variable definition at compile.
        byte cont = gp.Container;
        if (cont == 3) cont = 0;
        // Template ground truth: bool variable pins default "false", never empty.
        string def = gp.DefaultValue ?? "";
        if (def.Length == 0 && cat == "bool") def = "false";
        var sp = new SynthPin
        {
            OwningNodePkg = ctx.NodePkg[flatIdx],
            PinId = ctx.PinIds[(flatIdx, gp.Name, dir)],
            PinName = gp.Name,
            Category = cat,
            SubCategory = sub,
            SubCategoryObjPkg = subObj,
            ContainerType = cont,
            Direction = dir,
            DefaultValue = def,
            IsUserPin = gp.IsUserPin,
        };
        if (ctx.Links.TryGetValue((flatIdx, gp.Name, dir), out var peers))
            foreach (var (peerPkg, peerId) in peers)
                sp.LinkedTo.Add((peerPkg, peerId));
        return sp;
    }

    private static void WriteMemberReference(TaggedPropertyWriter inner, int memberParent, string memberName, bool withGuid = false)
    {
        if (memberParent != 0) inner.Object("MemberParent", memberParent);
        else inner.Bool("bSelfContext", true);
        inner.Name("MemberName", memberName);
        // UE5 member references carry MemberGuid (redirector fixup); 4.21 has no such field.
        if (withGuid) inner.GuidStruct("MemberGuid", default);
        inner.WriteNone();
    }
}

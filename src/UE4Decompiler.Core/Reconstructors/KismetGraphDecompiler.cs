using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// A K2 data pin on a decompiled graph node. Categories are the editor's PinCategory names
/// ("exec", "bool", "int", "float", "string", "name", "text", "byte", "object", "class",
/// "struct", "softobject", "delegate", "wildcard", …). Unresolvable types use "wildcard" —
/// the editor keeps the pin and resolves it at compile time.
/// </summary>
public sealed class GraphPin
{
    public string Name = "";
    public string Category = "exec";
    public string SubCategory = "None";
    /// <summary>Optional "/Script/…" path of the pin's sub-category object (class/struct). Null = none.</summary>
    public string? SubCategoryObjPath;
    /// <summary>EPinContainerType: 0 none, 1 array, 2 set, 3 map.</summary>
    public byte Container;
    /// <summary>EGPD_Input = 0, EGPD_Output = 1.</summary>
    public byte Direction;
    /// <summary>Entry user-defined pin (function/event parameter): emitted as a pin AND a
    /// UserDefinedPins tail entry on CustomEvent nodes so calls can bind arguments.</summary>
    public bool IsUserPin;
    public string DefaultValue = "";
}

/// <summary>A data wire: consumer input pin reads producer output pin.</summary>
public readonly record struct DataLink(int Node, string Pin);

/// <summary>
/// One decompiled K2 node. Exec wiring is <see cref="ExecNext"/> (successor node ids, in pin
/// order); data wiring is <see cref="DataLinks"/> (input pin name → source).
/// </summary>
public sealed class GraphNode
{
    public int Id;
    public string K2Class = "K2Node_CallFunction";
    public string? FunctionRef;
    public string? FuncPkg;
    public string? FuncClass;
    public string? FuncName;
    public string? VarName;
    public string? EventName;
    public string? Note;
    public bool IsPure;
    public List<GraphPin> Inputs = new();
    public List<GraphPin> Outputs = new();
    public List<int> ExecNext = new();
    public Dictionary<string, DataLink> DataLinks = new(StringComparer.Ordinal);
    public int PosX;
    public int PosY;
}

/// <summary>All recovered graphs for one cooked function (one entry event + its node chains).</summary>
public sealed class FunctionGraph
{
    public string FunctionName = "";
    public bool IsUbergraph;
    public List<GraphNode> Nodes = new();
    public int EntryNode = -1;
    /// <summary>Cooked local/param types by name (category, subcategory, engine-only object path,
    /// container) — feeds grafted variable typing when pins stay unresolved.</summary>
    public readonly Dictionary<string, (string Cat, string Sub, string? Obj, byte Cont)> VarTypes = new(StringComparer.Ordinal);
}

/// <summary>
/// Decompiles cooked Kismet bytecode (<see cref="UStruct.ScriptBytecode"/>) into a K2 graph IR with
/// BOTH exec ordering and data flow, for every function in a blueprint:
///
/// <list type="bullet">
/// <item>Impure calls (incl. context/member, multicast/delegate, timeline markers) become
/// exec-wired <c>K2Node_CallFunction</c> nodes with data input pins + a <c>ReturnValue</c> output.</item>
/// <item>Pure calls become exec-less nodes whose <c>ReturnValue</c> feeds the consuming pin.</item>
/// <item><c>EX_Let*</c> becomes <c>K2Node_VariableSet</c>; variable refs become
/// <c>K2Node_VariableGet</c>; literals fold into pin <c>DefaultValue</c>s.</item>
/// <item><c>EX_JumpIfNot</c> becomes <c>K2Node_IfThenElse</c> with the tested expression wired to
/// <c>Condition</c>; <c>EX_SwitchValue</c> becomes a switch node; <c>EX_Return</c> becomes
/// <c>K2Node_FunctionResult</c>.</item>
/// </list>
///
/// Fidelity notes (honest limits): cooked bytecode drops static pin types/names, so parameter pins
/// are named <c>Arg0…</c> (or the real names when the callee <c>UFunction</c> resolves from the pak)
/// and unknown types use <c>wildcard</c>. Jump targets are statement indices, not graph links, so
/// branches wire linearly with the target recorded in <c>Note</c>. The editor may prune data pins it
/// cannot match to the live signature on load — the exec skeleton always survives, and the JSON
/// sidecar carries the exact IR for the re-import commandlet.
/// </summary>
public static class KismetGraphDecompiler
{
    public const int MaxNodesPerBlueprint = 800;

    /// <summary>Engine events that map to an overriding K2Node_Event (everything else gets a CustomEvent).</summary>
    internal static readonly HashSet<string> OverrideEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReceiveBeginPlay", "ReceiveTick", "ReceiveEndPlay", "ReceiveDestroyed",
        "ReceivePossess", "ReceiveUnpossess", "UserConstructionScript",
        "ReceiveActorBeginOverlap", "ReceiveActorEndOverlap",
        "ReceiveHit", "ReceiveAnyDamage", "ReceivePointDamage", "ReceiveRadialDamage",
        "ReceiveActorOnClicked", "ReceiveActorOnReleased",
        "ReceiveActorBeginCursorOver", "ReceiveActorEndCursorOver",
        "ReceiveActorOnInputTouchBegin", "ReceiveActorOnInputTouchEnd",
    };

    /// <summary>Decompile every bytecode-carrying function, ubergraph first. Never throws (per-function guard).</summary>
    public static List<FunctionGraph> DecompileFunctions(IEnumerable<UFunction> functions)
        => DecompileFunctions(functions, null);

    /// <summary>Cooked BGC member types (name -> pin type). Cooked serialization is per-struct,
    /// so ChildProperties are own members. Shared by decompile grounding and graft typing.</summary>
    internal static Dictionary<string, (string Cat, string Sub, string? Obj, byte Cont)> BgcMemberTypes(
        IReadOnlyList<CUE4Parse.UE4.Assets.Exports.UObject>? packageExports)
    {
        var memberTypes = new Dictionary<string, (string Cat, string Sub, string? Obj, byte Cont)>(StringComparer.Ordinal);
        if (packageExports is null) return memberTypes;
        try
        {
            foreach (var e in packageExports)
            {
                if (e is not UStruct s || !e.ExportType.EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (s.ChildProperties is null) continue;
                foreach (var field in s.ChildProperties)
                    if (field is CUE4Parse.UE4.Objects.UObject.FProperty fp && !memberTypes.ContainsKey(fp.Name.Text))
                        memberTypes[fp.Name.Text] = PinTypeDetails(fp);
            }
        }
        catch { }
        return memberTypes;
    }
    public static List<FunctionGraph> DecompileFunctions(IEnumerable<UFunction> functions,
        IReadOnlyList<CUE4Parse.UE4.Assets.Exports.UObject>? packageExports)
    {
        var fns = functions.Where(fn => fn.ScriptBytecode is { Length: > 0 })
            .OrderBy(fn => fn.Name.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(fn => fn.Name, StringComparer.Ordinal)
            .ToList();

        // Index sibling UFunctions by bare name, keeping only unambiguous ones (a wrong signature is
        // worse than ArgN: the editor would drop the misnamed pins).
        Dictionary<string, UFunction>? callees = null;
        // Cooked BGC member types (name -> pin type): grounds member getters/sets and the
        // incompatibility drop. (Cooked serialization is per-struct, so these are own members.)
        var memberTypes = BgcMemberTypes(packageExports);
        if (packageExports != null)
        {
            var byName = new Dictionary<string, List<UFunction>>(StringComparer.Ordinal);
            foreach (var uf in packageExports.OfType<UFunction>())
            {
                if (!byName.TryGetValue(uf.Name, out var list)) byName[uf.Name] = list = new();
                list.Add(uf);
            }
            callees = byName.Where(kv => kv.Value.Count == 1)
                .ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.Ordinal);
        }

        var graphs = new List<FunctionGraph>(fns.Count);
        int total = 0;
        foreach (var fn in fns)
        {
            if (total >= MaxNodesPerBlueprint)
            {
                Log.Warning("BP graph decompile truncated at {Max} nodes ({Left} function(s) skipped)",
                    MaxNodesPerBlueprint, fns.Count - graphs.Count);
                break;
            }
            try
            {
                var g = DecompileOne(fn, callees, memberTypes);
                total += g.Nodes.Count;
                graphs.Add(g);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Graph decompile failed for function {Fn}; emitting stub entry", fn.Name);
                graphs.Add(StubGraph(fn.Name));
            }
        }
        return graphs;
    }

    /// <summary>Cooked ubergraph dispatchers collide with the compiler's auto-stub: a CustomEvent named
    /// ExecuteUbergraph_X plus a thunk call to (own BGC, ExecuteUbergraph_X) registers the function twice
    /// ("Found more than one function with the same name"). Rename both sides to Ubergraph_X so the
    /// dispatcher event is unique and thunk calls bind to it.</summary>
    public static void RetargetUbergraphDispatch(List<FunctionGraph> graphs, string classShort)
    {
        if (graphs.Count == 0 || string.IsNullOrWhiteSpace(classShort)) return;
        string ubergraphEvent = "Ubergraph_" + classShort;
        string ownClass = classShort + "_C";
        foreach (var g in graphs)
        {
            if (!g.IsUbergraph) continue;
            var entry = g.Nodes.FirstOrDefault(n => n.Id == g.EntryNode);
            if (entry != null) entry.EventName = ubergraphEvent;
            g.FunctionName = ubergraphEvent;
        }
        foreach (var g in graphs)
            foreach (var n in g.Nodes)
            {
                if (n.K2Class != "K2Node_CallFunction" || string.IsNullOrEmpty(n.FuncName)) continue;
                if (!n.FuncName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)) continue;
                if (!string.Equals(n.FuncClass, ownClass, StringComparison.Ordinal)) continue;
                n.FuncName = ubergraphEvent;
                if (!string.IsNullOrEmpty(n.FunctionRef))
                {
                    int ci = n.FunctionRef.LastIndexOf(':');
                    if (ci >= 0) n.FunctionRef = n.FunctionRef.Substring(0, ci + 1) + ubergraphEvent;
                }
            }
    }

    private static FunctionGraph StubGraph(string name)
    {
        var g = new FunctionGraph { FunctionName = name };
        var entry = NewNode(g, OverrideEvents.Contains(name) ? "K2Node_Event" : "K2Node_CustomEvent");
        entry.EventName = name;
        entry.Outputs.Add(new GraphPin { Name = "OutputDelegate", Category = "delegate", Direction = 1 });
        entry.Outputs.Add(ExecOut("then"));
        g.EntryNode = entry.Id;
        return g;
    }

    private static FunctionGraph DecompileOne(UFunction fn, Dictionary<string, UFunction>? callees,
        Dictionary<string, (string Cat, string Sub, string? Obj, byte Cont)>? memberTypes = null)
    {
        var g = new FunctionGraph
        {
            FunctionName = fn.Name,
            IsUbergraph = fn.Name.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal),
        };
        var ctx = new FuncCtx(g, callees) { MemberTypes = memberTypes };
        // Cooked local types (params AND temps) for pin typing during emission + post passes.
        var tempTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (fn.ChildProperties != null)
                foreach (var field in fn.ChildProperties)
                    if (field is CUE4Parse.UE4.Objects.UObject.FProperty fp)
                    {
                        tempTypes[fp.Name.Text] = PinCategoryOfProperty(fp);
                        g.VarTypes[fp.Name.Text] = PinTypeDetails(fp);
                    }
        }
        catch { }
        ctx.TempTypes = tempTypes;

        // Entry: override event for known engine events, custom event otherwise.
        var entry = NewNode(g, OverrideEvents.Contains(fn.Name) ? "K2Node_Event" : "K2Node_CustomEvent");
        entry.EventName = fn.Name;
        entry.Outputs.Add(new GraphPin { Name = "OutputDelegate", Category = "delegate", Direction = 1 });
        entry.Outputs.Add(ExecOut("then"));
        g.EntryNode = entry.Id;
        ctx.AttachExec(entry.Id);
        // Custom-event parameters become user-defined output pins (thunk calls bind arguments by
        // name; the editor requires matching UserDefinedPins tail entries, written at emit time).
        // Cooked ChildProperties order params-before-locals (compiler invariant), so the leading run
        // of non-temp names are the params; the CPF_Parm flag is unreliable in cooked bytecode.
        // Ubergraph K2Node_Event_* frame inputs ride the same rule (never temp-prefixed).
        if (entry.K2Class == "K2Node_CustomEvent" && fn.ChildProperties != null)
            foreach (var fp in LeadingParams(fn))
            {
                var (pcat, psub, pobj, pcont) = PinTypeDetails(fp);
                entry.Outputs.Add(new GraphPin
                {
                    Name = fp.Name.Text,
                    Category = pcat,
                    SubCategory = psub,
                    SubCategoryObjPath = pobj,
                    Direction = 1,
                    IsUserPin = true,
                    Container = pcont,
                });
            }

        foreach (var expr in fn.ScriptBytecode!)
        {
            if (expr.Token is EExprToken.EX_EndOfScript or EExprToken.EX_Nothing
                or EExprToken.EX_NothingInt32 or EExprToken.EX_Tracepoint or EExprToken.EX_WireTracepoint
                or EExprToken.EX_Breakpoint or EExprToken.EX_InstrumentationEvent)
                continue;
            try { EmitStatement(ctx, expr); }
            catch (Exception ex)
            {
                Log.Debug(ex, "Graph decompile: statement {Idx} ({Tok}) in {Fn} fell back to note",
                    expr.StatementIndex, expr.Token, fn.Name);
                ctx.Note($"unmapped {expr.Token} @ {expr.StatementIndex}");
            }
        }
        UniqifyPins(g);
        RewireParamGets(g);
        EliminateTemps(g, tempTypes);
        PropagatePinTypes(g);
        GroundMemberPins(g, ctx.MemberTypes);
        // Dev bisection: UE4D_NO_SPLICE=1 skips the object->bool null-check splice.
        if (Environment.GetEnvironmentVariable("UE4D_NO_SPLICE") != "1")
            SpliceNullChecks(g);
        RenumberNodes(g);
        DropIncompatibleWires(g);
        return g;
    }

    /// <summary>Overwrite member-bound var pins with the cooked declared type (authoritative over
    /// propagation, which poisons pins from mismatched consumers). Runs after propagation so the
    /// member wins; Drop severs the genuinely-unconnectable wires next.</summary>
    private static void GroundMemberPins(FunctionGraph g,
        Dictionary<string, (string Cat, string Sub, string? Obj, byte Cont)>? memberTypes)
    {
        if (memberTypes is null) return;
        foreach (var n in g.Nodes)
        {
            if (n.K2Class is not ("K2Node_VariableGet" or "K2Node_VariableSet")) continue;
            if (string.IsNullOrEmpty(n.VarName) || !memberTypes.TryGetValue(n.VarName, out var mt)) continue;
            if (n.K2Class == "K2Node_VariableGet")
            {
                var o = n.Outputs.FirstOrDefault(p => p.Category != "exec");
                if (o != null) { o.Category = mt.Cat; o.SubCategory = mt.Sub; o.SubCategoryObjPath = mt.Obj; o.Container = mt.Cont; }
            }
            else
            {
                foreach (var p in n.Inputs.Where(p => p.Category != "exec" && p.Name != "self" && p.Direction == 0))
                { p.Category = mt.Cat; p.SubCategory = mt.Sub; p.SubCategoryObjPath = mt.Obj; p.Container = mt.Cont; }
                var og = n.Outputs.FirstOrDefault(p => p.Category != "exec");
                if (og != null) { og.Category = mt.Cat; og.SubCategory = mt.Sub; og.SubCategoryObjPath = mt.Obj; og.Container = mt.Cont; }
            }
        }
    }

    /// <summary>Cooked object-truthiness tests (EX_JumpIfNot on an object = null check, no compare node
    /// in bytecode): splice a pure NotEqual_ObjectObject (B left unwired = null) between an object
    /// producer and a bool consumer. Without this the wire is unconnectable either way.</summary>
    private static void SpliceNullChecks(FunctionGraph g)
    {
        var fresh = g.Nodes.Count > 0 ? g.Nodes.Max(n => n.Id) + 1 : 0;
        foreach (var n in g.Nodes.ToList())
        {
            foreach (var key in n.DataLinks.Keys.ToList())
            {
                var dl = n.DataLinks[key];
                var consumer = n.Inputs.FirstOrDefault(p => p.Name == key);
                var producer = NodeOf(g, dl.Node)?.Outputs.FirstOrDefault(p => p.Name == dl.Pin);
                if (consumer is null || producer is null) continue;
                if (consumer.Category != "bool" || producer.Category != "object") continue;
                var cmp = NewNode(g, "K2Node_CallFunction");
                cmp.Id = fresh++;
                cmp.FunctionRef = "/Script/Engine.KismetMathLibrary:NotEqual_ObjectObject";
                cmp.FuncPkg = "/Script/Engine"; cmp.FuncClass = "KismetMathLibrary"; cmp.FuncName = "NotEqual_ObjectObject";
                cmp.Inputs.Add(new GraphPin { Name = "A", Category = "object", Direction = 0 });
                cmp.Inputs.Add(new GraphPin { Name = "B", Category = "object", Direction = 0 });
                cmp.Outputs.Add(new GraphPin { Name = "ReturnValue", Category = "bool", Direction = 1 });
                cmp.DataLinks["A"] = new DataLink(dl.Node, dl.Pin);
                n.DataLinks[key] = new DataLink(cmp.Id, "ReturnValue");
            }
        }
    }

    /// <summary>Drop data wires the editor can never connect: scalar categories (bool/int/float/string/
    /// name/text/byte) must match (int&lt;-&gt;float auto-converts), object/struct/delegate only wire
    /// within their kind; wildcards wire to anything. A doomed wire errors at compile; unwired+default
    /// is clean. Runs last (categories final).</summary>
    private static void DropIncompatibleWires(FunctionGraph g)
    {
        // The editor auto-casts within the numeric family (byte/int/float/double/real discharge
        // through each other, incl. UE5's real=float/double rename) — only drop wires across
        // families or kinds. A doomed wire errors at compile; unwired+default is clean.
        static bool IsNumeric(string c) => c is "byte" or "int" or "float" or "double" or "real";
        static bool Compatible(string a, string b)
        {
            if (a == b) return true;
            if (a == "wildcard" || b == "wildcard") return true;
            if (a == "exec" || b == "exec") return true;
            if (IsNumeric(a) && IsNumeric(b)) return true;
            return false;
        }
        foreach (var n in g.Nodes)
            foreach (var key in n.DataLinks.Keys.ToList())
            {
                var dl = n.DataLinks[key];
                var consumer = n.Inputs.FirstOrDefault(p => p.Name == key);
                var producer = NodeOf(g, dl.Node)?.Outputs.FirstOrDefault(p => p.Name == dl.Pin);
                if (consumer is null || producer is null) continue;
                if (!Compatible(consumer.Category, producer.Category))
                    n.DataLinks.Remove(key);
            }
    }

    /// <summary>Compiler-generated temp prefixes (single-assignment locals, never member variables).</summary>
    private static bool IsTempName(string? n) => !string.IsNullOrEmpty(n)
        && (n.StartsWith("CallFunc_", StringComparison.Ordinal) || n.StartsWith("K2Node_Switch", StringComparison.Ordinal));

    /// <summary>Remove temp plumbing that can never bind as member lookups: Self getters (unwire consumer
    /// self pins — unwired self IS self), single-flow temp set/get pairs (consumers read the producer
    /// directly, dead stores spliced out), and set-less temps filled by calls (by-ref out-params become
    /// call output pins). Bypass guards: store reachable + dominating every exec anchor; filler calls
    /// ordered by id (documented straight-flow approximation).</summary>
    private static void EliminateTemps(FunctionGraph g, Dictionary<string, string> tempTypes)
    {
        // (i) Self getters: a self-of-self input is meaningless — clear it — and unwire all
        // consumers (an unwired self pin IS self; a data pin falls back to its default). A VarGet
        // "Self" can never bind as a member lookup, and a red node fails the whole compile while a
        // defaulted pin compiles — so dropping the wires is strictly better. Survivors with no
        // consumers left are pruned at emit time.
        foreach (var n in g.Nodes.ToList())
        {
            if (n.K2Class != "K2Node_VariableGet" || n.VarName != "Self") continue;
            n.DataLinks.Remove("self");
            foreach (var m in g.Nodes)
                foreach (var key in m.DataLinks.Keys.ToList())
                    if (m.DataLinks[key].Node == n.Id)
                        m.DataLinks.Remove(key);
        }
        // (ii) Temp set/get pairs.
        var sets = g.Nodes.Where(n => n.K2Class == "K2Node_VariableSet" && IsTempName(n.VarName)).ToList();
        var gets = g.Nodes.Where(n => n.K2Class == "K2Node_VariableGet" && IsTempName(n.VarName)).ToList();
        if (sets.Count == 0 || gets.Count == 0) return;
        var pairedSets = new HashSet<int>();
        var execPreds = ExecPredMap(g);
        var reachable = ReachableFromEntry(g);
        // Data-forward map: (producer node, pin) -> consumer node ids.
        var dataFwd = new Dictionary<(int, string), List<int>>();
        foreach (var m in g.Nodes)
            foreach (var kv in m.DataLinks)
            {
                var k = (kv.Value.Node, kv.Value.Pin);
                if (!dataFwd.TryGetValue(k, out var l)) dataFwd[k] = l = new();
                l.Add(m.Id);
            }
        bool HasExec(GraphNode n) => n.Inputs.Any(p => p.Category == "exec") || n.Outputs.Any(p => p.Category == "exec");
        // Exec anchors of a value: exec nodes reachable by following data wires forward (bounded).
        // Temp bypass is sound iff the store dominates every anchor.
        HashSet<int> Anchors(int nodeId, string pin)
        {
            var outs = new HashSet<int>();
            var vis = new HashSet<(int, string)> { (nodeId, pin) };
            var q = new Queue<(int, string)>();
            q.Enqueue((nodeId, pin));
            int steps = 0;
            while (q.Count > 0 && steps++ < 500)
            {
                var (nid, pn) = q.Dequeue();
                if (!dataFwd.TryGetValue((nid, pn), out var cs)) continue;
                foreach (var c in cs)
                {
                    var cn = NodeOf(g, c);
                    if (cn is null) continue;
                    if (HasExec(cn)) outs.Add(c);
                    else foreach (var op in cn.Outputs)
                            if (vis.Add((c, op.Name))) q.Enqueue((c, op.Name));
                }
            }
            return outs;
        }
        foreach (var get in gets)
        {
            var anchors = Anchors(get.Id, get.VarName ?? "");
            if (anchors.Count == 0) continue;   // dead value: emitter prunes the getter
            var set = sets.Where(s => s.Id < get.Id && s.VarName == get.VarName
                    && reachable.Contains(s.Id) && anchors.All(a => NodeOf(g, a) is { } an && ExecDominates(execPreds, g, s, an)))
                .OrderByDescending(s => s.Id).FirstOrDefault();
            if (set is null) continue;
            if (!set.DataLinks.TryGetValue(set.VarName ?? "", out var prod)) continue;
            bool repointed = false;
            foreach (var m in g.Nodes)
                foreach (var key in m.DataLinks.Keys.ToList())
                    if (m.DataLinks[key].Node == get.Id)
                    { m.DataLinks[key] = prod; repointed = true; }
            if (repointed) pairedSets.Add(set.Id);
        }
        // Splice out fully-bypassed stores (exec preds inherit the set's successors). Readers
        // still pointing at the set (direct Output_Get reads) are repointed at its producer first.
        foreach (var setId in pairedSets)
        {
            var set = sets.FirstOrDefault(s => s.Id == setId);
            if (set is null) continue;
            DataLink? prod = set.DataLinks.TryGetValue(set.VarName ?? "", out var pl) ? pl : null;
            foreach (var m in g.Nodes)
                foreach (var key in m.DataLinks.Keys.ToList())
                    if (m.DataLinks[key].Node == setId && prod is { } pr)
                        m.DataLinks[key] = pr;
            bool stillRead = g.Nodes.Any(m => m.DataLinks.Values.Any(dl => dl.Node == setId));
            if (stillRead) continue;
            foreach (var m in g.Nodes)
                m.ExecNext = m.ExecNext.SelectMany(id => id == setId ? set.ExecNext : new List<int> { id }).ToList();
            g.Nodes.Remove(set);
        }
        // (iii) Set-less CallFunc_* temps filled by calls (cooked by-ref out-params, e.g. OVR request
        // temps): the filler is the call the temp is named after (CallFunc_<F>_<R> belongs to <F>) —
        // pure data readers can never fill. The filled value is unreadable without member variables
        // (a UPARAM(ref) stub pin demands a wired variable input), so drop the temp reads: consumers
        // keep their (typed) pins unwired with defaults, arg inputs reading temps are removed, and the
        // orphaned getters are pruned at emit time. Exec order is fully preserved.
        var setNames = new HashSet<string>(g.Nodes
            .Where(n => n.K2Class == "K2Node_VariableSet" && !string.IsNullOrEmpty(n.VarName))
            .Select(n => n.VarName!), StringComparer.Ordinal);
        var tempGets = g.Nodes
            .Where(n => n.K2Class == "K2Node_VariableGet" && (n.VarName ?? "").StartsWith("CallFunc_", StringComparison.Ordinal)
                && !setNames.Contains(n.VarName ?? ""))
            .ToList();
        foreach (var grp in tempGets.GroupBy(n => n.VarName))
        {
            var tname = grp.Key ?? "";
            bool FillerMatch(GraphNode call)
            {
                var f = call.FuncName ?? "";
                if (f.Length == 0) return false;
                var rest = tname["CallFunc_".Length..];
                return rest == f || rest.StartsWith(f + "_", StringComparison.Ordinal);
            }
            var fillers = g.Nodes
                .Where(n => n.K2Class is ("K2Node_CallFunction" or "K2Node_CallParentFunction") && FillerMatch(n)
                    && n.Inputs.Any(p => p.Direction == 0 && n.DataLinks.TryGetValue(p.Name, out var dl)
                        && grp.Any(gg => gg.Id == dl.Node)))
                .OrderBy(n => n.Id).ToList();
            if (fillers.Count == 0) continue;
            var first = fillers[0];
            foreach (var pin in first.Inputs.Where(p => p.Direction == 0
                    && first.DataLinks.TryGetValue(p.Name, out var dl) && grp.Any(gg => gg.Id == dl.Node)).ToList())
            {
                first.DataLinks.Remove(pin.Name);
                first.Inputs.Remove(pin);
            }
            // Unwire every other read of the temp (getters orphan -> pruned below). Pins left
            // wildcard+defaultless by the drop are removed outright (the reconciler re-adds signature
            // pins for engine calls; stub sigs mirror surviving pins) — an unwired wildcard errors.
            foreach (var m in g.Nodes)
                foreach (var key in m.DataLinks.Keys.ToList())
                {
                    var dl = m.DataLinks[key];
                    if (!grp.Any(gg => gg.Id == dl.Node)) continue;
                    m.DataLinks.Remove(key);
                    var pin = m.Inputs.FirstOrDefault(p => p.Name == key);
                    if (pin != null && pin.Category == "wildcard" && pin.DefaultValue.Length == 0)
                        m.Inputs.Remove(pin);
                }
            foreach (var gg in grp.ToList())
                if (!g.Nodes.Any(m => m.DataLinks.Values.Any(dl => dl.Node == gg.Id)))
                    g.Nodes.Remove(gg);
        }
        RenumberNodes(g);
    }

    /// <summary>Restore the Id==index invariant after node removal (the emitter's ToFlat and NodeOf
    /// rely on it): reassign dense ids in list order and rewrite every reference.</summary>
    private static void RenumberNodes(FunctionGraph g)
    {
        var idMap = new Dictionary<int, int>();
        for (int i = 0; i < g.Nodes.Count; i++) idMap[g.Nodes[i].Id] = i;
        for (int i = 0; i < g.Nodes.Count; i++) g.Nodes[i].Id = i;
        foreach (var n in g.Nodes)
        {
            n.ExecNext = n.ExecNext.Select(id => idMap.TryGetValue(id, out var ni) ? ni : id).ToList();
            foreach (var key in n.DataLinks.Keys.ToList())
            {
                var dl = n.DataLinks[key];
                n.DataLinks[key] = idMap.TryGetValue(dl.Node, out var ni) ? new DataLink(ni, dl.Pin) : dl;
            }
        }
        if (idMap.TryGetValue(g.EntryNode, out var ne)) g.EntryNode = ne;
    }

    /// <summary>Exec predecessor map: node id -> ids with an ExecNext edge into it.</summary>
    private static Dictionary<int, List<int>> ExecPredMap(FunctionGraph g)
    {
        var preds = new Dictionary<int, List<int>>();
        foreach (var n in g.Nodes)
            foreach (var succ in n.ExecNext)
            {
                if (!preds.TryGetValue(succ, out var l)) preds[succ] = l = new();
                l.Add(n.Id);
            }
        return preds;
    }

    /// <summary>Nodes forward-reachable from the entry over exec edges (bounded).</summary>
    private static HashSet<int> ReachableFromEntry(FunctionGraph g)
    {
        var seen = new HashSet<int> { g.EntryNode };
        var queue = new Queue<int>();
        queue.Enqueue(g.EntryNode);
        int steps = 0;
        while (queue.Count > 0 && steps++ < 2000)
        {
            var cur = queue.Dequeue();
            var n = NodeOf(g, cur);
            if (n is null) continue;
            foreach (var succ in n.ExecNext)
                if (seen.Add(succ)) queue.Enqueue(succ);
        }
        return seen;
    }

    /// <summary>Does temp store <paramref name="set"/> dominate exec node <paramref name="target"/> — every
    /// backward exec path from target reaches set before any other same-temp store, a no-pred node, a
    /// cycle, or the step cap? Bounded BFS over exec predecessors (merges pass through on all arms).</summary>
    private static bool ExecDominates(Dictionary<int, List<int>> preds, FunctionGraph g, GraphNode set, GraphNode target)
    {
        if (target.Id == set.Id) return true;
        var visited = new HashSet<int> { target.Id };
        var queue = new Queue<int>();
        queue.Enqueue(target.Id);
        int steps = 0;
        while (queue.Count > 0 && steps++ < 2000)
        {
            var cur = queue.Dequeue();
            if (!preds.TryGetValue(cur, out var ps) || ps.Count == 0) return false;  // start reached
            foreach (var p in ps)
            {
                if (p == set.Id) continue;   // this path satisfied
                var pn = NodeOf(g, p);
                if (pn != null && pn.K2Class == "K2Node_VariableSet" && pn.VarName == set.VarName)
                    return false;            // another store wins this path
                if (visited.Add(p)) queue.Enqueue(p);
            }
        }
        return queue.Count == 0;
    }

    /// <summary>Signature parameters: ChildProperties minus machine names (ReturnValue, EntryPoint,
    /// ubergraph frame) and compiler temps. Cooked order interleaves params and locals, so every
    /// non-temp name counts (no positional assumptions).</summary>
    private static List<CUE4Parse.UE4.Objects.UObject.FProperty> LeadingParams(CUE4Parse.UE4.Objects.UObject.UFunction fn)
    {
        var list = new List<CUE4Parse.UE4.Objects.UObject.FProperty>();
        try
        {
            if (fn.ChildProperties is null) return list;
            foreach (var field in fn.ChildProperties)
            {
                if (field is not CUE4Parse.UE4.Objects.UObject.FProperty fp) continue;
                var n = fp.Name.Text;
                if (n == "ReturnValue" || n == "EntryPoint" || n.Contains("UberGraphFrame", StringComparison.Ordinal))
                    continue;
                if (IsTempPrefix(n)) continue;
                list.Add(fp);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Cooked-compiler temp prefixes (never designer parameters).</summary>
    private static bool IsTempPrefix(string n) =>
        n.StartsWith("CallFunc_", StringComparison.Ordinal)
        || n.StartsWith("K2Node_Switch", StringComparison.Ordinal)
        // Delegate machinery temps (K2Node_CreateDelegate_OutputDelegate_N, ...) ride the ubergraph as
        // pseudo-params; admitting them as CustomEvent user pins emits dozens of unbound delegate pins
        // whose bodies desync every reader (Invalid bool / Linker 135 editor crash).
        || n.StartsWith("K2Node_", StringComparison.Ordinal)
        || n.StartsWith("Temp_", StringComparison.Ordinal)
        || n.StartsWith("Loop_", StringComparison.Ordinal)
        || n.StartsWith("Select_", StringComparison.Ordinal)
        || n.StartsWith("Array_", StringComparison.Ordinal)
        || n.StartsWith("MADE_", StringComparison.Ordinal);

    /// <summary>VariableGets reading a function parameter (cooked K2Node_Event_X locals, forwarded args)
    /// can never bind as member lookups. Repoint their consumers at the entry's user-pin output; the
    /// orphaned getter is pruned at emit time. Skips vars written anywhere in-graph (reassigned params
    /// read back mutated values — rewiring those would freeze them at entry).</summary>
    private static void RewireParamGets(FunctionGraph g)
    {
        var entry = g.Nodes.FirstOrDefault(n => n.Id == g.EntryNode);
        if (entry is null) return;
        var userPins = new HashSet<string>(entry.Outputs.Where(p => p.IsUserPin).Select(p => p.Name),
            StringComparer.Ordinal);
        if (userPins.Count == 0) return;
        var written = new HashSet<string>(g.Nodes
            .Where(n => n.K2Class == "K2Node_VariableSet" && !string.IsNullOrEmpty(n.VarName))
            .Select(n => n.VarName!), StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            if (n.Id == entry.Id || n.K2Class != "K2Node_VariableGet") continue;
            if (string.IsNullOrEmpty(n.VarName) || !userPins.Contains(n.VarName)) continue;
            if (written.Contains(n.VarName)) continue;
            foreach (var m in g.Nodes)
                foreach (var key in m.DataLinks.Keys.ToList())
                    if (m.DataLinks[key].Node == n.Id)
                        m.DataLinks[key] = new DataLink(entry.Id, n.VarName);
        }
    }

    /// <summary>Fixpoint type propagation across data wires: a <c>wildcard</c> pin wired to a concrete
    /// pin adopts its type (category + subcategory + object path + container). Cooked bytecode drops
    /// static types, so without this every inferred-through-wiring pin compiles as "undetermined".</summary>
    private static void PropagatePinTypes(FunctionGraph g)
    {
        for (int round = 0; round < 10; round++)
        {
            bool changed = false;
            foreach (var n in g.Nodes)
            {
                foreach (var kv in n.DataLinks)
                {
                    var consumer = n.Inputs.FirstOrDefault(p => p.Name == kv.Key);
                    var producer = NodeOf(g, kv.Value.Node)?.Outputs.FirstOrDefault(p => p.Name == kv.Value.Pin);
                    if (consumer is null || producer is null) continue;
                    if (AdoptIfWildcard(consumer, producer)) changed = true;
                    if (AdoptIfWildcard(producer, consumer)) changed = true;
                }
            }
            if (!changed) break;
        }
    }

    /// <summary>If <paramref name="wild"/> is a wildcard data pin and <paramref name="solid"/> is concrete,
    /// copy the type over. Never touches exec pins; first concrete type wins on conflicts.</summary>
    private static bool AdoptIfWildcard(GraphPin wild, GraphPin solid)
    {
        if (wild.Category != "wildcard" || solid.Category is "wildcard" or "exec") return false;
        wild.Category = solid.Category;
        wild.SubCategory = solid.SubCategory;
        wild.SubCategoryObjPath = solid.SubCategoryObjPath;
        wild.Container = solid.Container;
        return true;
    }

    /// <summary>PinIds are keyed (node, name, dir) at emit time, so duplicate pin names within one
    /// direction must be disambiguated (a cooked signature can legally repeat a name like "self").
    /// Input renames carry their DataLinks entry along; output renames are resolved by the emitter's
    /// first-non-exec fallback.</summary>
    private static void UniqifyPins(FunctionGraph g)
    {
        foreach (var n in g.Nodes)
        {
            var seenIn = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in n.Inputs)
            {
                if (!seenIn.Add(p.Name))
                {
                    int k = 2;
                    string renamed;
                    do { renamed = $"{p.Name}_{k++}"; } while (!seenIn.Add(renamed));
                    if (n.DataLinks.TryGetValue(p.Name, out var link))
                    {
                        n.DataLinks.Remove(p.Name);
                        n.DataLinks[renamed] = link;
                    }
                    p.Name = renamed;
                }
            }
            var seenOut = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in n.Outputs)
            {
                if (!seenOut.Add(p.Name))
                {
                    int k = 2;
                    string renamed;
                    do { renamed = $"{p.Name}_{k++}"; } while (!seenOut.Add(renamed));
                    p.Name = renamed;
                }
            }
        }
    }

    #region statement emission

    private sealed class FuncCtx
    {
        public readonly FunctionGraph G;
        public readonly Dictionary<string, UFunction>? Callees;
        public Dictionary<string, string>? TempTypes;
        public Dictionary<string, (string Cat, string Sub, string? Obj, byte Cont)>? MemberTypes;
        public int CurrentExec = -1;   // last impure node (or entry) awaiting its successor
        public readonly List<string> Notes = new();
        public FuncCtx(FunctionGraph g, Dictionary<string, UFunction>? callees) { G = g; Callees = callees; }
        /// <summary>Cooked local type for a temp name (pin typing when pointers don't resolve).</summary>
        public string? TempType(string? name)
            => !string.IsNullOrEmpty(name) && TempTypes != null && TempTypes.TryGetValue(name, out var c) ? c : null;
        public void AttachExec(int nodeId)
        {
            if (CurrentExec >= 0)
                NodeOf(G, CurrentExec).ExecNext.Add(nodeId);
            CurrentExec = nodeId;
        }
        public void Note(string s) => Notes.Add(s);
    }

    private static GraphNode? NodeOf(FunctionGraph g, int id) => g.Nodes.FirstOrDefault(n => n.Id == id);

    private static GraphNode NewNode(FunctionGraph g, string k2class)
    {
        var n = new GraphNode { Id = g.Nodes.Count, K2Class = k2class };
        g.Nodes.Add(n);
        return n;
    }

    private static void EmitStatement(FuncCtx ctx, KismetExpression expr)
    {
        var g = ctx.G;
        switch (expr)
        {
            case EX_JumpIfNot j:
            {
                var br = NewNode(g, "K2Node_IfThenElse");
                br.Outputs.Add(ExecOut("then"));
                br.Outputs.Add(ExecOut("else"));
                var cond = br.Inputs.Count;
                br.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
                br.Inputs.Add(new GraphPin { Name = "Condition", Category = "bool", Direction = 0, DefaultValue = "true" });
                var src = BuildData(ctx, j.BooleanExpression);
                if (src is { } s) br.DataLinks["Condition"] = s;
                br.Note = $"JumpIfNot -> stmt {j.CodeOffset}";
                ctx.AttachExec(br.Id);
                _ = cond;
                return;
            }
            case EX_Jump j:
                // Unconditional jump: record the target; keep the linear chain (see class notes).
                ctx.Note($"Jump -> {j.ObjectName} (code offset {j.CodeOffset})");
                return;
            case EX_ComputedJump c:
                // Switch-dispatch jump: recover the selector as data for inspection.
                BuildData(ctx, c.CodeOffsetExpression);
                ctx.Note("ComputedJump (switch dispatch)");
                return;
            case EX_Return r:
            {
                var ret = NewNode(g, "K2Node_FunctionResult");
                ret.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
                if (r.ReturnExpression != null)
                {
                    var pin = new GraphPin { Name = "ReturnValue", Category = "wildcard", Direction = 0 };
                    ret.Inputs.Add(pin);
                    var src = BuildData(ctx, r.ReturnExpression);
                if (src is { } s)
                {
                    var producer = NodeOf(g, s.Node);
                    var outPin = producer?.Outputs.FirstOrDefault(p => p.Name == s.Pin);
                    if (outPin != null) pin.Category = outPin.Category;
                    ret.DataLinks["ReturnValue"] = s;
                }
                }
                ctx.AttachExec(ret.Id);
                return;
            }
            case EX_LetBase l:  // EX_Let, EX_LetObj, EX_LetBool, EX_LetWeakObjPtr, EX_LetValueOnPersistentFrame, EX_LetDelegate, …
            {
                var set = NewNode(g, "K2Node_VariableSet");
                var varName = VarNameOf(l.Variable) ?? "Var";
                set.VarName = varName;
                set.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
                var inPin = new GraphPin { Name = varName, Category = "wildcard", Direction = 0 };
                set.Inputs.Add(inPin);
                set.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
                set.Outputs.Add(ExecOut("then"));
                var outGet = new GraphPin { Name = "Output_Get", Category = "wildcard", Direction = 1 };
                set.Outputs.Add(outGet);
                var src = BuildData(ctx, l.Assignment);
                if (src is { } s)
                {
                    var producer = NodeOf(g, s.Node);
                    var outPin = producer?.Outputs.FirstOrDefault(p => p.Name == s.Pin);
                    if (outPin != null)
                    {
                        inPin.Category = outPin.Category;
                        inPin.SubCategoryObjPath = outPin.SubCategoryObjPath;
                        outGet.Category = outPin.Category;
                        outGet.SubCategoryObjPath = outPin.SubCategoryObjPath;
                    }
                    set.DataLinks[varName] = s;
                }
                else if (l is EX_LetBool)
                {
                    // No assignment expression (uses native bool value): fold from the property pointer.
                    inPin.Category = "bool";
                    outGet.Category = "bool";
                }
                if (expr is EX_Let let && let.Property.Old != null)
                {
                    // Declared local type wins, but only when it resolves: an unresolvable pointer
                    // yields wildcard and must NOT clobber the producer's concrete type (or bool above).
                    var pc = VarCategoryOf(let.Property);
                    if (pc != "wildcard") ClassifyVarPin(inPin, outGet, let.Property);
                }
                if (inPin.Category == "wildcard" || outGet.Category == "wildcard")
                {
                    var tc = ctx.TempType(varName);
                    if (tc != null)
                    {
                        if (inPin.Category == "wildcard") inPin.Category = tc;
                        if (outGet.Category == "wildcard") outGet.Category = tc;
                    }
                }
                if (ctx.MemberTypes != null && ctx.MemberTypes.TryGetValue(varName, out var mmt))
                {
                    foreach (var pp in new[] { inPin, outGet })
                    { pp.Category = mmt.Cat; pp.SubCategory = mmt.Sub; pp.SubCategoryObjPath ??= mmt.Obj; pp.Container = mmt.Cont; }
                }
                ctx.AttachExec(set.Id);
                return;
            }
            case EX_SwitchValue sw:
            {
                var node = NewNode(g, "K2Node_SwitchInteger");
                node.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
                node.Inputs.Add(new GraphPin { Name = "Selection", Category = "int", Direction = 0, DefaultValue = "0" });
                var sel = BuildData(ctx, sw.IndexTerm);
                if (sel is { } s) node.DataLinks["Selection"] = s;
                if (sw.DefaultTerm != null) BuildData(ctx, sw.DefaultTerm);
                if (sw.Cases != null)
                    foreach (var c in sw.Cases)
                    {
                        var lit = LiteralOf(c.CaseIndexValueTerm);
                        node.Outputs.Add(ExecOut(lit ?? $"Case{g.Nodes.Count}"));
                        if (c.CaseTerm != null) BuildData(ctx, c.CaseTerm);
                    }
                node.Note = $"SwitchValue end-goto {sw.EndGotoOffset}";
                ctx.AttachExec(node.Id);
                return;
            }
            case EX_PushExecutionFlow p:
                ctx.Note($"PushExecutionFlow {p.ObjectPath} (+{p.PushingAddress})");
                return;
            case EX_PopExecutionFlow:
            case EX_PopExecutionFlowIfNot:
                ctx.Note(expr.Token.ToString());
                return;
        }

        // Calls + everything else with a function reference: impure call node (exec) or pure data root.
        if (TryGetCallRef(expr, out var fref, out var pkg, out var cls, out var func))
        {
            EmitCallStatement(ctx, expr, fref, pkg, cls, func);
            return;
        }

        // No direct reference: recurse for nested calls (context/member chains, casts, …).
        bool found = false;
        foreach (var child in ChildExpressions(expr))
        {
            if (TryGetCallRef(child, out var cfref, out var cpkg, out var ccls, out var cfunc))
            {
                EmitCallStatement(ctx, child, cfref, cpkg, ccls, cfunc);
                found = true;
            }
            else if (child is EX_LetBase nestedLet)
            {
                EmitStatement(ctx, nestedLet);
                found = true;
            }
        }
        if (!found) ctx.Note($"unmapped {expr.Token} @ {expr.StatementIndex}");
    }

    private static void EmitCallStatement(FuncCtx ctx, KismetExpression expr, string fref, string pkg, string cls, string func)
    {
        var g = ctx.G;
        // Calls to engine events (ReceiveTick/BeginPlay/…) are super-calls: the engine forbids calling
        // them as plain CallFunctions ("should not be called from a Blueprint"), so emit a parent node.
        string callCls = OverrideEvents.Contains(func) ? "K2Node_CallParentFunction" : "K2Node_CallFunction";
        if (IsDelegateCall(expr, func))
        {
            var d = NewNode(g, callCls);
            d.FunctionRef = fref; d.FuncPkg = pkg; d.FuncClass = cls; d.FuncName = func;
            d.Note = $"delegate op {expr.Token}";
            d.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
            d.Outputs.Add(ExecOut("then"));
            AddCallDataPins(ctx, d, expr, pure: false);
            ctx.AttachExec(d.Id);
            return;
        }

        bool pure = IsPureCall(expr, pkg, cls, func);
        var node = NewNode(g, callCls);
        node.FunctionRef = fref; node.FuncPkg = pkg; node.FuncClass = cls; node.FuncName = func;
        node.IsPure = pure;
        if (!pure)
        {
            node.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
            node.Outputs.Add(ExecOut("then"));
        }
        AddCallDataPins(ctx, node, expr, pure);
        if (!pure) ctx.AttachExec(node.Id);
        else ctx.Note($"pure {func} (data only)");
    }

    #endregion

    #region data subgraph

    /// <summary>Build the data-flow subgraph for an r-value. Returns the producer (node, pin),
    /// or null when the value folded into a literal (use <see cref="LiteralOf"/> / <see cref="PinCategoryOf"/>).</summary>
    private static DataLink? BuildData(FuncCtx ctx, KismetExpression? expr)
    {
        if (expr is null) return null;
        var g = ctx.G;

        if (IsLiteral(expr)) return null;

        switch (expr)
        {
            case EX_VariableBase v:
            {
                var get = NewNode(g, "K2Node_VariableGet");
                get.VarName = VarNameOf(expr) ?? "Var";
                get.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
                var outPin = new GraphPin { Name = get.VarName, Category = "wildcard", Direction = 1 };
                ClassifyVarPin(null, outPin, v.Variable);
                if (outPin.Category == "wildcard")
                {
                    var tc = ctx.TempType(get.VarName);
                    if (tc != null) outPin.Category = tc;
                }
                // Cooked member type grounds the pin when pointers don't resolve.
                if ((outPin.Category == "wildcard" || outPin.SubCategoryObjPath is null)
                    && ctx.MemberTypes != null && get.VarName != null
                    && ctx.MemberTypes.TryGetValue(get.VarName, out var mt))
                { outPin.Category = mt.Cat; outPin.SubCategory = mt.Sub; outPin.SubCategoryObjPath ??= mt.Obj; outPin.Container = mt.Cont; }
                get.Outputs.Add(outPin);
                return new DataLink(get.Id, outPin.Name);
            }
            case EX_Self:
            {
                var get = NewNode(g, "K2Node_VariableGet");
                get.VarName = "Self";
                get.Inputs.Add(new GraphPin { Name = "self", Category = "object", Direction = 0 });
                get.Outputs.Add(new GraphPin { Name = "Self", Category = "object", Direction = 1 });
                return new DataLink(get.Id, "Self");
            }
            case EX_Cast c:
            {
                var cast = NewNode(g, "K2Node_DynamicCast");
                cast.Note = $"cast {c.ConversionType}";
                cast.Inputs.Add(new GraphPin { Name = "Object", Category = "object", Direction = 0 });
                var src = BuildData(ctx, c.Target);
                if (src is { } s) cast.DataLinks["Object"] = s;
                var outPin = new GraphPin { Name = "AsResult", Category = "object", Direction = 1 };
                cast.Outputs.Add(outPin);
                return new DataLink(cast.Id, outPin.Name);
            }
            case EX_StructConst sc:
            {
                var make = NewNode(g, "K2Node_MakeStruct");
                make.Note = $"make {sc.Struct?.ResolvedObject?.Name ?? sc.Struct?.Name ?? "struct"}";
                if (sc.Properties != null)
                    for (int i = 0; i < sc.Properties.Length; i++)
                    {
                        var pin = new GraphPin { Name = $"Arg{i}", Category = "wildcard", Direction = 0 };
                        make.Inputs.Add(pin);
                        var src = BuildData(ctx, sc.Properties[i]);
                        if (src is { } s) make.DataLinks[pin.Name] = s;
                        else pin.DefaultValue = LiteralDefault(sc.Properties[i]) ?? "";
                    }
                var o = new GraphPin { Name = "ReturnValue", Category = "struct", Direction = 1 };
                make.Outputs.Add(o);
                return new DataLink(make.Id, o.Name);
            }
            case EX_ArrayConst ac:
            {
                var make = NewNode(g, "K2Node_MakeArray");
                make.Inputs.Add(new GraphPin { Name = "Array", Category = "wildcard", Container = 1, Direction = 0 });
                if (ac.Elements != null)
                    foreach (var el in ac.Elements) BuildData(ctx, el);
                var o = new GraphPin { Name = "ReturnValue", Category = "wildcard", Container = 1, Direction = 1 };
                make.Outputs.Add(o);
                return new DataLink(make.Id, o.Name);
            }
            case EX_SetConst or EX_MapConst:
                return null; // collection literal: consumer keeps default
        }

        if (TryGetCallRef(expr, out var fref, out var pkg, out var cls, out var func))
        {
            // Nested call in data position: pure -> data node; impure -> exec node whose ReturnValue we still wire
            // (execution-order caveat documented on the class).
            bool pure = IsPureCall(expr, pkg, cls, func);
            var node = NewNode(g, "K2Node_CallFunction");
            node.FunctionRef = fref; node.FuncPkg = pkg; node.FuncClass = cls; node.FuncName = func;
            node.IsPure = pure;
            if (!pure)
            {
                node.Inputs.Add(new GraphPin { Name = "execute", Category = "exec", Direction = 0 });
                node.Outputs.Add(ExecOut("then"));
            }
            AddCallDataPins(ctx, node, expr, pure);
            if (!pure) ctx.AttachExec(node.Id);
            // First data output (ReturnValue or resolved name); void calls have none — no value to wire.
            var ret = node.Outputs.FirstOrDefault(p => p.Category != "exec" && p.Direction == 1);
            if (ret is null) return null;
            return new DataLink(node.Id, ret.Name);
        }

        // Transparent wrappers (context/member access, interface, struct-member): wire through the inner value,
        // but keep the outer target (self) for the eventual consumer via a note.
        foreach (var child in ChildExpressions(expr))
        {
            var inner = BuildData(ctx, child);
            if (inner is { } s) return s;
            if (IsLiteral(child)) return null;
        }
        return null;
    }

    /// <summary>Add "self" + parameter data pins to a call node, wiring each to its data subgraph.</summary>
    /// <summary>Engine-frozen Kismet signatures (Kismet libraries never change these): real pin names
    /// + types for engine calls, which have no UFunction in the pak. Keyed "Class.Func" (FuncPkg must be
    /// /Script/Engine). Real names let the editor reconcile pins with the live signature; without them
    /// every engine-call pin stays ArgN/wildcard ("undetermined type" compile errors).</summary>
    private sealed record EngineSig(string[] Names, string[] Cats, string? RetCat, byte[]? Conts = null,
        string? RetObj = null, IReadOnlyList<(string Name, string Cat)>? Outs = null, string[]? Objs = null);
    private static readonly Dictionary<string, EngineSig> EngineSigs = new(StringComparer.Ordinal)
    {
        ["KismetSystemLibrary.PrintString"] = new(["WorldContextObject", "InString", "bPrintToScreen", "bPrintToLog", "TextColor", "Duration", "Key"],
            ["object", "string", "bool", "bool", "struct", "float", "name"], null),
        ["KismetSystemLibrary.PrintText"] = new(["WorldContextObject", "InText", "bPrintToScreen", "bPrintToLog", "TextColor", "Duration", "Key"],
            ["object", "text", "bool", "bool", "struct", "float", "name"], null),
        ["KismetSystemLibrary.IsValid"] = new(["Object"], ["object"], "bool"),
        ["KismetSystemLibrary.IsValidClass"] = new(["Class"], ["object"], "bool"),
        ["KismetSystemLibrary.GetObjectName"] = new(["Object"], ["object"], "string"),
        ["KismetSystemLibrary.IsDedicatedServer"] = new(["WorldContextObject"], ["object"], "bool"),
        ["KismetSystemLibrary.IsServer"] = new(["WorldContextObject"], ["object"], "bool"),
        ["KismetSystemLibrary.IsStandalone"] = new(["WorldContextObject"], ["object"], "bool"),
        ["KismetSystemLibrary.GetGameTimeInSeconds"] = new(["WorldContextObject"], ["object"], "float"),
        ["KismetSystemLibrary.GetPlayerController"] = new(["WorldContextObject", "PlayerIndex"], ["object", "int"], "object"),
        ["KismetSystemLibrary.GetPlayerPawn"] = new(["WorldContextObject", "PlayerIndex"], ["object", "int"], "object"),
        ["KismetMathLibrary.NotEqual_ByteByte"] = new(["A", "B"], ["byte", "byte"], "bool"),
        ["KismetMathLibrary.EqualEqual_ByteByte"] = new(["A", "B"], ["byte", "byte"], "bool"),
        ["KismetMathLibrary.NotEqual_IntInt"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.EqualEqual_IntInt"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.Greater_IntInt"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.GreaterEqual_IntInt"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.Less_IntInt"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.LessEqual_IntInt"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.NotEqual_Int64Int64"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.EqualEqual_Int64Int64"] = new(["A", "B"], ["int", "int"], "bool"),
        ["KismetMathLibrary.Add_IntInt"] = new(["A", "B"], ["int", "int"], "int"),
        ["KismetMathLibrary.Subtract_IntInt"] = new(["A", "B"], ["int", "int"], "int"),
        ["KismetMathLibrary.Multiply_IntInt"] = new(["A", "B"], ["int", "int"], "int"),
        ["KismetMathLibrary.Divide_IntInt"] = new(["A", "B"], ["int", "int"], "int"),
        ["KismetMathLibrary.Percent_IntInt"] = new(["A", "B"], ["int", "int"], "int"),
        ["KismetMathLibrary.NotEqual_FloatFloat"] = new(["A", "B"], ["float", "float"], "bool"),
        ["KismetMathLibrary.EqualEqual_FloatFloat"] = new(["A", "B"], ["float", "float"], "bool"),
        ["KismetMathLibrary.Greater_FloatFloat"] = new(["A", "B"], ["float", "float"], "bool"),
        ["KismetMathLibrary.Less_FloatFloat"] = new(["A", "B"], ["float", "float"], "bool"),
        ["KismetMathLibrary.Add_FloatFloat"] = new(["A", "B"], ["float", "float"], "float"),
        ["KismetMathLibrary.Subtract_FloatFloat"] = new(["A", "B"], ["float", "float"], "float"),
        ["KismetMathLibrary.Multiply_FloatFloat"] = new(["A", "B"], ["float", "float"], "float"),
        ["KismetMathLibrary.Divide_FloatFloat"] = new(["A", "B"], ["float", "float"], "float"),
        ["KismetMathLibrary.NotEqual_BoolBool"] = new(["A", "B"], ["bool", "bool"], "bool"),
        ["KismetMathLibrary.EqualEqual_BoolBool"] = new(["A", "B"], ["bool", "bool"], "bool"),
        ["KismetMathLibrary.BooleanAND"] = new(["A", "B"], ["bool", "bool"], "bool"),
        ["KismetMathLibrary.BooleanOR"] = new(["A", "B"], ["bool", "bool"], "bool"),
        ["KismetMathLibrary.BooleanXOR"] = new(["A", "B"], ["bool", "bool"], "bool"),
        ["KismetMathLibrary.BooleanNOT"] = new(["A"], ["bool"], "bool"),
        ["KismetMathLibrary.NotEqual_ObjectObject"] = new(["A", "B"], ["object", "object"], "bool"),
        ["KismetMathLibrary.EqualEqual_ObjectObject"] = new(["A", "B"], ["object", "object"], "bool"),
        ["KismetMathLibrary.NotEqual_StringString"] = new(["A", "B"], ["string", "string"], "bool"),
        ["KismetMathLibrary.EqualEqual_StringString"] = new(["A", "B"], ["string", "string"], "bool"),
        ["KismetMathLibrary.Concat_StrStr"] = new(["A", "B"], ["string", "string"], "string"),
        ["KismetMathLibrary.NotEqual_NameName"] = new(["A", "B"], ["name", "name"], "bool"),
        ["KismetMathLibrary.EqualEqual_NameName"] = new(["A", "B"], ["name", "name"], "bool"),
        ["KismetMathLibrary.SelectInt"] = new(["A", "B", "bPickA"], ["int", "int", "bool"], "int"),
        ["KismetMathLibrary.SelectFloat"] = new(["A", "B", "bPickA"], ["float", "float", "bool"], "float"),
        ["KismetMathLibrary.SelectBool"] = new(["A", "B", "bPickA"], ["bool", "bool", "bool"], "bool"),
        ["KismetMathLibrary.SelectString"] = new(["A", "B", "bPickA"], ["string", "string", "bool"], "string"),
        ["KismetMathLibrary.ClampInt"] = new(["Value", "Min", "Max"], ["int", "int", "int"], "int"),
        ["KismetMathLibrary.ClampFloat"] = new(["Value", "Min", "Max"], ["float", "float", "float"], "float"),
        ["KismetMathLibrary.Lerp"] = new(["A", "B", "Alpha"], ["float", "float", "float"], "float"),
        ["KismetMathLibrary.RandomInteger"] = new(["Max"], ["int"], "int"),
        ["KismetMathLibrary.RandomFloat"] = new([], [], "float"),
        ["KismetMathLibrary.MakeVector"] = new(["X", "Y", "Z"], ["float", "float", "float"], "struct"),
        ["KismetMathLibrary.MakeRotator"] = new(["Pitch", "Yaw", "Roll"], ["float", "float", "float"], "struct"),
        ["KismetMathLibrary.Add_VectorVector"] = new(["A", "B"], ["struct", "struct"], "struct"),
        ["KismetMathLibrary.Subtract_VectorVector"] = new(["A", "B"], ["struct", "struct"], "struct"),
        ["KismetMathLibrary.EqualEqual_VectorVector"] = new(["A", "B"], ["struct", "struct"], "bool"),
        ["KismetMathLibrary.VSize"] = new(["A"], ["struct"], "float", null, null, null,
            ["/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.Dot_VectorVector"] = new(["A", "B"], ["struct", "struct"], "float", null, null, null,
            ["/Script/CoreUObject.Vector", "/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.Cross_VectorVector"] = new(["A", "B"], ["struct", "struct"], "struct", null, "/Script/CoreUObject.Vector",
            null, ["/Script/CoreUObject.Vector", "/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.Subtract_DoubleDouble"] = new(["A", "B"], ["double", "double"], "double"),
        ["KismetMathLibrary.Add_DoubleDouble"] = new(["A", "B"], ["double", "double"], "double"),
        ["KismetMathLibrary.Multiply_DoubleDouble"] = new(["A", "B"], ["double", "double"], "double"),
        ["KismetMathLibrary.Divide_DoubleDouble"] = new(["A", "B"], ["double", "double"], "double"),
        ["KismetMathLibrary.NotEqual_DoubleDouble"] = new(["A", "B"], ["double", "double"], "bool"),
        ["KismetMathLibrary.EqualEqual_DoubleDouble"] = new(["A", "B"], ["double", "double"], "bool"),
        ["KismetMathLibrary.Greater_DoubleDouble"] = new(["A", "B"], ["double", "double"], "bool"),
        ["KismetMathLibrary.GreaterEqual_DoubleDouble"] = new(["A", "B"], ["double", "double"], "bool"),
        ["KismetMathLibrary.Less_DoubleDouble"] = new(["A", "B"], ["double", "double"], "bool"),
        ["KismetMathLibrary.LessEqual_DoubleDouble"] = new(["A", "B"], ["double", "double"], "bool"),
        ["GameplayStatics.ObjectIsA"] = new(["Object", "Class"], ["object", "object"], "bool"),
        ["KismetMathLibrary.Dist"] = new(["A", "B"], ["struct", "struct"], "float", null, null, null,
            ["/Script/CoreUObject.Vector", "/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.NegateVector"] = new(["A"], ["struct"], "struct", null, "/Script/CoreUObject.Vector",
            null, ["/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.InverseTransformLocation"] = new(["T", "Location"], ["struct", "struct"], "struct", null, "/Script/CoreUObject.Vector",
            null, ["/Script/CoreUObject.Transform", "/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.TransformLocation"] = new(["T", "Location"], ["struct", "struct"], "struct", null, "/Script/CoreUObject.Vector",
            null, ["/Script/CoreUObject.Transform", "/Script/CoreUObject.Vector"]),
        ["KismetMathLibrary.Normalize"] = new(["A"], ["struct"], "struct", null, "/Script/CoreUObject.Vector",
            null, ["/Script/CoreUObject.Vector"]),
        ["Actor.GetActorForwardVector"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["Actor.GetActorRightVector"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["Actor.GetActorUpVector"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["Actor.GetActorLocation"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["Actor.GetActorRotation"] = new([], [], "struct", null, "/Script/CoreUObject.Rotator"),
        ["Actor.GetActorScale3D"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["Actor.GetActorTransform"] = new([], [], "struct", null, "/Script/CoreUObject.Transform"),
        ["SceneComponent.GetComponentTransform"] = new([], [], "struct", null, "/Script/CoreUObject.Transform"),
        ["SceneComponent.GetComponentLocation"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["SceneComponent.GetComponentRotation"] = new([], [], "struct", null, "/Script/CoreUObject.Rotator"),
        ["SceneComponent.GetComponentScale"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["SceneComponent.GetForwardVector"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["SceneComponent.GetRightVector"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["SceneComponent.GetUpVector"] = new([], [], "struct", null, "/Script/CoreUObject.Vector"),
        ["KismetMathLibrary.BreakVector"] = new(["InVec"], ["struct"], null, null, null,
            [("X", "float"), ("Y", "float"), ("Z", "float")]),
        ["KismetMathLibrary.BreakRotator"] = new(["InRot"], ["struct"], null, null, null,
            [("Pitch", "float"), ("Yaw", "float"), ("Roll", "float")]),
        ["KismetMathLibrary.BreakTransform"] = new(["InTransform"], ["struct"], null, null, null,
            [("Location", "struct"), ("Rotation", "struct"), ("Scale", "struct")]),
        ["KismetMathLibrary.BreakHitResult"] = new(["Hit"], ["struct"], null, null, null,
            [("bBlockingHit", "bool"), ("bInitialOverlap", "bool"), ("Time", "float"), ("Distance", "float"),
             ("Location", "struct"), ("ImpactPoint", "struct"), ("Normal", "struct"), ("ImpactNormal", "struct"),
             ("HitActor", "object"), ("HitComponent", "object"), ("HitBoneName", "name")]),
        ["KismetSystemLibrary.IsAChildOfClass"] = new(["Object", "Class"], ["object", "object"], "bool"),
        ["KismetStringLibrary.Len"] = new(["S"], ["string"], "int"),
        ["KismetStringLibrary.Conv_IntToString"] = new(["InInt"], ["int"], "string"),
        ["KismetStringLibrary.Conv_FloatToString"] = new(["InFloat"], ["float"], "string"),
        ["KismetStringLibrary.Conv_BoolToString"] = new(["InBool"], ["bool"], "string"),
        ["KismetStringLibrary.Conv_ByteToString"] = new(["InByte"], ["byte"], "string"),
        ["KismetStringLibrary.Conv_NameToString"] = new(["InName"], ["name"], "string"),
        ["KismetStringLibrary.Conv_ObjectToString"] = new(["InObject"], ["object"], "string"),
        ["KismetTextLibrary.EqualEqual_TextText"] = new(["A", "B"], ["text", "text"], "bool"),
        ["KismetTextLibrary.NotEqual_TextText"] = new(["A", "B"], ["text", "text"], "bool"),
        ["KismetArrayLibrary.Array_Length"] = new(["TargetArray"], ["wildcard"], "int", new byte[] { 1 }),
    };

    /// <summary>Type of a CallFunc_ temp from its producer's engine-table signature:
    /// CallFunc_<F>_<R> takes <F>'s return (or Break member <R>) type. Null when unknown.</summary>
    internal static (string Cat, string? Obj)? EngineTempType(string tempName)
    {
        if (!tempName.StartsWith("CallFunc_", StringComparison.Ordinal)) return null;
        var rest = tempName["CallFunc_".Length..];
        foreach (var kv in EngineSigs)
        {
            var dot = kv.Key.LastIndexOf('.');
            var f = dot >= 0 ? kv.Key.Substring(dot + 1) : kv.Key;
            if (rest != f && !rest.StartsWith(f + "_", StringComparison.Ordinal)) continue;
            var es = kv.Value;
            var r = rest.Length > f.Length ? rest.Substring(f.Length + 1) : "";
            if (es.Outs != null)
                foreach (var o in es.Outs)
                    if (o.Name == r) return (o.Cat, null);
            if (es.RetCat != null) return (es.RetCat, es.RetObj);
            return null;
        }
        return null;
    }

    private static void AddCallDataPins(FuncCtx ctx, GraphNode node, KismetExpression expr, bool pure)
    {
        var g = ctx.G;
        // Member-access target (EX_Context.ObjectExpression) -> "self" input. A "self" pin is always
        // present on editor call nodes (even static ones); wire it when the target is recoverable.
        KismetExpression? selfExpr = expr switch
        {
            EX_Context c => c.ObjectExpression,
            EX_InterfaceContext ic => ic.InterfaceValue,
            _ => null,
        };
        var selfPin = new GraphPin { Name = "self", Category = "object", Direction = 0 };
        node.Inputs.Add(selfPin);
        if (selfExpr != null && !IsLiteral(selfExpr))
        {
            var src = BuildData(ctx, selfExpr);
            if (src is { } s) node.DataLinks["self"] = s;
        }

        var parameters = CallParameters(expr);
        var names = ResolveParamNames(ctx, node);
        // Resolved sibling signature: real types per parameter (index-aligned with names).
        var sigProps = ResolveSignatureProps(ctx, node).Where(p => p.Name.Text != "ReturnValue").ToList();
        // Engine-frozen signature (real names+types for pak-absent engine calls).
        EngineSig? esig = null;
        if (node.FuncPkg == "/Script/Engine" && !string.IsNullOrEmpty(node.FuncClass) && !string.IsNullOrEmpty(node.FuncName))
            EngineSigs.TryGetValue(node.FuncClass + "." + node.FuncName, out esig);
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            string pinName;
            string pinCat;
            byte pinCont = 0;
            if (esig != null && i < esig.Names.Length)
            {
                pinName = esig.Names[i];
                pinCat = i < esig.Cats.Length ? esig.Cats[i] : PinCategoryOf(p);
                if (esig.Conts != null && i < esig.Conts.Length) pinCont = esig.Conts[i];
            }
            else
            {
                pinName = i < names.Count ? names[i] : $"Arg{i}";
                pinCat = PinCategoryOf(p);
            }
            var pin = new GraphPin { Name = pinName, Category = pinCat, Direction = 0, Container = pinCont };
            if (esig?.Objs != null && i < esig.Objs.Length && !string.IsNullOrEmpty(esig.Objs[i]))
                pin.SubCategoryObjPath = esig.Objs[i];
            if (pin.Category == "wildcard" && i < sigProps.Count)
            {
                var (scat, ssub, sobj, scont) = PinTypeDetails(sigProps[i]);
                pin.Category = scat; pin.SubCategory = ssub; pin.SubCategoryObjPath = sobj; pin.Container = scont;
            }
            node.Inputs.Add(pin);
            var src = BuildData(ctx, p);
            if (src is { } s)
            {
                node.DataLinks[pinName] = s;
                var producer = NodeOf(g, s.Node);
                var outPin = producer?.Outputs.FirstOrDefault(o => o.Name == s.Pin);
                if (outPin != null && pin.Category == "wildcard") pin.Category = outPin.Category;
            }
            else
            {
                pin.DefaultValue = LiteralDefault(p) ?? "";
            }
        }

        // ReturnValue output (real name when the callee signature resolves; omitted when the
        // callee is known-void, mirroring editor nodes — an extra pin would just be pruned on load).
        // Engine multi-outs (Break*) become output pins even when there is no return value.
        if (CalleeIsKnownVoid(ctx, node)) return;
        if (esig != null && esig.RetCat is null && esig.Outs is null) return;   // engine-frozen void: no pins
        if (esig?.Outs != null)
            foreach (var (oname, ocat) in esig.Outs)
                node.Outputs.Add(new GraphPin { Name = oname, Category = ocat, Direction = 1 });
        if (esig != null && esig.RetCat is null) return;
        var retName = ResolveReturnName(ctx, node) ?? "ReturnValue";
        var retCat = GuessReturnCategory(node);
        string? retObj = esig?.RetObj;
        if (retCat == "wildcard")
        {
            if (esig?.RetCat != null) retCat = esig.RetCat;
            else
            {
                var retProp = ResolveSignatureProps(ctx, node).FirstOrDefault(p => p.Name.Text == "ReturnValue");
                if (retProp != null) retCat = PinCategoryOfProperty(retProp);
            }
        }
        node.Outputs.Add(new GraphPin { Name = retName, Category = retCat, Direction = 1,
            SubCategoryObjPath = retCat == "struct" ? retObj : null });
    }

    private static bool CalleeIsKnownVoid(FuncCtx ctx, GraphNode node)
    {
        try
        {
            var uf = ResolveCallee(ctx, node);
            if (uf?.ChildProperties is null) return false;
            // A resolved signature with no ReturnValue child is void. Unresolved callees keep the pin.
            foreach (var field in uf.ChildProperties)
                if (field.Name.Text == "ReturnValue") return false;
            return true;
        }
        catch { return false; }
    }

    #endregion

    #region call / type helpers

    private static KismetExpression[] CallParameters(KismetExpression expr) => expr switch
    {
        EX_FinalFunction f => f.Parameters ?? Array.Empty<KismetExpression>(),
        EX_VirtualFunction v => v.Parameters ?? Array.Empty<KismetExpression>(),
        EX_CallMulticastDelegate d => d.Parameters ?? Array.Empty<KismetExpression>(),
        _ => Array.Empty<KismetExpression>(),
    };

    private static bool TryGetCallRef(KismetExpression expr, out string fref, out string pkg, out string cls, out string func)
    {
        fref = ""; pkg = ""; cls = ""; func = "";
        switch (expr)
        {
            case EX_FinalFunction f when f.StackNode != null:
            {
                var path = f.StackNode.ResolvedObject?.GetPathName() ?? f.StackNode.Name;
                if (string.IsNullOrEmpty(path) || path == "None") return false;
                (pkg, cls, func) = SplitFunctionRef(path);
                fref = path;
                return true;
            }
            case EX_VirtualFunction v:
            {
                func = v.VirtualFunctionName.Text;
                if (string.IsNullOrEmpty(func) || func == "None") return false;
                fref = func;
                return true;
            }
            case EX_CallMulticastDelegate d when d.StackNode != null:
            {
                var path = d.StackNode.ResolvedObject?.GetPathName() ?? d.StackNode.Name;
                if (string.IsNullOrEmpty(path) || path == "None") return false;
                (pkg, cls, func) = SplitFunctionRef(path);
                fref = path;
                return true;
            }
            default:
                return false;
        }
    }

    internal static (string pkg, string cls, string func) SplitFunctionRef(string path)
    {
        var colon = path.LastIndexOf(':');
        var left = colon >= 0 ? path[..colon] : path;
        var fn = colon >= 0 ? path[(colon + 1)..] : path;
        var dot = left.LastIndexOf('.');
        if (dot < 0) return ("", "", fn);
        return (left[..dot], left[(dot + 1)..], fn);
    }

    private static bool IsDelegateCall(KismetExpression expr, string func) =>
        expr.Token is EExprToken.EX_BindDelegate or EExprToken.EX_AddMulticastDelegate
            or EExprToken.EX_ClearMulticastDelegate or EExprToken.EX_RemoveMulticastDelegate
            or EExprToken.EX_CallMulticastDelegate
        || func.EndsWith("__DelegateSignature", StringComparison.Ordinal);

    /// <summary>Pure (exec-less) when the callee UFunction carries FUNC_BlueprintPure, else a
    /// conservative heuristic (math/string pure except Random*).</summary>
    private static bool IsPureCall(KismetExpression expr, string pkg, string cls, string func)
    {
        FPackageIndex? stackNode = expr switch
        {
            EX_FinalFunction f => f.StackNode,
            EX_CallMulticastDelegate d => d.StackNode,
            _ => null,
        };
        try
        {
            if (stackNode?.ResolvedObject != null
                && stackNode.ResolvedObject.TryLoad<UFunction>(out var uf)
                && uf != null)
                return uf.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure);
        }
        catch { }
        if (cls.EndsWith("MathLibrary", StringComparison.Ordinal)
            || cls.EndsWith("StringLibrary", StringComparison.Ordinal)
            || cls.EndsWith("TextLibrary", StringComparison.Ordinal)
            || cls.EndsWith("GuidLibrary", StringComparison.Ordinal)
            || cls.EndsWith("NameLibrary", StringComparison.Ordinal))
            return !func.StartsWith("Random", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>Real parameter names when the callee UFunction resolves (ChildProperties minus ReturnValue).</summary>
    private static List<string> ResolveParamNames(FuncCtx ctx, GraphNode node)
    {
        var names = new List<string>();
        try
        {
            var uf = ResolveCallee(ctx, node);
            if (uf?.ChildProperties is null) return names;
            foreach (var field in uf.ChildProperties)
            {
                if (field is not FProperty) continue;
                var n = field.Name.Text;
                if (n == "ReturnValue") continue;
                names.Add(n);
            }
        }
        catch { }
        return names;
    }

    private static string? ResolveReturnName(FuncCtx ctx, GraphNode node)
    {
        try
        {
            var uf = ResolveCallee(ctx, node);
            return uf?.ChildProperties?.FirstOrDefault(f => f.Name.Text == "ReturnValue")?.Name.Text;
        }
        catch { return null; }
    }

    /// <summary>Resolved signature properties (params in order, ReturnValue last-or-wherever) for a call
    /// node whose callee is a sibling game function. Empty when the callee doesn't resolve.</summary>
    private static List<CUE4Parse.UE4.Objects.UObject.FProperty> ResolveSignatureProps(FuncCtx ctx, GraphNode node)
    {
        var list = new List<CUE4Parse.UE4.Objects.UObject.FProperty>();
        try
        {
            var uf = ResolveCallee(ctx, node);
            if (uf?.ChildProperties is null) return list;
            foreach (var field in uf.ChildProperties)
                if (field is CUE4Parse.UE4.Objects.UObject.FProperty fp) list.Add(fp);
        }
        catch { }
        return list;
    }

    /// <summary>Pin category from a resolved signature property (CLR type name). Matches VarCategoryOf
    /// plus enum support (cooked temps like CallFunc_X_OutExecs are FEnumProperty).</summary>
    internal static string PinCategoryOfProperty(CUE4Parse.UE4.Objects.UObject.FProperty prop)
        => PinTypeDetails(prop).Cat;

    /// <summary>Full pin type from a resolved signature property: category, sub-category, engine-only
    /// sub-category object path (game paths stay null — their imports would dangle), container.</summary>
    internal static (string Cat, string Sub, string? ObjPath, byte Cont) PinTypeDetails(CUE4Parse.UE4.Objects.UObject.FProperty prop)
    {
        try
        {
            // CUE4Parse exposes FProperty payloads as properties OR fields depending on the subclass.
            static object? Member(object o, string name)
            {
                try
                {
                    var pv = o.GetType().GetProperty(name,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(o);
                    if (pv != null) return pv;
                }
                catch { }
                try
                {
                    return o.GetType().GetField(name,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(o);
                }
                catch { return null; }
            }
            string? PackagePath(object? idx)
            {
                try
                {
                    if (idx is CUE4Parse.UE4.Objects.UObject.FPackageIndex pi)
                    {
                        var p = pi.ResolvedObject?.GetPathName();
                        if (!string.IsNullOrEmpty(p) && p != "None") return p;
                    }
                    return idx?.ToString();
                }
                catch { return null; }
            }
            string? EnginePath(object? idx)
            {
                var p = PackagePath(idx);
                if (string.IsNullOrEmpty(p) || p == "None") return null;
                if (!p.StartsWith("/Script/Engine.", StringComparison.Ordinal)
                    && !p.StartsWith("/Script/CoreUObject.", StringComparison.Ordinal)) return null;
                return p;
            }
            string? ShortName(string? path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                var d = path.LastIndexOf('.');
                return d >= 0 ? path.Substring(d + 1) : path;
            }
            switch (prop.GetType().Name)
            {
                case "FBoolProperty": return ("bool", "None", null, 0);
                case "FIntProperty" or "FInt16Property" or "FInt8Property" or "FInt64Property"
                    or "FUInt16Property" or "FUInt32Property" or "FUInt64Property": return ("int", "None", null, 0);
                case "FFloatProperty" or "FDoubleProperty": return ("float", "None", null, 0);
                case "FStrProperty" or "FUtf8StrProperty": return ("string", "None", null, 0);
                case "FNameProperty": return ("name", "None", null, 0);
                case "FTextProperty": return ("text", "None", null, 0);
                case "FByteProperty":
                {
                    var enName = ShortName(PackagePath(Member(prop, "Enum")));
                    if (!string.IsNullOrEmpty(enName) && enName != "None") return ("byte", enName, null, 0);
                    return ("int", "None", null, 0);
                }
                case "FEnumProperty":
                {
                    var enName2 = ShortName(PackagePath(Member(prop, "Enum")));
                    if (!string.IsNullOrEmpty(enName2) && enName2 != "None") return ("byte", enName2, null, 0);
                    return ("byte", "None", null, 0);
                }
                case "FObjectProperty" or "FClassProperty" or "FSoftObjectProperty" or "FSoftClassProperty"
                    or "FWeakObjectProperty" or "FInterfaceProperty" or "FLazyObjectProperty":
                {
                    var cls = Member(prop, "PropertyClass") ?? Member(prop, "MetaClass");
                    return ("object", "None", EnginePath(cls), 0);
                }
                case "FStructProperty":
                    return ("struct", "None", EnginePath(Member(prop, "Struct")), 0);
                case "FArrayProperty" or "FSetProperty" or "FMapProperty":
                {
                    var inner = Member(prop, "Inner") as CUE4Parse.UE4.Objects.UObject.FProperty;
                    if (inner != null)
                    {
                        var (ic, isub, iobj, _) = PinTypeDetails(inner);
                        byte cont = prop.GetType().Name == "FArrayProperty" ? (byte)1
                            : prop.GetType().Name == "FSetProperty" ? (byte)2 : (byte)3;
                        return (ic, isub, iobj, cont);
                    }
                    return ("wildcard", "None", null, prop.GetType().Name == "FArrayProperty" ? (byte)1
                        : prop.GetType().Name == "FSetProperty" ? (byte)2 : (byte)3);
                }
                case "FDelegateProperty" or "FMulticastInlineDelegateProperty" or "FMulticastSparseDelegateProperty":
                    return ("delegate", "None", null, 0);
                default: return ("wildcard", "None", null, 0);
            }
        }
        catch { return ("wildcard", "None", null, 0); }
    }

    private static UFunction? ResolveCallee(FuncCtx ctx, GraphNode node)
    {
        // Sibling game functions (same package, unambiguous name) carry real signatures. Engine
        // functions aren't in the pak, so those keep ArgN/ReturnValue naming (see class notes).
        if (ctx.Callees != null && !string.IsNullOrEmpty(node.FuncName)
            && ctx.Callees.TryGetValue(node.FuncName, out var uf))
            return uf;
        return null;
    }

    /// <summary>Best-effort pin category for an r-value expression.</summary>
    internal static string PinCategoryOf(KismetExpression? expr)
    {
        if (expr is null) return "wildcard";
        switch (expr.Token)
        {
            case EExprToken.EX_True:
            case EExprToken.EX_False:
                return "bool";
            case EExprToken.EX_IntConst:
            case EExprToken.EX_IntConstByte:
            case EExprToken.EX_IntZero:
            case EExprToken.EX_IntOne:
            case EExprToken.EX_Int64Const:
            case EExprToken.EX_UInt64Const:
            case EExprToken.EX_ByteConst:
            case EExprToken.EX_SkipOffsetConst:
                return "int";
            case EExprToken.EX_FloatConst:
            case EExprToken.EX_DoubleConst:
                return "float";
            case EExprToken.EX_StringConst:
            case EExprToken.EX_UnicodeStringConst:
                return "string";
            case EExprToken.EX_NameConst:
                return "name";
            case EExprToken.EX_TextConst:
                return "text";
            case EExprToken.EX_ObjectConst:
            case EExprToken.EX_SoftObjectConst:
            case EExprToken.EX_NoObject:
            case EExprToken.EX_Self:
                return "object";
            case EExprToken.EX_VectorConst:
            case EExprToken.EX_Vector3fConst:
            case EExprToken.EX_RotationConst:
            case EExprToken.EX_TransformConst:
            case EExprToken.EX_StructConst:
                return "struct";
            case EExprToken.EX_ArrayConst:
            case EExprToken.EX_EndArrayConst:
                return "wildcard";
        }
        if (expr is EX_VariableBase v) return VarCategoryOf(v.Variable);
        if (expr is EX_LetBase l) return PinCategoryOf(l.Assignment);
        return "wildcard";
    }

    private static string GuessReturnCategory(GraphNode node) =>
        node.FuncName.StartsWith("Get", StringComparison.Ordinal) ? "wildcard" : "wildcard";

    private static string VarCategoryOf(FKismetPropertyPointer ptr)
    {
        try
        {
            var type = ptr.Old?.ResolvedObject?.Class?.Name.Text;
            return type switch
            {
                "BoolProperty" => "bool",
                "IntProperty" or "Int64Property" or "Int16Property" or "Int8Property"
                    or "UInt16Property" or "UInt32Property" or "UInt64Property" or "ByteProperty" => "int",
                "FloatProperty" or "DoubleProperty" => "float",
                "StrProperty" or "Utf8StrProperty" => "string",
                "NameProperty" => "name",
                "TextProperty" => "text",
                "ObjectProperty" or "ClassProperty" or "SoftObjectProperty" or "SoftClassProperty"
                    or "WeakObjectProperty" or "InterfaceProperty" or "LazyObjectProperty" => "object",
                "StructProperty" => "struct",
                "EnumProperty" => "byte",
                "ArrayProperty" or "SetProperty" or "MapProperty" => "wildcard",
                _ => "wildcard",
            };
        }
        catch { return "wildcard"; }
    }

    private static void ClassifyVarPin(GraphPin? inPin, GraphPin outPin, FKismetPropertyPointer ptr)
    {
        var cat = VarCategoryOf(ptr);
        if (inPin != null) inPin.Category = cat;
        outPin.Category = cat;
        try
        {
            var ro = ptr.Old?.ResolvedObject;
            var path = ro?.GetPathName();
            if (!string.IsNullOrEmpty(path) && path != "None"
                && (cat is "object" or "struct") && path.StartsWith("/Script/", StringComparison.Ordinal))
            {
                if (inPin != null) inPin.SubCategoryObjPath = path;
                outPin.SubCategoryObjPath = path;
            }
        }
        catch { }
    }

    private static string? VarNameOf(KismetExpression? expr)
    {
        if (expr is EX_VariableBase v)
        {
            try
            {
                var s = v.Variable.ToString();
                if (!string.IsNullOrEmpty(s) && s != "None") return s;
            }
            catch { }
            try
            {
                var n = v.Variable.Old?.ResolvedObject?.Name.Text;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
        }
        return null;
    }

    private static bool IsLiteral(KismetExpression expr) => expr.Token switch
    {
        EExprToken.EX_IntConst or EExprToken.EX_IntConstByte or EExprToken.EX_IntZero or EExprToken.EX_IntOne
            or EExprToken.EX_Int64Const or EExprToken.EX_UInt64Const or EExprToken.EX_ByteConst
            or EExprToken.EX_FloatConst or EExprToken.EX_DoubleConst or EExprToken.EX_StringConst
            or EExprToken.EX_UnicodeStringConst or EExprToken.EX_NameConst or EExprToken.EX_TextConst
            or EExprToken.EX_True or EExprToken.EX_False or EExprToken.EX_NoObject
            or EExprToken.EX_SkipOffsetConst or EExprToken.EX_Nothing or EExprToken.EX_EndFunctionParms => true,
        _ => false,
    };

    private static string? LiteralOf(KismetExpression? expr)
    {
        if (expr is null) return null;
        try
        {
            var t = expr.GetType();
            var valueProp = t.GetProperty("Value");
            var v = valueProp?.GetValue(expr);
            return v?.ToString();
        }
        catch { return null; }
    }

    /// <summary>K2 DefaultValue text for a literal (bool/int/float/string/name/text/byte).</summary>
    internal static string? LiteralDefault(KismetExpression? expr)
    {
        if (expr is null) return null;
        switch (expr.Token)
        {
            case EExprToken.EX_True: return "true";
            case EExprToken.EX_False:
            case EExprToken.EX_NoObject: return "";
            case EExprToken.EX_IntZero: return "0";
            case EExprToken.EX_IntOne: return "1";
            default:
                return LiteralOf(expr);
        }
    }

    private static IEnumerable<KismetExpression> ChildExpressions(KismetExpression expr)
    {
        foreach (var field in expr.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            object? value;
            try { value = field.GetValue(expr); }
            catch { continue; }
            if (value is KismetExpression child) yield return child;
            else if (value is KismetExpression[] arr)
                foreach (var c in arr)
                    if (c != null) yield return c;
        }
    }

    private static GraphPin ExecOut(string name) =>
        new() { Name = name, Category = "exec", SubCategory = "None", Direction = 1 };

    #endregion
}

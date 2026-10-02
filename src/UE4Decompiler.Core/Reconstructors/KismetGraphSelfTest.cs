using CUE4Parse.UE4.Kismet;
using Serilog;
using UE4Decompiler.Output.Writer;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// In-process smoke tests for BP graph recovery (no pak/assets needed). Run via
/// <c>--self-test-graph</c>: exercises the decompiler's pure helpers plus a full
/// <see cref="PinSerializer"/> → <see cref="NodePayloadWalker"/> pin round-trip, so a green run
/// proves the graph emission byte layout matches what the payload walker (and the editor) reads.
/// </summary>
public static class KismetGraphSelfTest
{
    public static bool Run()
    {
        int pass = 0, fail = 0;
        void Check(bool ok, string name)
        {
            if (ok) { pass++; Log.Information("  [PASS] {N}", name); }
            else { fail++; Log.Warning("  [FAIL] {N}", name); }
        }

        Log.Information("Kismet graph self-test:");

        // 1. Empty input -> empty graphs (never throws).
        try
        {
            var graphs = KismetGraphDecompiler.DecompileFunctions(
                Enumerable.Empty<CUE4Parse.UE4.Objects.UObject.UFunction>());
            Check(graphs.Count == 0, "empty decompile");
        }
        catch (Exception ex) { Check(false, "empty decompile threw: " + ex.Message); }

        // 2. Function-ref splitting.
        Check(KismetGraphDecompiler.SplitFunctionRef("/Script/Engine.KismetSystemLibrary:PrintString")
            == ("/Script/Engine", "KismetSystemLibrary", "PrintString"), "SplitFunctionRef engine");
        Check(KismetGraphDecompiler.SplitFunctionRef("ReceiveBeginPlay") == ("", "", "ReceiveBeginPlay"), "SplitFunctionRef bare");

        // 3. Literal / pin-category mapping on synthetic expressions (no package needed).
        // Kismet expressions require an FKismetArchive ctor arg, so instantiate uninitialized —
        // category/literal mapping only reads Token/Value, never archive state.
        try
        {
            T Fresh<T>() where T : KismetExpression =>
                (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
            Check(KismetGraphDecompiler.PinCategoryOf(Fresh<EX_IntConst>()) == "int", "PinCategory int");
            Check(KismetGraphDecompiler.PinCategoryOf(Fresh<EX_FloatConst>()) == "float", "PinCategory float");
            Check(KismetGraphDecompiler.PinCategoryOf(Fresh<EX_StringConst>()) == "string", "PinCategory string");
            Check(KismetGraphDecompiler.PinCategoryOf(Fresh<EX_True>()) == "bool", "PinCategory bool");
            Check(KismetGraphDecompiler.PinCategoryOf(Fresh<EX_NameConst>()) == "name", "PinCategory name");
            Check(KismetGraphDecompiler.PinCategoryOf(Fresh<EX_Self>()) == "object", "PinCategory self");
            Check(KismetGraphDecompiler.LiteralDefault(Fresh<EX_True>()) == "true", "Literal true");
            Check(KismetGraphDecompiler.LiteralDefault(Fresh<EX_IntZero>()) == "0", "Literal intzero");
            Check(KismetGraphDecompiler.LiteralDefault(null) is null, "Literal null");
        }
        catch (Exception ex) { Check(false, "synthetic expr threw: " + ex.Message); }

        // 4. PinSerializer -> NodePayloadWalker round-trip (proves the emit layout parses back).
        try
        {
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            int Name(string s)
            {
                if (!names.TryGetValue(s, out var i)) names[s] = i = names.Count;
                return i;
            }
            // Seed the names the walker needs positionally is unnecessary — it only reads indices.
            byte[] bytes;
            var execId = FGuid16.NewGuid();
            var thenId = FGuid16.NewGuid();
            using (var ms = new MemoryStream())
            {
                using var w = new FArchiveWriter(ms);
                var execPin = new SynthPin { OwningNodePkg = 7, PinId = execId, PinName = "execute", Category = "exec", Direction = 0 };
                var thenPin = new SynthPin { OwningNodePkg = 7, PinId = thenId, PinName = "then", Category = "exec", Direction = 1 };
                var dataPin = new SynthPin { OwningNodePkg = 7, PinName = "Message", Category = "string", Direction = 0, DefaultValue = "hello" };
                execPin.LinkedTo.Add((8, thenId));
                new PinSerializer(w, Name).WriteOwningPins(new[] { execPin, thenPin, dataPin });
                w.Flush();
                bytes = ms.ToArray();
            }
            var pins = NodePayloadWalker.WalkPins(bytes, 0);
            Check(pins.Count == 3, "pin count round-trip");
            Check(pins[0].LinkedToCount == 1, "exec LinkedTo round-trip");
            Check(pins[2].LinkedToCount == 0, "data pin no-link round-trip");
            Check(pins[0].PinNameIdx == Name("execute") && pins[1].PinNameIdx == Name("then"), "pin names round-trip");
        }
        catch (Exception ex) { Check(false, "pin round-trip threw: " + ex.Message); }

        Log.Information("Kismet graph self-test: {P} passed, {F} failed", pass, fail);
        return fail == 0;
    }
}

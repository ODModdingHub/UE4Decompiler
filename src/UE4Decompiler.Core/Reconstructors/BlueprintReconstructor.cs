using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Best-effort recovery of Blueprint logic. Cooking lowers K2 graphs to Kismet bytecode, so a
/// byte-perfect round-trip is impossible; the goal is "opens in editor with most logic visible".
///
/// Detects <c>UBlueprint</c> / <c>UBlueprintGeneratedClass</c>, pulls each function's
/// <c>UStruct.ScriptBytecode</c>, and pattern-maps opcodes to K2 node equivalents. Unrecognized
/// statements become <c>UK2Node_Comment</c> stubs carrying the raw opcode dump, and every fallback
/// is logged as a warning.
/// </summary>
public sealed class BlueprintReconstructor
{
    private readonly bool _skip;
    private readonly bool _fullRecovery;

    public BlueprintReconstructor(bool skipBlueprints, bool fullRecovery = false)
    {
        _skip = skipBlueprints;
        _fullRecovery = fullRecovery;
    }

    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        var bpClass = asset.Exports.FirstOrDefault(e =>
            AssetParser.ClassName(e).EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase) ||
            AssetParser.ClassName(e).Equals("Blueprint", StringComparison.OrdinalIgnoreCase));

        if (bpClass is null)
            return ReconstructionResult.Failed("No Blueprint/BlueprintGeneratedClass export found");

        var className = AssetParser.ClassName(bpClass);
        var isAnim = className.StartsWith("Anim", StringComparison.OrdinalIgnoreCase);

        if (_skip)
        {
            // Emit a stub: class metadata + variable defaults only, no graph reconstruction.
            return new ReconstructionResult
            {
                Fidelity = Fidelity.Stub,
                Note = "--skip-blueprints: emitted class metadata stub, no graph reconstruction",
                Model = new { AssetType = className, bpClass.Name, Stub = true, Properties = bpClass.Properties }
            };
        }

        var functions = new List<object>();
        var stubCount = 0;
        var nodeCount = 0;

        foreach (var fn in asset.Exports.OfType<UFunction>())
        {
            var (nodes, stubs) = ReconstructFunction(fn, asset.File.Path);
            nodeCount += nodes.Count;
            stubCount += stubs;

            // Under --full-recovery, attach the normalized opcode tree the editor commandlet consumes.
            object? bytecode = _fullRecovery && fn.ScriptBytecode is { Length: > 0 }
                ? KismetWalker.Walk(fn.ScriptBytecode)
                : null;

            functions.Add(new { fn.Name, NodeCount = nodes.Count, Nodes = nodes, Bytecode = bytecode });
        }

        // Parent class + (for anim) skeleton target — the commandlet needs these to create the asset.
        var superStruct = (bpClass as UStruct)?.SuperStruct?.ResolvedObject?.GetPathName();
        var targetSkeleton = bpClass.GetOrDefault<FPackageIndex>("TargetSkeleton")?.ResolvedObject?.GetPathName();

        var model = new
        {
            AssetType = className,
            bpClass.Name,
            SuperStruct = superStruct,
            TargetSkeleton = targetSkeleton,
            IsAnimBlueprint = isAnim,
            // Export order is what the commandlet uses to resolve anim-graph LinkID -> node mapping.
            AnimGraphNodeOrder = isAnim ? asset.Exports.Select(e => e.Name).ToList() : null,
            FunctionCount = functions.Count,
            Functions = functions,
            ClassProperties = bpClass.Properties
        };

        // Anything with stub fallbacks is "Stub" fidelity overall; pure matches count as "Partial".
        var fidelity = functions.Count == 0 ? Fidelity.Stub : (stubCount > 0 ? Fidelity.Stub : Fidelity.Partial);
        Log.Information("Blueprint {Name}: {Fns} function(s), {Nodes} node(s), {Stubs} opcode stub(s)",
            bpClass.Name, functions.Count, nodeCount, stubCount);

        return new ReconstructionResult
        {
            Fidelity = fidelity,
            Note = $"{functions.Count} functions, {nodeCount} nodes reconstructed, {stubCount} stubbed",
            Model = model
        };
    }

    private (List<object> Nodes, int Stubs) ReconstructFunction(UFunction fn, string assetPath)
    {
        var nodes = new List<object>();
        var stubs = 0;

        if (fn.ScriptBytecode is not { Length: > 0 })
            return (nodes, stubs);

        foreach (var expr in fn.ScriptBytecode)
        {
            // Structural sentinels carry no graph meaning — skip rather than stub.
            if (expr.Token is EExprToken.EX_EndOfScript or EExprToken.EX_Nothing
                or EExprToken.EX_NothingInt32 or EExprToken.EX_Tracepoint or EExprToken.EX_WireTracepoint)
                continue;

            var k2 = MapToK2Node(expr.Token);
            if (k2 is null)
            {
                stubs++;
                Log.Warning("BP {Asset}:{Fn} stmt {Idx}: unmapped opcode {Token} -> UK2Node_Comment stub",
                    assetPath, fn.Name, expr.StatementIndex, expr.Token);
                nodes.Add(new
                {
                    K2Node = "UK2Node_Comment",
                    SourceOpcode = expr.Token.ToString(),
                    expr.StatementIndex,
                    RawDump = expr // serialized opcode operands for manual inspection
                });
            }
            else
            {
                nodes.Add(new { K2Node = k2, SourceOpcode = expr.Token.ToString(), expr.StatementIndex });
            }
        }
        return (nodes, stubs);
    }

    /// <summary>Pattern-match a Kismet opcode to its K2 node equivalent; null => fall back to a stub.</summary>
    private static string? MapToK2Node(EExprToken token) => token switch
    {
        EExprToken.EX_CallMath or EExprToken.EX_FinalFunction or EExprToken.EX_VirtualFunction
            or EExprToken.EX_LocalFinalFunction or EExprToken.EX_LocalVirtualFunction
            or EExprToken.EX_CallMulticastDelegate => "UK2Node_CallFunction",

        EExprToken.EX_LocalVariable or EExprToken.EX_InstanceVariable or EExprToken.EX_DefaultVariable
            or EExprToken.EX_LocalOutVariable or EExprToken.EX_ClassSparseDataVariable => "UK2Node_VariableGet",

        EExprToken.EX_Let or EExprToken.EX_LetObj or EExprToken.EX_LetBool or EExprToken.EX_LetWeakObjPtr
            or EExprToken.EX_LetValueOnPersistentFrame or EExprToken.EX_LetDelegate
            or EExprToken.EX_LetMulticastDelegate => "UK2Node_VariableSet",

        EExprToken.EX_JumpIfNot => "UK2Node_IfThenElse",
        EExprToken.EX_Jump => "UK2Node_ExecutionSequence",
        EExprToken.EX_ComputedJump => "UK2Node_Switch",
        EExprToken.EX_Return => "UK2Node_FunctionResult",
        EExprToken.EX_Context or EExprToken.EX_Context_FailSilent => "UK2Node_CallFunction", // target.member access
        EExprToken.EX_StructConst or EExprToken.EX_ArrayConst or EExprToken.EX_SetConst => "UK2Node_MakeStruct",

        _ => null
    };
}

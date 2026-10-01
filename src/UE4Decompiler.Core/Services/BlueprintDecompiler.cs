using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using UE4Decompiler.Core.Abstractions;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Decompiles Kismet bytecode into human-readable Pseudo-Blueprint source, Graphviz DOT, and Mermaid graphs (Phase 10, 11, 12).
/// </summary>
public sealed class BlueprintDecompiler : IBlueprintDecompiler
{
    public string ToPseudoBlueprint(ParsedAsset asset, string? functionName = null)
    {
        var sb = new StringBuilder();
        var bpClass = asset.Exports.FirstOrDefault(e =>
            AssetParser.ClassName(e).EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase) ||
            AssetParser.ClassName(e).Equals("Blueprint", StringComparison.OrdinalIgnoreCase));

        var name = bpClass?.Name ?? Path.GetFileNameWithoutExtension(asset.File.Path);
        var super = (bpClass as UStruct)?.SuperStruct?.ResolvedObject?.GetPathName() ?? "Object";

        sb.AppendLine($"// Decompiled Pseudo-Blueprint: {name}");
        sb.AppendLine($"// Asset Path: {asset.File.Path}");
        sb.AppendLine($"Blueprint {name} : {super}");
        sb.AppendLine("{");

        // Member Variables
        if (bpClass?.Properties != null && bpClass.Properties.Count > 0)
        {
            sb.AppendLine("    // Variables");
            foreach (var prop in bpClass.Properties)
            {
                sb.AppendLine($"    var {prop.Name.Text} : {prop.PropertyType.Text} = {prop.Tag?.GenericValue?.ToString() ?? "default"};");
            }
            sb.AppendLine();
        }

        // Functions & Events
        var functions = asset.Exports.OfType<UFunction>().ToList();
        if (!string.IsNullOrWhiteSpace(functionName))
            functions = functions.Where(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var fn in functions)
        {
            DecompileFunction(sb, fn);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void DecompileFunction(StringBuilder sb, UFunction fn)
    {
        var isEvent = fn.Name.StartsWith("Receive") || fn.Name.StartsWith("InpActEv_") || fn.Name.Equals("ExecuteUbergraph");
        var keyword = isEvent ? "Event" : "Function";

        sb.AppendLine($"    {keyword} {fn.Name}()");
        sb.AppendLine("    {");

        if (fn.ScriptBytecode == null || fn.ScriptBytecode.Length == 0)
        {
            sb.AppendLine("        // (No bytecode statements found or stripped in cooked build)");
            sb.AppendLine("    }");
            sb.AppendLine();
            return;
        }

        int indent = 2;
        foreach (var expr in fn.ScriptBytecode)
        {
            var str = FormatExpression(expr);
            if (!string.IsNullOrWhiteSpace(str))
            {
                sb.AppendLine(new string(' ', indent * 4) + str);
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string FormatExpression(KismetExpression expr)
    {
        var token = expr.Token;
        switch (token)
        {
            case EExprToken.EX_EndOfScript:
            case EExprToken.EX_Nothing:
            case EExprToken.EX_Tracepoint:
            case EExprToken.EX_WireTracepoint:
                return string.Empty;

            case EExprToken.EX_Return:
                return "return;";

            case EExprToken.EX_CallMath:
            case EExprToken.EX_FinalFunction:
            case EExprToken.EX_VirtualFunction:
                return $"CallFunction: {expr.GetType().Name} (Token: {token})";

            case EExprToken.EX_Let:
            case EExprToken.EX_LetObj:
            case EExprToken.EX_LetBool:
                return $"Assign: {token}";

            case EExprToken.EX_JumpIfNot:
                return "if (!condition) goto ...";

            case EExprToken.EX_Jump:
                return "goto ...";

            default:
                return $"Statement [0x{(byte)token:X2}] {token}";
        }
    }

    public string ToGraphvizDot(ParsedAsset asset, string? functionName = null)
    {
        var sb = new StringBuilder();
        var bpName = Path.GetFileNameWithoutExtension(asset.File.Path);
        sb.AppendLine($"digraph \"{bpName}\" {{");
        sb.AppendLine("    rankdir=LR;");
        sb.AppendLine("    node [shape=box, style=\"rounded,filled\", fillcolor=\"#2D3748\", fontcolor=white, fontname=\"Segoe UI\"];");
        sb.AppendLine("    edge [color=\"#CBD5E0\", fontcolor=\"#A0AEC0\", fontname=\"Segoe UI\"];");

        var functions = asset.Exports.OfType<UFunction>().ToList();
        if (!string.IsNullOrWhiteSpace(functionName))
            functions = functions.Where(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)).ToList();

        int nodeCounter = 0;
        foreach (var fn in functions)
        {
            var fnNodeId = $"fn_{nodeCounter++}";
            sb.AppendLine($"    \"{fnNodeId}\" [label=\"Function: {fn.Name}\", fillcolor=\"#3182CE\"];");

            if (fn.ScriptBytecode is { Length: > 0 })
            {
                string? prevNodeId = fnNodeId;
                for (int i = 0; i < Math.Min(fn.ScriptBytecode.Length, 30); i++)
                {
                    var expr = fn.ScriptBytecode[i];
                    if (expr.Token is EExprToken.EX_EndOfScript or EExprToken.EX_Nothing) continue;

                    var exprNodeId = $"node_{nodeCounter++}";
                    var label = $"{expr.Token}\\n[Offset {expr.StatementIndex}]";
                    sb.AppendLine($"    \"{exprNodeId}\" [label=\"{label}\"];");
                    sb.AppendLine($"    \"{prevNodeId}\" -> \"{exprNodeId}\" [color=\"#48BB78\", penwidth=2];");
                    prevNodeId = exprNodeId;
                }
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public string ToMermaid(ParsedAsset asset, string? functionName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        var functions = asset.Exports.OfType<UFunction>().ToList();
        if (!string.IsNullOrWhiteSpace(functionName))
            functions = functions.Where(f => f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)).ToList();

        int counter = 0;
        foreach (var fn in functions)
        {
            var fnId = $"fn_{counter++}";
            sb.AppendLine($"    {fnId}[\"Function: {fn.Name}\"]");

            if (fn.ScriptBytecode is { Length: > 0 })
            {
                var prevId = fnId;
                for (int i = 0; i < Math.Min(fn.ScriptBytecode.Length, 20); i++)
                {
                    var expr = fn.ScriptBytecode[i];
                    if (expr.Token is EExprToken.EX_EndOfScript or EExprToken.EX_Nothing) continue;

                    var curId = $"n_{counter++}";
                    sb.AppendLine($"    {curId}[\"{expr.Token} ({expr.StatementIndex})\"]");
                    sb.AppendLine($"    {prevId} --> {curId}");
                    prevId = curId;
                }
            }
        }

        return sb.ToString();
    }
}

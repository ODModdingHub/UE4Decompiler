using System.Collections;
using System.Reflection;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Converts CUE4Parse's parsed <see cref="KismetExpression"/> tree into a normalized, JSON-friendly
/// structure consumed by the editor re-import commandlet (Part 1). Rather than reimplement a reader
/// for every one of the ~100 opcode classes, it reflects over each expression's public fields and
/// emits a predictable shape:
///
///   { "Opcode": "EX_CallMath", "Offset": 5,
///     "FunctionRef": "/Script/Engine.KismetMathLibrary:Multiply_FloatFloat",
///     "Parameters": [ { "Opcode": "EX_LocalVariable", "OperandName": "Alpha", ... }, ... ] }
///
/// Nested expressions (Parameters, ObjectExpression, ContextExpression, AssignmentExpression, …)
/// are recursed into under their field name so the commandlet can rebuild K2 node wiring.
/// </summary>
public static class KismetWalker
{
    public static List<Dictionary<string, object?>> Walk(IEnumerable<KismetExpression> expressions)
        => expressions.Select(WalkOne).ToList();

    public static Dictionary<string, object?> WalkOne(KismetExpression expr)
    {
        var node = new Dictionary<string, object?>
        {
            ["Opcode"] = expr.Token.ToString(),
            ["Offset"] = expr.StatementIndex
        };

        foreach (var field in expr.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            // StatementIndex already captured as Offset.
            if (field.Name == "StatementIndex") continue;

            var value = field.GetValue(expr);
            if (value is null) continue;

            switch (value)
            {
                case KismetExpression childExpr:
                    node[field.Name] = WalkOne(childExpr);
                    break;

                case KismetExpression[] childExprs:
                    node[field.Name == "Value" ? "Elements" : field.Name] = Walk(childExprs);
                    break;

                case FKismetPropertyPointer ptr:
                    // Variable reference: surface the resolved name + best-effort type.
                    node["OperandName"] = ptr.ToString();
                    var type = ptr.Old?.ResolvedObject?.Class?.Name.Text;
                    if (type is not null) node["OperandType"] = type;
                    break;

                case FPackageIndex pkgIndex:
                    // Function / object reference (e.g. EX_FinalFunction.StackNode).
                    var path = pkgIndex.ResolvedObject?.GetPathName() ?? pkgIndex.Name;
                    if (!string.IsNullOrEmpty(path) && path != "None")
                        node[field.Name == "StackNode" ? "FunctionRef" : field.Name] = path;
                    break;

                case FName name:
                    node[field.Name == "VirtualFunctionName" ? "FunctionRef" : field.Name] = name.Text;
                    break;

                case string s:
                    node[field.Name] = s;
                    break;

                case Enum e:
                    node[field.Name] = e.ToString();
                    break;

                case IConvertible conv: // numeric / bool literals (EX_IntConst, EX_FloatConst, …)
                    node[field.Name] = conv;
                    break;

                case IEnumerable when value is not IEnumerable<byte>:
                    // Mixed arrays we don't specifically handle: stringify shallowly.
                    break;
            }
        }

        return node;
    }
}

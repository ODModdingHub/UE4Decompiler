using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Material expression graphs survive cooking, so materials are among the most faithfully
/// reconstructable assets. Walks the <c>Expressions</c> array of a <see cref="UMaterial"/>,
/// recovering each node's class, editor position, and connected input/output pins plus any
/// scalar/vector parameters. Emits a full-fidelity expression graph model.
/// </summary>
public sealed class MaterialReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            if (asset.Exports.OfType<UMaterial>().FirstOrDefault() is { } material)
                return ReconstructMaterial(material);

            // Material instances: parameters override a parent; capture overrides + parent ref.
            if (asset.Exports.OfType<UMaterialInstanceConstant>().FirstOrDefault() is { } mic)
            {
                var model = new
                {
                    AssetType = "MaterialInstanceConstant",
                    Parent = mic.Parent?.GetPathName(),
                    Properties = mic.Properties
                };
                return new ReconstructionResult { Fidelity = Fidelity.Full, Model = model, Note = "Instance parameter overrides preserved" };
            }

            return ReconstructionResult.Failed("No UMaterial/MaterialInstance export found");
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Material reconstruction error: {ex.Message}");
        }
    }

    private ReconstructionResult ReconstructMaterial(UMaterial material)
    {
        var nodes = new List<object>();
        foreach (var index in material.Expressions)
        {
            if (!index.TryLoad(out var expr) || expr is null) continue;

            // Each expression's connected pins are themselves FExpressionInput properties; we keep
            // the raw property tags so node positions (MaterialExpressionEditorX/Y) and pin links survive.
            nodes.Add(new
            {
                Class = expr.ExportType,
                expr.Name,
                EditorX = expr.GetOrDefault<int>("MaterialExpressionEditorX"),
                EditorY = expr.GetOrDefault<int>("MaterialExpressionEditorY"),
                Properties = expr.Properties
            });
        }

        var model = new
        {
            AssetType = "Material",
            material.Name,
            ExpressionCount = nodes.Count,
            Expressions = nodes,
            Properties = material.Properties // BaseColor/Metallic/etc. connection roots + material settings
        };

        Log.Information("Material {Name}: reconstructed {Count} expression node(s)", material.Name, nodes.Count);
        return new ReconstructionResult
        {
            Fidelity = nodes.Count > 0 ? Fidelity.Full : Fidelity.Partial,
            Note = $"{nodes.Count} expression nodes recovered",
            Model = model
        };
    }
}

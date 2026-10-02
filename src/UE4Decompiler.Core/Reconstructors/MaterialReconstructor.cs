using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine materials (<see cref="UMaterial"/>) and material instances
/// (<see cref="UMaterialInstanceConstant"/>).
/// For master materials: preserves expression node hierarchies, pins, and comments.
/// For material instances: extracts parent references, scalar/vector/texture/switch parameters,
/// and emits an automated Unreal Editor Python setup script (&lt;MatName&gt;_mic_setup.py)
/// using <c>unreal.MaterialEditingLibrary</c>.
/// </summary>
public sealed class MaterialReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            if (asset.Exports.OfType<UMaterial>().FirstOrDefault() is { } material)
                return ReconstructMaterial(material, asset, outputPathNoExt);

            if (asset.Exports.OfType<UMaterialInstanceConstant>().FirstOrDefault() is { } mic)
                return ReconstructMaterialInstance(mic, asset, outputPathNoExt);

            return ReconstructionResult.Failed("No UMaterial/MaterialInstance export found");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Material reconstruction error for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Material reconstruction error: {ex.Message}");
        }
    }

    private ReconstructionResult ReconstructMaterial(UMaterial material, ParsedAsset asset, string outputPathNoExt)
    {
        var nodes = new List<object>();
        foreach (var index in material.Expressions)
        {
            if (!index.TryLoad(out var expr) || expr is null) continue;

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
            Properties = material.Properties
        };

        var sidecars = new List<string>();
        var outDir = Path.GetDirectoryName(outputPathNoExt);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var matJsonPath = outputPathNoExt + "_mat.json";
        File.WriteAllText(matJsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(matJsonPath);

        Log.Information("Material {Name}: reconstructed {Count} expression node(s)", material.Name, nodes.Count);
        return new ReconstructionResult
        {
            Fidelity = nodes.Count > 0 ? Fidelity.Full : Fidelity.Partial,
            Note = $"{nodes.Count} expression nodes recovered; graph JSON emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private ReconstructionResult ReconstructMaterialInstance(UMaterialInstanceConstant mic, ParsedAsset asset, string outputPathNoExt)
    {
        var outDir = Path.GetDirectoryName(outputPathNoExt);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var matName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var parentPath = mic.Parent?.GetPathName()
                         ?? mic.GetOrDefault<FPackageIndex>("Parent")?.ResolvedObject?.GetPathName();

        // 1. Scalar Parameters
        var scalarParams = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var spvs = mic.GetOrDefault<FStructFallback[]>("ScalarParameterValues");
        if (spvs != null)
        {
            foreach (var s in spvs)
            {
                var info = s.GetOrDefault<FStructFallback>("ParameterInfo");
                var name = info?.GetOrDefault<FName>("Name").Text ?? s.GetOrDefault<FName>("ParameterName").Text;
                if (!string.IsNullOrEmpty(name))
                    scalarParams[name] = s.GetOrDefault<float>("ParameterValue");
            }
        }

        // 2. Vector Parameters
        var vectorParams = new Dictionary<string, Vector4Data>(StringComparer.OrdinalIgnoreCase);
        var vpvs = mic.GetOrDefault<FStructFallback[]>("VectorParameterValues");
        if (vpvs != null)
        {
            foreach (var v in vpvs)
            {
                var info = v.GetOrDefault<FStructFallback>("ParameterInfo");
                var name = info?.GetOrDefault<FName>("Name").Text ?? v.GetOrDefault<FName>("ParameterName").Text;
                if (!string.IsNullOrEmpty(name))
                {
                    var col = v.GetOrDefault<FLinearColor>("ParameterValue");
                    vectorParams[name] = new Vector4Data(col.R, col.G, col.B, col.A);
                }
            }
        }

        // 3. Texture Parameters
        var textureParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tpvs = mic.GetOrDefault<FStructFallback[]>("TextureParameterValues");
        if (tpvs != null)
        {
            foreach (var t in tpvs)
            {
                var info = t.GetOrDefault<FStructFallback>("ParameterInfo");
                var name = info?.GetOrDefault<FName>("Name").Text ?? t.GetOrDefault<FName>("ParameterName").Text;
                var ro = t.GetOrDefault<FPackageIndex>("ParameterValue")?.ResolvedObject;
                var p = ro?.GetPathName();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(p))
                    textureParams[name] = p;
            }
        }

        // 4. Static Switch Parameters
        var switchParams = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var sp = mic.GetOrDefault<FStructFallback>("StaticParametersRuntime")
                 ?? mic.GetOrDefault<FStructFallback>("StaticParameters");
        var sw = sp?.GetOrDefault<FStructFallback[]>("StaticSwitchParameters");
        if (sw != null)
        {
            foreach (var s in sw)
            {
                var info = s.GetOrDefault<FStructFallback>("ParameterInfo");
                var name = info?.GetOrDefault<FName>("Name").Text ?? s.GetOrDefault<FName>("ParameterName").Text;
                if (!string.IsNullOrEmpty(name))
                    switchParams[name] = s.GetOrDefault<bool>("Value");
            }
        }

        // 5. Overrides
        var bpo = mic.GetOrDefault<FStructFallback>("BasePropertyOverrides");
        bool twoSided = bpo?.GetOrDefault<bool>("TwoSided") ?? false;
        string blendMode = bpo?.GetOrDefault<FName>("BlendMode").Text ?? "BLEND_Opaque";
        string shadingModel = bpo?.GetOrDefault<FName>("ShadingModel").Text ?? "MSM_DefaultLit";

        var sidecars = new List<string>();

        // Emit JSON
        var micJsonPath = outputPathNoExt + "_mic.json";
        var model = new
        {
            AssetType = "MaterialInstanceConstant",
            matName,
            Parent = parentPath,
            ScalarParameters = scalarParams,
            VectorParameters = vectorParams,
            TextureParameters = textureParams,
            SwitchParameters = switchParams,
            Overrides = new { TwoSided = twoSided, BlendMode = blendMode, ShadingModel = shadingModel },
            Properties = mic.Properties
        };

        File.WriteAllText(micJsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(micJsonPath);

        // Emit Unreal Editor Python setup script
        var micPyPath = outputPathNoExt + "_mic_setup.py";
        var pyScript = GenerateMicPythonScript(matName, asset.File.Path, parentPath, scalarParams, vectorParams, textureParams, switchParams);
        File.WriteAllText(micPyPath, pyScript, Encoding.UTF8);
        sidecars.Add(micPyPath);

        var paramSummary = $"{scalarParams.Count} scalar, {vectorParams.Count} vector, {textureParams.Count} texture param(s)";
        Log.Information("MaterialInstance {Name}: Parent={Parent}, {Summary}", matName, parentPath ?? "None", paramSummary);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"MaterialInstance recovered ({paramSummary}); Python setup script & JSON emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static string GenerateMicPythonScript(
        string matName,
        string virtualPath,
        string? parentPath,
        Dictionary<string, float> scalars,
        Dictionary<string, Vector4Data> vectors,
        Dictionary<string, string> textures,
        Dictionary<string, bool> switches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine Material Instance Setup Script: {matName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_material_instance_{SanitizePy(matName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Setting up MaterialInstance: {matName}')");
        sb.AppendLine("    editor_asset = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Materials";

        sb.AppendLine($"    target_path = '{assetPath}'");
        sb.AppendLine($"    target_pkg = '{packagePath}'");
        sb.AppendLine($"    asset_name = '{matName}'");
        sb.AppendLine();
        sb.AppendLine("    mic = editor_asset.load_asset(target_path)");
        sb.AppendLine("    if not mic:");
        sb.AppendLine("        mic_factory = unreal.MaterialInstanceConstantFactoryNew()");
        sb.AppendLine("        mic = asset_tools.create_asset(asset_name, target_pkg, unreal.MaterialInstanceConstant, mic_factory)");
        sb.AppendLine();
        sb.AppendLine("    if not mic:");
        sb.AppendLine("        unreal.log_error(f'Failed to find or create MaterialInstance: {target_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(parentPath))
        {
            sb.AppendLine($"    parent_mat = editor_asset.load_asset('{parentPath}')");
            sb.AppendLine("    if parent_mat:");
            sb.AppendLine("        unreal.MaterialEditingLibrary.set_material_instance_parent(mic, parent_mat)");
            sb.AppendLine("        unreal.log(f'Assigned parent material: {parent_mat.get_name()}')");
            sb.AppendLine("    else:");
            sb.AppendLine($"        unreal.log_warning('Parent material not loaded yet: {parentPath}')");
            sb.AppendLine();
        }

        foreach (var (k, v) in scalars)
        {
            sb.AppendLine($"    unreal.MaterialEditingLibrary.set_material_instance_scalar_parameter_value(mic, '{EscapePy(k)}', {v.ToString("G7", CultureInfo.InvariantCulture)})");
        }

        foreach (var (k, v) in vectors)
        {
            sb.AppendLine($"    unreal.MaterialEditingLibrary.set_material_instance_vector_parameter_value(mic, '{EscapePy(k)}', unreal.LinearColor({v.R.ToString("G7", CultureInfo.InvariantCulture)}, {v.G.ToString("G7", CultureInfo.InvariantCulture)}, {v.B.ToString("G7", CultureInfo.InvariantCulture)}, {v.A.ToString("G7", CultureInfo.InvariantCulture)}))");
        }

        foreach (var (k, v) in textures)
        {
            var cleanTex = v.Split('.')[0];
            sb.AppendLine($"    tex_{SanitizePy(k)} = editor_asset.load_asset('{cleanTex}')");
            sb.AppendLine($"    if tex_{SanitizePy(k)}:");
            sb.AppendLine($"        unreal.MaterialEditingLibrary.set_material_instance_texture_parameter_value(mic, '{EscapePy(k)}', tex_{SanitizePy(k)})");
        }

        sb.AppendLine();
        sb.AppendLine("    unreal.MaterialEditingLibrary.update_material_instance(mic)");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Finished configuring MaterialInstance: {matName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_material_instance_{SanitizePy(matName)}()");

        return sb.ToString();
    }

    private static string SanitizePy(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        var s = sb.ToString();
        return char.IsDigit(s.FirstOrDefault()) ? "_" + s : s;
    }

    private static string EscapePy(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    public record Vector4Data(float R, float G, float B, float A);
}

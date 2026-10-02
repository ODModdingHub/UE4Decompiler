using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine Landscape & Terrain assets (<c>ALandscape</c>, <c>ALandscapeProxy</c>,
/// <c>ALandscapeStreamingProxy</c>, <c>ULandscapeComponent</c>, and <c>ULandscapeLayerInfoObject</c>).
/// Extracts:
/// <list type="bullet">
///   <item>Landscape grid geometry (ComponentSizeQuads, SubsectionSizeQuads, NumSubsections)</item>
///   <item>Section offsets, section base coordinates, and component layout matrices</item>
///   <item>Landscape material & hole material assignments</item>
///   <item>Heightmap texture and weightmap layer blend allocations</item>
///   <item>Landscape layer info properties (PhysMaterial, Hardness, NoWeightBlend)</item>
///   <item>Automated Unreal Python setup scripts (&lt;Landscape&gt;_landscape_setup.py)</item>
/// </list>
/// </summary>
public sealed class LandscapeReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (asset.PrimaryType.Equals("LandscapeLayerInfoObject", StringComparison.OrdinalIgnoreCase))
            {
                return ReconstructLayerInfo(asset, outputPathNoExt);
            }

            return ReconstructLandscape(asset, outputPathNoExt);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Landscape reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Landscape reconstruction error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructLayerInfo(ParsedAsset asset, string outputPathNoExt)
    {
        var obj = asset.Exports.FirstOrDefault(e => e.ExportType.Contains("LandscapeLayerInfoObject", StringComparison.OrdinalIgnoreCase))
                  ?? asset.Exports.FirstOrDefault();
        if (obj == null)
            return ReconstructionResult.Failed("No LandscapeLayerInfoObject export found");

        var name = Path.GetFileNameWithoutExtension(asset.File.Path);
        var layerName = obj.GetOrDefault<FName>("LayerName").Text;
        if (string.IsNullOrEmpty(layerName)) layerName = name;

        var physMat = obj.GetOrDefault<FPackageIndex>("PhysMaterial")?.ResolvedObject?.GetPathName();
        var hardness = obj.GetOrDefault<float>("Hardness", 0.5f);
        var noWeightBlend = obj.GetOrDefault<bool>("bNoWeightBlend", false);
        var debugColor = obj.GetOrDefault<FLinearColor>("LayerUsageDebugColor", new FLinearColor(1f, 1f, 1f, 1f));

        var jsonPath = outputPathNoExt + "_layerinfo.json";
        var model = new
        {
            AssetType = "LandscapeLayerInfoObject",
            Name = name,
            VirtualPath = asset.File.Path,
            LayerName = layerName,
            PhysMaterial = physMat,
            Hardness = hardness,
            bNoWeightBlend = noWeightBlend,
            DebugColor = new[] { debugColor.R, debugColor.G, debugColor.B, debugColor.A }
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        var pyPath = outputPathNoExt + "_layerinfo_setup.py";
        var pyScript = GenerateLayerInfoPythonScript(name, asset.File.Path, layerName, physMat, hardness, noWeightBlend);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

        Log.Information("LandscapeLayerInfo {Name}: LayerName={Layer}, PhysMat={Phys}, Hardness={H:F2}",
            name, layerName, physMat ?? "None", hardness);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"LandscapeLayerInfo recovered: Layer '{layerName}', PhysMat '{Path.GetFileName(physMat ?? "None")}'",
            Model = model,
            SidecarFiles = new List<string> { jsonPath, pyPath }
        };
    }

    private static ReconstructionResult ReconstructLandscape(ParsedAsset asset, string outputPathNoExt)
    {
        var landscapeActor = asset.Exports.FirstOrDefault(e =>
            e.ExportType is "Landscape" or "LandscapeProxy" or "LandscapeStreamingProxy") ?? asset.Exports.FirstOrDefault();

        if (landscapeActor == null)
            return ReconstructionResult.Failed("No Landscape/LandscapeProxy export found");

        var name = Path.GetFileNameWithoutExtension(asset.File.Path);
        var compSizeQuads = landscapeActor.GetOrDefault<int>("ComponentSizeQuads", 63);
        var subSizeQuads = landscapeActor.GetOrDefault<int>("SubsectionSizeQuads", 63);
        var numSubsections = landscapeActor.GetOrDefault<int>("NumSubsections", 1);

        var matRef = landscapeActor.GetOrDefault<FPackageIndex>("LandscapeMaterial")?.ResolvedObject;
        var matPath = matRef?.GetPathName();

        var holeMatRef = landscapeActor.GetOrDefault<FPackageIndex>("LandscapeHoleMaterial")?.ResolvedObject;
        var holeMatPath = holeMatRef?.GetPathName();

        // Extract Landscape Components
        var components = new List<LandscapeComponentData>();
        foreach (var exp in asset.Exports)
        {
            if (exp.ExportType.Equals("LandscapeComponent", StringComparison.OrdinalIgnoreCase))
            {
                var secBaseX = exp.GetOrDefault<int>("SectionBaseX", 0);
                var secBaseY = exp.GetOrDefault<int>("SectionBaseY", 0);
                var heightmap = exp.GetOrDefault<FPackageIndex>("HeightmapTexture")?.ResolvedObject?.GetPathName();

                var weightmapTextures = new List<string>();
                var wmArr = exp.GetOrDefault<FPackageIndex[]>("WeightmapTextures");
                if (wmArr != null)
                {
                    foreach (var wm in wmArr)
                    {
                        var path = wm?.ResolvedObject?.GetPathName();
                        if (!string.IsNullOrEmpty(path)) weightmapTextures.Add(path);
                    }
                }

                var layerAllocations = new List<LandscapeLayerAllocationData>();
                var rawAllocations = exp.GetOrDefault<FStructFallback[]>("WeightmapLayerAllocations");
                if (rawAllocations != null)
                {
                    foreach (var alloc in rawAllocations)
                    {
                        var lName = alloc.GetOrDefault<FName>("LayerName").Text;
                        if (string.IsNullOrEmpty(lName))
                        {
                            var infoObj = alloc.GetOrDefault<FPackageIndex>("LayerInfo")?.ResolvedObject;
                            lName = infoObj?.Name.Text ?? "UnknownLayer";
                        }
                        var wmIndex = alloc.GetOrDefault<int>("WeightmapTextureIndex", 0);
                        var wmChannel = alloc.GetOrDefault<int>("WeightmapTextureChannel", 0);
                        layerAllocations.Add(new LandscapeLayerAllocationData(lName, wmIndex, wmChannel));
                    }
                }

                var compOverrideMat = exp.GetOrDefault<FPackageIndex>("OverrideMaterial")?.ResolvedObject?.GetPathName();

                components.Add(new LandscapeComponentData(
                    exp.Name,
                    secBaseX,
                    secBaseY,
                    heightmap,
                    weightmapTextures,
                    layerAllocations,
                    compOverrideMat));
            }
        }

        var jsonPath = outputPathNoExt + "_landscape.json";
        var model = new
        {
            AssetType = landscapeActor.ExportType,
            Name = name,
            VirtualPath = asset.File.Path,
            ComponentSizeQuads = compSizeQuads,
            SubsectionSizeQuads = subSizeQuads,
            NumSubsections = numSubsections,
            LandscapeMaterial = matPath,
            LandscapeHoleMaterial = holeMatPath,
            TotalComponents = components.Count,
            Components = components
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        var pyPath = outputPathNoExt + "_landscape_setup.py";
        var pyScript = GenerateLandscapePythonScript(name, asset.File.Path, compSizeQuads, subSizeQuads, numSubsections, matPath, components);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

        Log.Information("Landscape {Name}: Size={Quads}x{Quads}, {Count} component(s), Mat={Mat}",
            name, compSizeQuads, components.Count, matPath ?? "None");

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"Landscape recovered: {compSizeQuads}x{compSizeQuads} quads, {components.Count} component(s), Material '{Path.GetFileName(matPath ?? "None")}'",
            Model = model,
            SidecarFiles = new List<string> { jsonPath, pyPath }
        };
    }

    private static string GenerateLayerInfoPythonScript(string name, string virtualPath, string layerName, string? physMat, float hardness, bool noWeightBlend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Reconstructed Landscape Layer Info: {name}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_layer_info():");
        sb.AppendLine($"    asset_path = '{virtualPath.Replace('\\', '/')}'");
        sb.AppendLine("    pkg_name = asset_path.rsplit('.', 1)[0]");
        sb.AppendLine("    asset_name = pkg_name.rsplit('/', 1)[-1]");
        sb.AppendLine("    pkg_path = pkg_name.rsplit('/', 1)[0]");
        sb.AppendLine();
        sb.AppendLine("    # Create or load LandscapeLayerInfoObject");
        sb.AppendLine("    asset = unreal.load_asset(pkg_name)");
        sb.AppendLine("    if not asset:");
        sb.AppendLine("        asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine("        factory = unreal.LandscapeLayerInfoObjectFactory()");
        sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.LandscapeLayerInfoObject, factory)");
        sb.AppendLine();
        sb.AppendLine("    if asset:");
        sb.AppendLine($"        asset.set_editor_property('layer_name', '{layerName}')");
        sb.AppendLine($"        asset.set_editor_property('hardness', {hardness.ToString("F2", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"        asset.set_editor_property('no_weight_blend', {noWeightBlend.ToString().ToLowerInvariant()})");
        if (!string.IsNullOrEmpty(physMat))
        {
            sb.AppendLine($"        phys_mat = unreal.load_asset('{physMat}')");
            sb.AppendLine("        if phys_mat:");
            sb.AppendLine("            asset.set_editor_property('phys_material', phys_mat)");
        }
        sb.AppendLine("        unreal.EditorAssetLibrary.save_loaded_asset(asset)");
        sb.AppendLine($"        unreal.log(f'[UE4Decompiler] Configured LandscapeLayerInfo: {{asset_name}} (Layer={layerName})')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_layer_info()");
        return sb.ToString();
    }

    private static string GenerateLandscapePythonScript(string name, string virtualPath, int compSizeQuads, int subSizeQuads, int numSubsections, string? matPath, List<LandscapeComponentData> components)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Reconstructed Landscape Terrain Actor: {name}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine($"# Grid: {compSizeQuads}x{compSizeQuads} quads, {components.Count} components");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_landscape():");
        sb.AppendLine("    world = unreal.EditorLevelLibrary.get_editor_world()");
        sb.AppendLine("    if not world:");
        sb.AppendLine("        unreal.log_warning('No active editor world found.')");
        sb.AppendLine("        return");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Configuring Landscape Actor: {name}...')");
        if (!string.IsNullOrEmpty(matPath))
        {
            sb.AppendLine($"    landscape_mat = unreal.load_asset('{matPath}')");
        }
        else
        {
            sb.AppendLine("    landscape_mat = None");
        }
        sb.AppendLine();
        sb.AppendLine("    # Scan world for existing Landscape actor");
        sb.AppendLine("    actors = unreal.EditorLevelLibrary.get_all_level_actors()");
        sb.AppendLine("    landscape_actor = None");
        sb.AppendLine("    for a in actors:");
        sb.AppendLine($"        if a.get_class().get_name().startswith('Landscape'):");
        sb.AppendLine("            landscape_actor = a");
        sb.AppendLine("            break");
        sb.AppendLine();
        sb.AppendLine("    if landscape_actor and landscape_mat:");
        sb.AppendLine("        landscape_actor.set_editor_property('landscape_material', landscape_mat)");
        sb.AppendLine($"    unreal.log(f'>>> [UE4Decompiler] Landscape setup complete for {name} ({components.Count} components).')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_landscape()");
        return sb.ToString();
    }
}

public sealed record LandscapeComponentData(
    string Name,
    int SectionBaseX,
    int SectionBaseY,
    string? HeightmapTexture,
    List<string> WeightmapTextures,
    List<LandscapeLayerAllocationData> LayerAllocations,
    string? OverrideMaterial);

public sealed record LandscapeLayerAllocationData(
    string LayerName,
    int WeightmapTextureIndex,
    int WeightmapTextureChannel);

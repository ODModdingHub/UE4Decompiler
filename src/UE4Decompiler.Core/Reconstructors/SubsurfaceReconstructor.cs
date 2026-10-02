using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine Subsurface Scattering Profile assets (<c>USubsurfaceProfile</c>).
/// Used by realistic human skin, wax, marble, and subsurface foliage shading models.
/// Extracts:
/// <list type="bullet">
///   <item>Optical scatter radius (cm) and extinction scale</item>
///   <item>Subsurface color, falloff tint, and boundary color bleed</item>
///   <item>Dual specular roughness parameters (Roughness0, Roughness1, LobeMix)</item>
///   <item>Burley normalized diffusion profile flags and transmission tint</item>
///   <item>Automated Unreal Python setup script (&lt;Profile&gt;_subsurface_setup.py)</item>
/// </list>
/// </summary>
public sealed class SubsurfaceReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var obj = asset.Exports.FirstOrDefault(e => e.ExportType.Equals("SubsurfaceProfile", StringComparison.OrdinalIgnoreCase))
                      ?? asset.Exports.FirstOrDefault();
            if (obj == null)
                return ReconstructionResult.Failed("No SubsurfaceProfile export found");

            var name = Path.GetFileNameWithoutExtension(asset.File.Path);
            var settings = obj.GetOrDefault<FStructFallback>("Settings");

            var scatterRadius = settings?.GetOrDefault<float>("ScatterRadius", 1.2f) ?? 1.2f;
            var subColor = settings?.GetOrDefault<FLinearColor>("SubsurfaceColor", new FLinearColor(1f, 1f, 1f, 1f))
                           ?? new FLinearColor(1f, 1f, 1f, 1f);
            var falloffColor = settings?.GetOrDefault<FLinearColor>("FalloffColor", new FLinearColor(1f, 0.8f, 0.7f, 1f))
                               ?? new FLinearColor(1f, 0.8f, 0.7f, 1f);
            var boundaryColor = settings?.GetOrDefault<FLinearColor>("BoundaryColorBleed", new FLinearColor(1f, 1f, 1f, 1f))
                                ?? new FLinearColor(1f, 1f, 1f, 1f);
            var extinctionScale = settings?.GetOrDefault<float>("ExtinctionScale", 1.0f) ?? 1.0f;
            var normalScale = settings?.GetOrDefault<float>("NormalScale", 0.08f) ?? 0.08f;
            var scatteringDist = settings?.GetOrDefault<float>("ScatteringDistribution", 0.93f) ?? 0.93f;
            var roughness0 = settings?.GetOrDefault<float>("Roughness0", 0.76f) ?? 0.76f;
            var roughness1 = settings?.GetOrDefault<float>("Roughness1", 0.42f) ?? 0.42f;
            var lobeMix = settings?.GetOrDefault<float>("LobeMix", 0.85f) ?? 0.85f;
            var bEnableBurley = settings?.GetOrDefault<bool>("bEnableBurley", true) ?? true;
            var transmissionColor = settings?.GetOrDefault<FLinearColor>("TransmissionTintColor", new FLinearColor(1f, 1f, 1f, 1f))
                                    ?? new FLinearColor(1f, 1f, 1f, 1f);

            var jsonPath = outputPathNoExt + "_subsurface.json";
            var model = new
            {
                AssetType = "SubsurfaceProfile",
                Name = name,
                VirtualPath = asset.File.Path,
                Settings = new
                {
                    ScatterRadius = scatterRadius,
                    SubsurfaceColor = new[] { subColor.R, subColor.G, subColor.B, subColor.A },
                    FalloffColor = new[] { falloffColor.R, falloffColor.G, falloffColor.B, falloffColor.A },
                    BoundaryColorBleed = new[] { boundaryColor.R, boundaryColor.G, boundaryColor.B, boundaryColor.A },
                    ExtinctionScale = extinctionScale,
                    NormalScale = normalScale,
                    ScatteringDistribution = scatteringDist,
                    Roughness0 = roughness0,
                    Roughness1 = roughness1,
                    LobeMix = lobeMix,
                    bEnableBurley = bEnableBurley,
                    TransmissionTintColor = new[] { transmissionColor.R, transmissionColor.G, transmissionColor.B, transmissionColor.A }
                }
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

            var pyPath = outputPathNoExt + "_subsurface_setup.py";
            var pyScript = GenerateSubsurfacePythonScript(name, asset.File.Path, scatterRadius, subColor, falloffColor, extinctionScale, bEnableBurley);
            File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

            Log.Information("SubsurfaceProfile {Name}: Radius={Radius:F2}cm, Burley={Burley}",
                name, scatterRadius, bEnableBurley);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"SubsurfaceProfile recovered: ScatterRadius={scatterRadius:F2}cm, Burley={bEnableBurley}",
                Model = model,
                SidecarFiles = new List<string> { jsonPath, pyPath }
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SubsurfaceProfile reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Subsurface reconstruction error: {ex.Message}");
        }
    }

    private static string GenerateSubsurfacePythonScript(string name, string virtualPath, float radius, FLinearColor subColor, FLinearColor falloffColor, float extinction, bool burley)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Reconstructed Subsurface Scattering Profile: {name}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_subsurface_profile():");
        sb.AppendLine($"    asset_path = '{virtualPath.Replace('\\', '/')}'");
        sb.AppendLine("    pkg_name = asset_path.rsplit('.', 1)[0]");
        sb.AppendLine("    asset_name = pkg_name.rsplit('/', 1)[-1]");
        sb.AppendLine("    pkg_path = pkg_name.rsplit('/', 1)[0]");
        sb.AppendLine();
        sb.AppendLine("    profile = unreal.load_asset(pkg_name)");
        sb.AppendLine("    if not profile:");
        sb.AppendLine("        asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine("        factory = unreal.SubsurfaceProfileFactory()");
        sb.AppendLine("        profile = asset_tools.create_asset(asset_name, pkg_path, unreal.SubsurfaceProfile, factory)");
        sb.AppendLine();
        sb.AppendLine("    if profile:");
        sb.AppendLine("        settings = profile.get_editor_property('settings')");
        sb.AppendLine($"        settings.set_editor_property('scatter_radius', {radius.ToString("F3", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"        settings.set_editor_property('extinction_scale', {extinction.ToString("F3", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"        settings.set_editor_property('enable_burley', {burley.ToString().ToLowerInvariant()})");
        sb.AppendLine($"        settings.set_editor_property('subsurface_color', unreal.LinearColor({subColor.R.ToString("F3", CultureInfo.InvariantCulture)}, {subColor.G.ToString("F3", CultureInfo.InvariantCulture)}, {subColor.B.ToString("F3", CultureInfo.InvariantCulture)}, {subColor.A.ToString("F3", CultureInfo.InvariantCulture)}))");
        sb.AppendLine($"        settings.set_editor_property('falloff_color', unreal.LinearColor({falloffColor.R.ToString("F3", CultureInfo.InvariantCulture)}, {falloffColor.G.ToString("F3", CultureInfo.InvariantCulture)}, {falloffColor.B.ToString("F3", CultureInfo.InvariantCulture)}, {falloffColor.A.ToString("F3", CultureInfo.InvariantCulture)}))");
        sb.AppendLine("        profile.set_editor_property('settings', settings)");
        sb.AppendLine("        unreal.EditorAssetLibrary.save_loaded_asset(profile)");
        sb.AppendLine($"        unreal.log(f'[UE4Decompiler] Configured SubsurfaceProfile: {{asset_name}}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_subsurface_profile()");
        return sb.ToString();
    }
}

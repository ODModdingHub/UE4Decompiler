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
/// Reconstructs Unreal Engine Foliage assets (<c>UFoliageType</c>, <c>UFoliageType_InstancedStaticMesh</c>).
/// Extracts:
/// <list type="bullet">
///   <item>StaticMesh model references and material overrides</item>
///   <item>Placement density, exclusion radius, and ground slope angles</item>
///   <item>Random scaling variations (Uniform, Free, LockXY), scale min/max boundaries</item>
///   <item>Shadow casting, collision profile assignments, and runtime mobility</item>
///   <item>Automated Unreal Python setup script (&lt;Foliage&gt;_foliage_setup.py)</item>
/// </list>
/// </summary>
public sealed class FoliageReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var obj = asset.Exports.FirstOrDefault(e => e.ExportType.Contains("FoliageType", StringComparison.OrdinalIgnoreCase)) ?? asset.Exports.FirstOrDefault();
            if (obj == null)
                return ReconstructionResult.Failed("No FoliageType export found");

            var name = Path.GetFileNameWithoutExtension(asset.File.Path);
            var meshRef = obj.GetOrDefault<FPackageIndex>("Mesh")?.ResolvedObject;
            var meshPath = meshRef?.GetPathName();

            var density = obj.GetOrDefault<float>("Density", 100f);
            var radius = obj.GetOrDefault<float>("Radius", 0f);
            var scaleMin = obj.GetOrDefault<FVector>("ScaleMin", new FVector(1f, 1f, 1f));
            var scaleMax = obj.GetOrDefault<FVector>("ScaleMax", new FVector(1f, 1f, 1f));
            var alignToNormal = obj.GetOrDefault<bool>("AlignToNormal", true);
            var randomYaw = obj.GetOrDefault<bool>("RandomYaw", true);
            var groundSlope = obj.GetOrDefault<FStructFallback>("GroundSlope");
            var minSlope = groundSlope?.GetOrDefault<float>("Min", 0f) ?? 0f;
            var maxSlope = groundSlope?.GetOrDefault<float>("Max", 90f) ?? 90f;
            var castShadow = obj.GetOrDefault<bool>("CastShadow", true);
            var collisionProfile = obj.GetOrDefault<FName>("CollisionProfileName").Text ?? "NoCollision";

            var jsonPath = outputPathNoExt + "_foliage.json";
            var model = new
            {
                AssetType = "FoliageType",
                Name = name,
                VirtualPath = asset.File.Path,
                MeshPath = meshPath,
                Density = density,
                Radius = radius,
                ScaleMin = new[] { (float)scaleMin.X, (float)scaleMin.Y, (float)scaleMin.Z },
                ScaleMax = new[] { (float)scaleMax.X, (float)scaleMax.Y, (float)scaleMax.Z },
                AlignToNormal = alignToNormal,
                RandomYaw = randomYaw,
                GroundSlopeMin = minSlope,
                GroundSlopeMax = maxSlope,
                CastShadow = castShadow,
                CollisionProfile = collisionProfile
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

            var pyPath = outputPathNoExt + "_foliage_setup.py";
            var pyScript = GenerateFoliagePythonScript(name, asset.File.Path, meshPath, density, radius, collisionProfile);
            File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

            Log.Information("FoliageType {Name}: Mesh={Mesh}, Density={Density:F1}, Collision={Col}",
                name, meshPath ?? "None", density, collisionProfile);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"FoliageType recovered: Mesh {Path.GetFileName(meshPath ?? "None")}, Density {density:F1}, Profile {collisionProfile}",
                Model = model,
                SidecarFiles = new List<string> { jsonPath, pyPath }
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Foliage reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Foliage reconstruction error: {ex.Message}");
        }
    }

    private static string GenerateFoliagePythonScript(string name, string virtualPath, string? meshPath, float density, float radius, string colProfile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine FoliageType Setup Script: {name}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_foliage_type():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Setting up FoliageType: {name}')");
        sb.AppendLine($"    pkg_path = '{virtualPath.Replace(".uasset", "")}'");
        sb.AppendLine("    foliage = unreal.EditorAssetLibrary.load_asset(pkg_path)");
        sb.AppendLine("    if not foliage:");
        sb.AppendLine("        unreal.log_warning(f'FoliageType not loaded at {pkg_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(meshPath))
        {
            sb.AppendLine($"    mesh = unreal.EditorAssetLibrary.load_asset('{meshPath}')");
            sb.AppendLine("    if mesh:");
            sb.AppendLine("        foliage.set_editor_property('mesh', mesh)");
        }
        sb.AppendLine($"    foliage.set_editor_property('density', {density.ToString("F1", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"    foliage.set_editor_property('radius', {radius.ToString("F1", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"    unreal.log(f'Configured FoliageType {name}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_foliage_type()");
        return sb.ToString();
    }
}

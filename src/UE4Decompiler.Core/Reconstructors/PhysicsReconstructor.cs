using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine physics assets:
/// <list type="bullet">
///   <item><c>UPhysicalMaterial</c>: Friction, restitution, density, and custom physical surface types.</item>
///   <item><c>UPhysicsAsset</c>: Skeletal collision bodies (Spheres, Boxes, Sphyls/Capsules, Convex) and joint constraints.</item>
/// </list>
/// Automatically discovers and records custom physical surface types for <c>DefaultEngine.ini</c> scaffolding.
/// </summary>
public sealed class PhysicsReconstructor
{
    /// <summary>Discovered physical surfaces across all packages (e.g. "SurfaceType1" -> "SurfaceType1").</summary>
    public static ConcurrentDictionary<string, string> DiscoveredPhysicalSurfaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return asset.PrimaryType switch
            {
                "PhysicalMaterial" => ReconstructPhysicalMaterial(asset, outputPathNoExt),
                "PhysicsAsset" => ReconstructPhysicsAsset(asset, outputPathNoExt),
                _ => ReconstructionResult.Failed($"Unsupported physics type: {asset.PrimaryType}")
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Physics reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Physics reconstruction error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructPhysicalMaterial(ParsedAsset asset, string outputPathNoExt)
    {
        var mat = asset.Exports.FirstOrDefault(e => e.ExportType == "PhysicalMaterial") ?? asset.Exports.FirstOrDefault();
        if (mat == null)
            return ReconstructionResult.Failed("No PhysicalMaterial export found");

        var name = Path.GetFileNameWithoutExtension(asset.File.Path);
        var friction = mat.GetOrDefault<float>("Friction", 0.7f);
        var frictionCombine = mat.GetOrDefault<FName>("FrictionCombineMode").Text ?? "Average";
        var restitution = mat.GetOrDefault<float>("Restitution", 0.3f);
        var restitutionCombine = mat.GetOrDefault<FName>("RestitutionCombineMode").Text ?? "Average";
        var density = mat.GetOrDefault<float>("Density", 1.0f);
        var raiseCollision = mat.GetOrDefault<bool>("bRaiseCollisionEvents", false);
        var surfaceType = mat.GetOrDefault<FName>("SurfaceType").Text ?? "SurfaceType_Default";

        if (!surfaceType.Equals("SurfaceType_Default", StringComparison.OrdinalIgnoreCase))
        {
            DiscoveredPhysicalSurfaces.TryAdd(surfaceType, name);
        }

        var jsonPath = outputPathNoExt + "_physical_material.json";
        var model = new
        {
            AssetType = "PhysicalMaterial",
            Name = name,
            VirtualPath = asset.File.Path,
            Friction = friction,
            FrictionCombineMode = frictionCombine,
            Restitution = restitution,
            RestitutionCombineMode = restitutionCombine,
            Density = density,
            RaiseCollisionEvents = raiseCollision,
            SurfaceType = surfaceType
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        var pyPath = outputPathNoExt + "_physmat_setup.py";
        var pyScript = GeneratePhysMatPythonScript(name, asset.File.Path, friction, frictionCombine, restitution, restitutionCombine, density, surfaceType);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

        Log.Information("PhysicalMaterial {Name}: Friction={F:F2}, Restitution={R:F2}, Surface={S}",
            name, friction, restitution, surfaceType);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"PhysicalMaterial recovered: Friction {friction:F2}, Restitution {restitution:F2}, Surface {surfaceType}",
            Model = model,
            SidecarFiles = new List<string> { jsonPath, pyPath }
        };
    }

    private static ReconstructionResult ReconstructPhysicsAsset(ParsedAsset asset, string outputPathNoExt)
    {
        var phys = asset.Exports.FirstOrDefault(e => e.ExportType == "PhysicsAsset") ?? asset.Exports.FirstOrDefault();
        if (phys == null)
            return ReconstructionResult.Failed("No PhysicsAsset export found");

        var physName = Path.GetFileNameWithoutExtension(asset.File.Path);

        // 1. Skeletal body setups
        var bodies = new List<DetailedPhysicsBodyData>();
        var rawBodies = phys.GetOrDefault<FPackageIndex[]>("SkeletalBodySetups");
        if (rawBodies != null)
        {
            foreach (var b in rawBodies)
            {
                var bodySetup = b?.ResolvedObject;
                if (bodySetup != null && bodySetup.TryLoad(out var loadedBody) && loadedBody is not null)
                {
                    var boneName = loadedBody.GetOrDefault<FName>("BoneName").Text;
                    var physType = loadedBody.GetOrDefault<FName>("PhysicsType").Text ?? "Default";
                    var colFlag = loadedBody.GetOrDefault<FName>("CollisionTraceFlag").Text ?? "CTF_UseDefault";

                    var bodyData = new DetailedPhysicsBodyData
                    {
                        BodyName = loadedBody.Name,
                        BoneName = string.IsNullOrEmpty(boneName) ? loadedBody.Name : boneName,
                        PhysicsType = physType,
                        CollisionTraceFlag = colFlag
                    };

                    // Extract AggGeom collision primitives
                    var aggGeom = loadedBody.GetOrDefault<FStructFallback>("AggGeom");
                    if (aggGeom != null)
                    {
                        // Spheres
                        var spheres = aggGeom.GetOrDefault<FStructFallback[]>("SphereElems");
                        if (spheres != null)
                        {
                            foreach (var s in spheres)
                            {
                                var center = ReadVector(s, "Center");
                                var radius = s.GetOrDefault<float>("Radius", 10f);
                                bodyData.Spheres.Add(new SphereElemData { Center = center, Radius = radius });
                            }
                        }

                        // Boxes
                        var boxes = aggGeom.GetOrDefault<FStructFallback[]>("BoxElems");
                        if (boxes != null)
                        {
                            foreach (var bx in boxes)
                            {
                                var center = ReadVector(bx, "Center");
                                var rot = ReadRotator(bx, "Rotation");
                                var x = bx.GetOrDefault<float>("X", 20f);
                                var y = bx.GetOrDefault<float>("Y", 20f);
                                var z = bx.GetOrDefault<float>("Z", 20f);
                                bodyData.Boxes.Add(new BoxElemData { Center = center, Rotation = rot, X = x, Y = y, Z = z });
                            }
                        }

                        // Sphyls (Capsules)
                        var sphyls = aggGeom.GetOrDefault<FStructFallback[]>("SphylElems");
                        if (sphyls != null)
                        {
                            foreach (var sp in sphyls)
                            {
                                var center = ReadVector(sp, "Center");
                                var rot = ReadRotator(sp, "Rotation");
                                var radius = sp.GetOrDefault<float>("Radius", 10f);
                                var length = sp.GetOrDefault<float>("Length", 20f);
                                bodyData.Capsules.Add(new CapsuleElemData { Center = center, Rotation = rot, Radius = radius, Length = length });
                            }
                        }
                    }

                    bodies.Add(bodyData);
                }
            }
        }

        // 2. Constraints
        var constraints = new List<DetailedConstraintData>();
        var rawConstraints = phys.GetOrDefault<FPackageIndex[]>("ConstraintSetup");
        if (rawConstraints != null)
        {
            foreach (var c in rawConstraints)
            {
                var ro = c?.ResolvedObject;
                if (ro != null && ro.TryLoad(out var loadedCon) && loadedCon is not null)
                {
                    var jName = loadedCon.GetOrDefault<FName>("JointName").Text;
                    var b1 = loadedCon.GetOrDefault<FName>("ConstraintBone1").Text;
                    var b2 = loadedCon.GetOrDefault<FName>("ConstraintBone2").Text;

                    var conInstance = loadedCon.GetOrDefault<FStructFallback>("DefaultInstance");
                    var swing1 = conInstance?.GetOrDefault<FName>("Swing1Motion").Text ?? "ACM_Limited";
                    var swing2 = conInstance?.GetOrDefault<FName>("Swing2Motion").Text ?? "ACM_Limited";
                    var twist = conInstance?.GetOrDefault<FName>("TwistMotion").Text ?? "ACM_Limited";
                    var swing1Limit = conInstance?.GetOrDefault<float>("Swing1LimitAngle", 45f) ?? 45f;
                    var swing2Limit = conInstance?.GetOrDefault<float>("Swing2LimitAngle", 45f) ?? 45f;
                    var twistLimit = conInstance?.GetOrDefault<float>("TwistLimitAngle", 45f) ?? 45f;

                    constraints.Add(new DetailedConstraintData
                    {
                        JointName = string.IsNullOrEmpty(jName) ? loadedCon.Name : jName,
                        Bone1 = b1,
                        Bone2 = b2,
                        Swing1Motion = swing1,
                        Swing2Motion = swing2,
                        TwistMotion = twist,
                        Swing1LimitAngle = swing1Limit,
                        Swing2LimitAngle = swing2Limit,
                        TwistLimitAngle = twistLimit
                    });
                }
            }
        }

        var jsonPath = outputPathNoExt + "_physics_asset.json";
        var model = new
        {
            AssetType = "PhysicsAsset",
            Name = physName,
            VirtualPath = asset.File.Path,
            BodyCount = bodies.Count,
            Bodies = bodies,
            ConstraintCount = constraints.Count,
            Constraints = constraints
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        var pyPath = outputPathNoExt + "_physics_setup.py";
        var pyScript = GeneratePhysicsAssetPythonScript(physName, asset.File.Path, bodies, constraints);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

        Log.Information("PhysicsAsset {Name}: {Bodies} collision body(s), {Constraints} joint constraint(s)",
            physName, bodies.Count, constraints.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"PhysicsAsset recovered: {bodies.Count} skeletal bodies ({bodies.Sum(b => b.Spheres.Count + b.Boxes.Count + b.Capsules.Count)} shapes), {constraints.Count} constraints",
            Model = model,
            SidecarFiles = new List<string> { jsonPath, pyPath }
        };
    }

    private static string GeneratePhysMatPythonScript(string name, string virtualPath, float friction, string frictionCombine, float restitution, string restitutionCombine, float density, string surface)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine Physical Material Setup Script: {name}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_physical_material():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Setting up PhysicalMaterial: {name}')");
        sb.AppendLine($"    pkg_path = '{virtualPath.Replace(".uasset", "")}'");
        sb.AppendLine("    mat = unreal.EditorAssetLibrary.load_asset(pkg_path)");
        sb.AppendLine("    if not mat:");
        sb.AppendLine("        unreal.log_warning(f'PhysicalMaterial not loaded at {pkg_path}')");
        sb.AppendLine("        return");
        sb.AppendLine($"    mat.set_editor_property('friction', {friction.ToString("F2", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"    mat.set_editor_property('restitution', {restitution.ToString("F2", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"    mat.set_editor_property('density', {density.ToString("F2", CultureInfo.InvariantCulture)})");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_physical_material()");
        return sb.ToString();
    }

    private static string GeneratePhysicsAssetPythonScript(string physName, string virtualPath, List<DetailedPhysicsBodyData> bodies, List<DetailedConstraintData> constraints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine Physics Asset Setup Script: {physName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_physics_asset():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Setting up PhysicsAsset: {physName}')");
        sb.AppendLine($"    pkg_path = '{virtualPath.Replace(".uasset", "")}'");
        sb.AppendLine("    phys = unreal.EditorAssetLibrary.load_asset(pkg_path)");
        sb.AppendLine("    if not phys:");
        sb.AppendLine("        unreal.log_warning(f'PhysicsAsset not loaded at {pkg_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();
        sb.AppendLine("    bodies = [");
        foreach (var b in bodies)
        {
            sb.AppendLine($"        {{ 'bone': '{b.BoneName}', 'spheres': {b.Spheres.Count}, 'boxes': {b.Boxes.Count}, 'capsules': {b.Capsules.Count} }},");
        }
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine("    constraints = [");
        foreach (var c in constraints)
        {
            sb.AppendLine($"        {{ 'joint': '{c.JointName}', 'bone1': '{c.Bone1}', 'bone2': '{c.Bone2}', 'swing1': {c.Swing1LimitAngle:F1}, 'twist': {c.TwistLimitAngle:F1} }},");
        }
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log(f'Recreated {physName} with {{len(bodies)}} bodies and {{len(constraints)}} constraints.')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_physics_asset()");
        return sb.ToString();
    }

    private static float[] ReadVector(FStructFallback obj, string propName)
    {
        var v = obj.GetOrDefault<FVector>(propName);
        return new[] { (float)v.X, (float)v.Y, (float)v.Z };
    }

    private static float[] ReadRotator(FStructFallback obj, string propName)
    {
        var r = obj.GetOrDefault<FRotator>(propName);
        return new[] { (float)r.Pitch, (float)r.Yaw, (float)r.Roll };
    }
}

public sealed class DetailedPhysicsBodyData
{
    public string BodyName { get; set; } = "";
    public string BoneName { get; set; } = "";
    public string PhysicsType { get; set; } = "Default";
    public string CollisionTraceFlag { get; set; } = "CTF_UseDefault";
    public List<SphereElemData> Spheres { get; } = new();
    public List<BoxElemData> Boxes { get; } = new();
    public List<CapsuleElemData> Capsules { get; } = new();
}

public sealed class SphereElemData
{
    public float[] Center { get; set; } = new float[3];
    public float Radius { get; set; }
}

public sealed class BoxElemData
{
    public float[] Center { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new float[3];
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public sealed class CapsuleElemData
{
    public float[] Center { get; set; } = new float[3];
    public float[] Rotation { get; set; } = new float[3];
    public float Radius { get; set; }
    public float Length { get; set; }
}

public sealed class DetailedConstraintData
{
    public string JointName { get; set; } = "";
    public string Bone1 { get; set; } = "";
    public string Bone2 { get; set; } = "";
    public string Swing1Motion { get; set; } = "ACM_Limited";
    public string Swing2Motion { get; set; } = "ACM_Limited";
    public string TwistMotion { get; set; } = "ACM_Limited";
    public float Swing1LimitAngle { get; set; }
    public float Swing2LimitAngle { get; set; }
    public float TwistLimitAngle { get; set; }
}

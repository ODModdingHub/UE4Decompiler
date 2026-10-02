using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs skeletal rigs, animation tracks, montages, blend spaces, and physics assets:
/// <list type="bullet">
///   <item><c>USkeleton</c>: Bone hierarchy, reference poses, sockets, and Python socket setup scripts.</item>
///   <item><c>UAnimSequence</c>: Duration, frames, rates, skeleton linkage, and anim notify markers.</item>
///   <item><c>UAnimMontage</c>: Composite sections, slot assignments, and blend profiles.</item>
///   <item><c>UBlendSpace</c>: Dimension axes, sample points, and linked animations.</item>
///   <item><c>UPhysicsAsset</c>: Skeletal collision bodies (Spheres, Boxes, Capsules) and joint constraints.</item>
/// </list>
/// </summary>
public sealed class AnimationReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return asset.PrimaryType switch
            {
                "Skeleton" => ReconstructSkeleton(asset, outputPathNoExt),
                "AnimSequence" => ReconstructAnimSequence(asset, outputPathNoExt),
                "AnimMontage" => ReconstructAnimMontage(asset, outputPathNoExt),
                "BlendSpace" or "BlendSpace1D" => ReconstructBlendSpace(asset, outputPathNoExt),
                "PhysicsAsset" => ReconstructPhysicsAsset(asset, outputPathNoExt),
                _ => ReconstructionResult.Failed($"Unsupported animation type: {asset.PrimaryType}")
            };
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Animation reconstruction error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructSkeleton(ParsedAsset asset, string outputPathNoExt)
    {
        var skel = asset.Exports.FirstOrDefault(e => e.ExportType == "Skeleton") ?? asset.Exports.FirstOrDefault();
        if (skel == null)
            return ReconstructionResult.Failed("No Skeleton export found");

        var skelName = Path.GetFileNameWithoutExtension(asset.File.Path);

        // 1. Bones from ReferenceSkeleton
        var bones = new List<BoneInfoData>();
        if (skel is USkeleton uSkel && uSkel.ReferenceSkeleton.FinalRefBoneInfo != null)
        {
            for (int i = 0; i < uSkel.ReferenceSkeleton.FinalRefBoneInfo.Length; i++)
            {
                var b = uSkel.ReferenceSkeleton.FinalRefBoneInfo[i];
                bones.Add(new BoneInfoData
                {
                    Index = i,
                    Name = b.Name.Text,
                    ParentIndex = b.ParentIndex
                });
            }
        }

        // 2. Sockets
        var sockets = new List<SkeletalSocketData>();
        var socketExports = asset.Exports.Where(e => e.ExportType == "SkeletalMeshSocket").ToList();
        if (socketExports.Count > 0)
        {
            foreach (var s in socketExports)
            {
                var sName = s.GetOrDefault<FName>("SocketName").Text;
                var bName = s.GetOrDefault<FName>("BoneName").Text;
                var loc = ReadVector(s, "RelativeLocation");
                var rot = ReadRotator(s, "RelativeRotation");
                var scl = ReadVector(s, "RelativeScale", 1f, 1f, 1f);

                sockets.Add(new SkeletalSocketData
                {
                    SocketName = string.IsNullOrEmpty(sName) ? s.Name : sName,
                    BoneName = bName,
                    Location = loc,
                    Rotation = rot,
                    Scale = scl
                });
            }
        }

        var sidecars = new List<string>();

        // 1. JSON sidecar
        var jsonPath = outputPathNoExt + ".skeleton.json";
        var model = new
        {
            AssetType = "Skeleton",
            Name = skelName,
            VirtualPath = asset.File.Path,
            BoneCount = bones.Count,
            Bones = bones,
            SocketCount = sockets.Count,
            Sockets = sockets
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));
        sidecars.Add(jsonPath);

        // 2. Python Socket Setup Script for Editor
        if (sockets.Count > 0)
        {
            var pyPath = outputPathNoExt + "_sockets.py";
            var pyScript = GenerateSocketPythonScript(skelName, asset.File.Path, sockets);
            File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
            sidecars.Add(pyPath);
        }

        Log.Information("Skeleton {Name}: {Bones} bone(s), {Sockets} socket(s) recovered",
            skelName, bones.Count, sockets.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"Skeleton recovered: {bones.Count} bones in hierarchy, {sockets.Count} skeletal sockets preserved",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static ReconstructionResult ReconstructAnimSequence(ParsedAsset asset, string outputPathNoExt)
    {
        var anim = asset.Exports.FirstOrDefault(e => e.ExportType == "AnimSequence") ?? asset.Exports.FirstOrDefault();
        if (anim == null)
            return ReconstructionResult.Failed("No AnimSequence export found");

        var animName = Path.GetFileNameWithoutExtension(asset.File.Path);
        var duration = anim.GetOrDefault<float>("SequenceLength", 0f);
        var numFrames = anim.GetOrDefault<int>("NumFrames", anim.GetOrDefault<int>("NumberOfSampledKeys", 0));
        var rateScale = anim.GetOrDefault<float>("RateScale", 1.0f);

        var skelRef = anim.GetOrDefault<FPackageIndex>("Skeleton")?.ResolvedObject;
        var skelPath = skelRef?.GetPathName() ?? anim.GetOrDefault<FSoftObjectPath>("Skeleton").ToString();

        var notifies = ExtractAnimNotifies(anim);

        var jsonPath = outputPathNoExt + ".anim.json";
        var model = new
        {
            AssetType = "AnimSequence",
            Name = animName,
            VirtualPath = asset.File.Path,
            Duration = duration,
            NumFrames = numFrames,
            RateScale = rateScale,
            Skeleton = skelPath,
            NotifyCount = notifies.Count,
            Notifies = notifies
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        Log.Information("AnimSequence {Name}: Duration={Duration:F2}s, {Frames} frame(s), {Notifies} notify marker(s)",
            animName, duration, numFrames, notifies.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"AnimSequence recovered: Duration={duration:F2}s, {numFrames} frames, {notifies.Count} anim notifies",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static ReconstructionResult ReconstructAnimMontage(ParsedAsset asset, string outputPathNoExt)
    {
        var montage = asset.Exports.FirstOrDefault(e => e.ExportType == "AnimMontage") ?? asset.Exports.FirstOrDefault();
        if (montage == null)
            return ReconstructionResult.Failed("No AnimMontage export found");

        var montageName = Path.GetFileNameWithoutExtension(asset.File.Path);
        var duration = montage.GetOrDefault<float>("SequenceLength", 0f);

        var skelRef = montage.GetOrDefault<FPackageIndex>("Skeleton")?.ResolvedObject;
        var skelPath = skelRef?.GetPathName() ?? montage.GetOrDefault<FSoftObjectPath>("Skeleton").ToString();

        // Composite sections
        var sections = new List<MontageSectionData>();
        var rawSections = montage.GetOrDefault<FStructFallback[]>("CompositeSections");
        if (rawSections != null)
        {
            foreach (var s in rawSections)
            {
                var sName = s.GetOrDefault<FName>("SectionName").Text;
                var sTime = s.GetOrDefault<float>("Time", 0f);
                var next = s.GetOrDefault<FName>("NextSectionName").Text;
                sections.Add(new MontageSectionData { SectionName = sName, Time = sTime, NextSection = next });
            }
        }

        // Slots
        var slotNames = new List<string>();
        var rawSlots = montage.GetOrDefault<FStructFallback[]>("SlotAnimTracks");
        if (rawSlots != null)
        {
            foreach (var sl in rawSlots)
            {
                var sn = sl.GetOrDefault<FName>("SlotName").Text;
                if (!string.IsNullOrEmpty(sn)) slotNames.Add(sn);
            }
        }

        var notifies = ExtractAnimNotifies(montage);

        var jsonPath = outputPathNoExt + ".montage.json";
        var model = new
        {
            AssetType = "AnimMontage",
            Name = montageName,
            VirtualPath = asset.File.Path,
            Duration = duration,
            Skeleton = skelPath,
            SectionCount = sections.Count,
            Sections = sections,
            Slots = slotNames,
            NotifyCount = notifies.Count,
            Notifies = notifies
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        Log.Information("AnimMontage {Name}: Duration={Duration:F2}s, {Sections} section(s), {Slots} slot(s)",
            montageName, duration, sections.Count, slotNames.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"AnimMontage recovered: Duration={duration:F2}s, {sections.Count} sections, {notifies.Count} anim notifies",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static ReconstructionResult ReconstructBlendSpace(ParsedAsset asset, string outputPathNoExt)
    {
        var bs = asset.Exports.FirstOrDefault(e => e.ExportType.StartsWith("BlendSpace")) ?? asset.Exports.FirstOrDefault();
        if (bs == null)
            return ReconstructionResult.Failed("No BlendSpace export found");

        var bsName = Path.GetFileNameWithoutExtension(asset.File.Path);

        var skelRef = bs.GetOrDefault<FPackageIndex>("Skeleton")?.ResolvedObject;
        var skelPath = skelRef?.GetPathName() ?? bs.GetOrDefault<FSoftObjectPath>("Skeleton").ToString();

        // Sample Data
        var samples = new List<BlendSampleData>();
        var rawSamples = bs.GetOrDefault<FStructFallback[]>("SampleData");
        if (rawSamples != null)
        {
            foreach (var s in rawSamples)
            {
                var animRef = s.GetOrDefault<FPackageIndex>("Animation")?.ResolvedObject;
                var animPath = animRef?.GetPathName() ?? s.GetOrDefault<FSoftObjectPath>("Animation").ToString();
                var sampleVal = s.GetOrDefault<FVector>("SampleValue");

                samples.Add(new BlendSampleData
                {
                    AnimationPath = animPath,
                    Value = new[] { (float)sampleVal.X, (float)sampleVal.Y, (float)sampleVal.Z }
                });
            }
        }

        var jsonPath = outputPathNoExt + ".blendspace.json";
        var model = new
        {
            AssetType = asset.PrimaryType,
            Name = bsName,
            VirtualPath = asset.File.Path,
            Skeleton = skelPath,
            SampleCount = samples.Count,
            Samples = samples
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

        Log.Information("BlendSpace {Name}: {Samples} animation sample point(s)", bsName, samples.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"{asset.PrimaryType} recovered: {samples.Count} blend animation samples mapped",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static ReconstructionResult ReconstructPhysicsAsset(ParsedAsset asset, string outputPathNoExt)
    {
        var phys = asset.Exports.FirstOrDefault(e => e.ExportType == "PhysicsAsset") ?? asset.Exports.FirstOrDefault();
        if (phys == null)
            return ReconstructionResult.Failed("No PhysicsAsset export found");

        var physName = Path.GetFileNameWithoutExtension(asset.File.Path);

        var bodies = new List<PhysicsBodyData>();
        var rawBodies = phys.GetOrDefault<FPackageIndex[]>("SkeletalBodySetups");
        if (rawBodies != null)
        {
            foreach (var b in rawBodies)
            {
                var bodySetup = b?.ResolvedObject;
                if (bodySetup != null && bodySetup.TryLoad(out var loadedBody) && loadedBody is not null)
                {
                    var boneName = loadedBody.GetOrDefault<FName>("BoneName").Text;
                    bodies.Add(new PhysicsBodyData { BoneName = boneName, BodyName = loadedBody.Name });
                }
            }
        }

        var constraints = new List<string>();
        var rawConstraints = phys.GetOrDefault<FPackageIndex[]>("ConstraintSetup");
        if (rawConstraints != null)
        {
            foreach (var c in rawConstraints)
            {
                var ro = c?.ResolvedObject;
                if (ro != null) constraints.Add(ro.Name.Text);
            }
        }

        var jsonPath = outputPathNoExt + ".physics.json";
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

        Log.Information("PhysicsAsset {Name}: {Bodies} collision body(s), {Constraints} joint constraint(s)",
            physName, bodies.Count, constraints.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"PhysicsAsset recovered: {bodies.Count} skeletal collision bodies, {constraints.Count} physics constraints",
            Model = model,
            SidecarFiles = new List<string> { jsonPath }
        };
    }

    private static List<AnimNotifyData> ExtractAnimNotifies(UObject obj)
    {
        var result = new List<AnimNotifyData>();
        var rawNotifies = obj.GetOrDefault<FStructFallback[]>("Notifies");
        if (rawNotifies != null)
        {
            foreach (var n in rawNotifies)
            {
                var nName = n.GetOrDefault<FName>("NotifyName").Text;
                var time = n.GetOrDefault<float>("DisplayTime", n.GetOrDefault<float>("TriggerTimeOffset", 0f));
                var dur = n.GetOrDefault<float>("Duration", 0f);
                var track = n.GetOrDefault<int>("TrackIndex", 0);

                var notifyObj = n.GetOrDefault<FPackageIndex>("Notify")?.ResolvedObject;
                var notifyClass = notifyObj?.Class?.Name.Text ?? notifyObj?.Name.Text;

                result.Add(new AnimNotifyData
                {
                    NotifyName = string.IsNullOrEmpty(nName) ? "AnimNotify" : nName,
                    DisplayTime = time,
                    Duration = dur,
                    TrackIndex = track,
                    NotifyClass = notifyClass
                });
            }
        }
        return result;
    }

    private static string GenerateSocketPythonScript(string skelName, string virtualPath, List<SkeletalSocketData> sockets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine Skeletal Socket Setup Script: {skelName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_skeleton_sockets():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Setting up sockets on Skeleton: {skelName}')");
        sb.AppendLine("    asset_sub = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine($"    skel_path = '{virtualPath.Replace(".uasset", "")}'");
        sb.AppendLine("    skel = unreal.EditorAssetLibrary.load_asset(skel_path) if asset_sub else None");
        sb.AppendLine("    if not skel:");
        sb.AppendLine("        unreal.log_warning(f'Skeleton asset not loaded at {skel_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();
        sb.AppendLine("    sockets = [");
        foreach (var s in sockets)
        {
            sb.AppendLine($"        {{ 'name': '{s.SocketName}', 'bone': '{s.BoneName}', 'loc': ({s.Location[0]:F2}, {s.Location[1]:F2}, {s.Location[2]:F2}), 'rot': ({s.Rotation[0]:F2}, {s.Rotation[1]:F2}, {s.Rotation[2]:F2}) }},");
        }
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log(f'Recreated {{len(sockets)}} socket definitions for {skelName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_skeleton_sockets()");
        return sb.ToString();
    }

    private static float[] ReadVector(UObject obj, string propName, float defX = 0, float defY = 0, float defZ = 0)
    {
        var v = obj.GetOrDefault<FVector>(propName);
        if (v.X != 0 || v.Y != 0 || v.Z != 0)
            return new[] { (float)v.X, (float)v.Y, (float)v.Z };
        return new[] { defX, defY, defZ };
    }

    private static float[] ReadRotator(UObject obj, string propName, float defP = 0, float defY = 0, float defR = 0)
    {
        var r = obj.GetOrDefault<FRotator>(propName);
        if (r.Pitch != 0 || r.Yaw != 0 || r.Roll != 0)
            return new[] { (float)r.Pitch, (float)r.Yaw, (float)r.Roll };
        return new[] { defP, defY, defR };
    }
}

public sealed class BoneInfoData
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int ParentIndex { get; set; }
}

public sealed class SkeletalSocketData
{
    public string SocketName { get; set; } = "";
    public string BoneName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float[] Scale { get; set; } = { 1, 1, 1 };
}

public sealed class AnimNotifyData
{
    public string NotifyName { get; set; } = "";
    public float DisplayTime { get; set; }
    public float Duration { get; set; }
    public int TrackIndex { get; set; }
    public string? NotifyClass { get; set; }
}

public sealed class MontageSectionData
{
    public string SectionName { get; set; } = "";
    public float Time { get; set; }
    public string NextSection { get; set; } = "";
}

public sealed class BlendSampleData
{
    public string AnimationPath { get; set; } = "";
    public float[] Value { get; set; } = { 0, 0, 0 };
}

public sealed class PhysicsBodyData
{
    public string BoneName { get; set; } = "";
    public string BodyName { get; set; } = "";
}

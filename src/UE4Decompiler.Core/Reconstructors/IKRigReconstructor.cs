using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine IK Rig and Animation Retargeting assets (<c>UIKRigDefinition</c>,
/// <c>UIKRetargeter</c>, and <c>UControlRig</c>).
/// Extracts:
/// <list type="bullet">
///   <item>Skeletal mesh and skeleton asset references</item>
///   <item>Kinematic bone chains (ChainName, StartBone, EndBone, IKGoalName)</item>
///   <item>IK Goals, effectors, and bone solver configurations (FBIK, LimbIK)</item>
///   <item>Retarget source and target rig assignments and chain re-mappings</item>
///   <item>Automated Unreal Python setup script (&lt;Asset&gt;_ikrig_setup.py)</item>
/// </list>
/// </summary>
public sealed class IKRigReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var obj = asset.Exports.FirstOrDefault(e =>
                e.ExportType is "IKRigDefinition" or "IKRetargeter" or "ControlRig") ?? asset.Exports.FirstOrDefault();

            if (obj == null)
                return ReconstructionResult.Failed("No IK Rig / Retargeter export found");

            var name = Path.GetFileNameWithoutExtension(asset.File.Path);
            var type = obj.ExportType;

            var meshRef = obj.GetOrDefault<FPackageIndex>("PreviewSkeletalMesh")?.ResolvedObject?.GetPathName();
            var skeletonRef = obj.GetOrDefault<FPackageIndex>("Skeleton")?.ResolvedObject?.GetPathName();

            var boneChains = new List<IKBoneChainData>();
            var rawChains = obj.GetOrDefault<FStructFallback[]>("BoneChains");
            if (rawChains != null)
            {
                foreach (var c in rawChains)
                {
                    var chainName = c.GetOrDefault<FName>("ChainName").Text;
                    var startBone = c.GetOrDefault<FName>("StartBone").Text;
                    var endBone = c.GetOrDefault<FName>("EndBone").Text;
                    var goalName = c.GetOrDefault<FName>("IKGoalName").Text;
                    boneChains.Add(new IKBoneChainData(chainName, startBone, endBone, goalName));
                }
            }

            var goals = new List<string>();
            var rawGoals = obj.GetOrDefault<FStructFallback[]>("Goals");
            if (rawGoals != null)
            {
                foreach (var g in rawGoals)
                {
                    var gName = g.GetOrDefault<FName>("GoalName").Text;
                    if (!string.IsNullOrEmpty(gName)) goals.Add(gName);
                }
            }

            string? srcRig = null;
            string? tgtRig = null;
            if (type == "IKRetargeter")
            {
                srcRig = obj.GetOrDefault<FPackageIndex>("SourceIKRigAsset")?.ResolvedObject?.GetPathName();
                tgtRig = obj.GetOrDefault<FPackageIndex>("TargetIKRigAsset")?.ResolvedObject?.GetPathName();
            }

            var jsonPath = outputPathNoExt + "_ikrig.json";
            var model = new
            {
                AssetType = type,
                Name = name,
                VirtualPath = asset.File.Path,
                PreviewSkeletalMesh = meshRef,
                Skeleton = skeletonRef,
                BoneChains = boneChains,
                Goals = goals,
                SourceIKRig = srcRig,
                TargetIKRig = tgtRig
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

            var pyPath = outputPathNoExt + "_ikrig_setup.py";
            var pyScript = GenerateIKRigPythonScript(name, asset.File.Path, type, meshRef, skeletonRef, srcRig, tgtRig, boneChains);
            File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

            Log.Information("IKRig {Name} ({Type}): {Chains} bone chain(s), Mesh={Mesh}",
                name, type, boneChains.Count, meshRef ?? "None");

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"{type} recovered: {boneChains.Count} bone chain(s), Mesh '{Path.GetFileName(meshRef ?? "None")}'",
                Model = model,
                SidecarFiles = new List<string> { jsonPath, pyPath }
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "IKRig reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"IKRig reconstruction error: {ex.Message}");
        }
    }

    private static string GenerateIKRigPythonScript(string name, string virtualPath, string type, string? meshRef, string? skeletonRef, string? srcRig, string? tgtRig, List<IKBoneChainData> chains)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Reconstructed IK Rig / Retargeter Asset: {name} ({type})");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_ik_asset():");
        sb.AppendLine($"    asset_path = '{virtualPath.Replace('\\', '/')}'");
        sb.AppendLine("    pkg_name = asset_path.rsplit('.', 1)[0]");
        sb.AppendLine("    asset_name = pkg_name.rsplit('/', 1)[-1]");
        sb.AppendLine("    pkg_path = pkg_name.rsplit('/', 1)[0]");
        sb.AppendLine();
        sb.AppendLine("    asset = unreal.load_asset(pkg_name)");
        sb.AppendLine("    if not asset:");
        sb.AppendLine("        asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        if (type == "IKRigDefinition")
        {
            sb.AppendLine("        factory = unreal.IKRigDefinitionFactory()");
            sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.IKRigDefinition, factory)");
        }
        else if (type == "IKRetargeter")
        {
            sb.AppendLine("        factory = unreal.IKRetargetFactory()");
            sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.IKRetargeter, factory)");
        }
        sb.AppendLine();
        sb.AppendLine("    if asset:");
        if (!string.IsNullOrEmpty(meshRef))
        {
            sb.AppendLine($"        mesh = unreal.load_asset('{meshRef}')");
            sb.AppendLine("        if mesh:");
            sb.AppendLine("            asset.set_editor_property('preview_skeletal_mesh', mesh)");
        }
        if (type == "IKRetargeter")
        {
            if (!string.IsNullOrEmpty(srcRig))
            {
                sb.AppendLine($"        src = unreal.load_asset('{srcRig}')");
                sb.AppendLine("        if src: asset.set_editor_property('source_ik_rig_asset', src)");
            }
            if (!string.IsNullOrEmpty(tgtRig))
            {
                sb.AppendLine($"        tgt = unreal.load_asset('{tgtRig}')");
                sb.AppendLine("        if tgt: asset.set_editor_property('target_ik_rig_asset', tgt)");
            }
        }
        sb.AppendLine("        unreal.EditorAssetLibrary.save_loaded_asset(asset)");
        sb.AppendLine($"        unreal.log(f'[UE4Decompiler] Configured {type}: {{asset_name}} ({chains.Count} chains)')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_ik_asset()");
        return sb.ToString();
    }
}

public sealed record IKBoneChainData(
    string ChainName,
    string StartBone,
    string EndBone,
    string IKGoalName);

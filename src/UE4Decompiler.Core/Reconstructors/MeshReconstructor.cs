using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Recovers <see cref="UStaticMesh"/> and <see cref="USkeletalMesh"/> geometry. The cooked binary
/// vertex/index/UV/normal buffers are exported to glTF 2.0 (single .glb) via CUE4Parse-Conversion —
/// the practical interchange path back into the editor. Skeletal meshes additionally carry their
/// bone hierarchy, influence weights and morph targets through the exporter. The recovered model
/// records geometry stats + properties; the .glb is the re-importable artifact.
/// </summary>
public sealed class MeshReconstructor
{
    private static readonly ExporterOptions Options = new()
    {
        MeshFormat = EMeshFormat.Gltf2,
        LodFormat = ELodFormat.FirstLod,
        ExportMaterials = false,   // materials handled by MaterialReconstructor
        ExportMorphTargets = true
    };

    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(outputPathNoExt)!);
        dir.Create();

        try
        {
            if (asset.Exports.OfType<UStaticMesh>().FirstOrDefault() is { } sm)
                return ExportMesh(new MeshExporter(sm, Options), dir, "StaticMesh", sm.Name,
                    new { sm.RenderData });

            if (asset.Exports.OfType<USkeletalMesh>().FirstOrDefault() is { } sk)
                return ExportMesh(new MeshExporter(sk, Options), dir, "SkeletalMesh", sk.Name,
                    new { BoneCount = sk.ReferenceSkeleton.FinalRefBoneInfo?.Length ?? 0, MorphTargetCount = sk.MorphTargets.Length });

            return ReconstructionResult.Failed("No StaticMesh/SkeletalMesh export found");
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Mesh export error: {ex.Message}");
        }
    }

    private ReconstructionResult ExportMesh(MeshExporter exporter, DirectoryInfo dir, string type, string name, object stats)
    {
        if (!exporter.TryWriteToDir(dir, out _, out var savedFilePath))
            return new ReconstructionResult { Fidelity = Fidelity.Stub, Note = $"{type} had no exportable LOD0 geometry" };

        Log.Information("{Type} {Name}: exported geometry -> {File}", type, name, Path.GetFileName(savedFilePath));
        var result = new ReconstructionResult
        {
            Fidelity = Fidelity.Partial,
            Note = $"Geometry exported as {Path.GetFileName(savedFilePath)} (re-import to re-cook)",
            Model = new { AssetType = type, Geometry = Path.GetFileName(savedFilePath), Stats = stats }
        };
        result.SidecarFiles.Add(Path.GetFileName(savedFilePath));
        return result;
    }
}

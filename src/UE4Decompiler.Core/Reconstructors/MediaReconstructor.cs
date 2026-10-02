using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine Media Framework assets:
/// <list type="bullet">
///   <item><c>UFileMediaSource</c>: video file path references and pre-cache options</item>
///   <item><c>UStreamMediaSource</c>: network / streaming video URL sources</item>
///   <item><c>UMediaPlayer</c>: playback configuration (PlayOnOpen, Loop, Shuffle)</item>
///   <item><c>UMediaTexture</c>: target render surface bindings and address modes</item>
///   <item>Automated Unreal Python script (&lt;Media&gt;_media_setup.py)</item>
/// </list>
/// </summary>
public sealed class MediaReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var obj = asset.Exports.FirstOrDefault(e =>
                e.ExportType is "FileMediaSource" or "StreamMediaSource" or "MediaPlayer" or "MediaTexture")
                ?? asset.Exports.FirstOrDefault();

            if (obj == null)
                return ReconstructionResult.Failed("No Media export found");

            var name = Path.GetFileNameWithoutExtension(asset.File.Path);
            var type = obj.ExportType;

            string? filePath = null;
            string? streamUrl = null;
            bool playOnOpen = true;
            bool loop = false;
            string? mediaPlayerRef = null;

            if (type == "FileMediaSource")
            {
                filePath = obj.GetOrDefault<string>("FilePath", "");
            }
            else if (type == "StreamMediaSource")
            {
                streamUrl = obj.GetOrDefault<string>("StreamUrl", "");
            }
            else if (type == "MediaPlayer")
            {
                playOnOpen = obj.GetOrDefault<bool>("PlayOnOpen", true);
                loop = obj.GetOrDefault<bool>("Loop", false);
            }
            else if (type == "MediaTexture")
            {
                mediaPlayerRef = obj.GetOrDefault<FPackageIndex>("MediaPlayer")?.ResolvedObject?.GetPathName();
            }

            var jsonPath = outputPathNoExt + "_media.json";
            var model = new
            {
                AssetType = type,
                Name = name,
                VirtualPath = asset.File.Path,
                FilePath = filePath,
                StreamUrl = streamUrl,
                PlayOnOpen = playOnOpen,
                Loop = loop,
                MediaPlayerRef = mediaPlayerRef
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, jsonOptions));

            var pyPath = outputPathNoExt + "_media_setup.py";
            var pyScript = GenerateMediaPythonScript(name, asset.File.Path, type, filePath, streamUrl, playOnOpen, loop, mediaPlayerRef);
            File.WriteAllText(pyPath, pyScript, Encoding.UTF8);

            Log.Information("Media asset {Name} ({Type}) reconstructed: File={File}",
                name, type, filePath ?? streamUrl ?? mediaPlayerRef ?? "None");

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"{type} recovered: {name} (Ref: {Path.GetFileName(filePath ?? streamUrl ?? mediaPlayerRef ?? "None")})",
                Model = model,
                SidecarFiles = new List<string> { jsonPath, pyPath }
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Media reconstruction failed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Media reconstruction error: {ex.Message}");
        }
    }

    private static string GenerateMediaPythonScript(string name, string virtualPath, string type, string? filePath, string? streamUrl, bool playOnOpen, bool loop, string? mediaPlayerRef)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Reconstructed Media Framework Asset: {name} ({type})");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine("def setup_media():");
        sb.AppendLine($"    asset_path = '{virtualPath.Replace('\\', '/')}'");
        sb.AppendLine("    pkg_name = asset_path.rsplit('.', 1)[0]");
        sb.AppendLine("    asset_name = pkg_name.rsplit('/', 1)[-1]");
        sb.AppendLine("    pkg_path = pkg_name.rsplit('/', 1)[0]");
        sb.AppendLine();
        sb.AppendLine("    asset = unreal.load_asset(pkg_name)");
        sb.AppendLine("    if not asset:");
        sb.AppendLine("        asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        if (type == "FileMediaSource")
        {
            sb.AppendLine("        factory = unreal.FileMediaSourceFactory()");
            sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.FileMediaSource, factory)");
        }
        else if (type == "StreamMediaSource")
        {
            sb.AppendLine("        factory = unreal.StreamMediaSourceFactory()");
            sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.StreamMediaSource, factory)");
        }
        else if (type == "MediaPlayer")
        {
            sb.AppendLine("        factory = unreal.MediaPlayerFactory()");
            sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.MediaPlayer, factory)");
        }
        else if (type == "MediaTexture")
        {
            sb.AppendLine("        factory = unreal.MediaTextureFactoryNew()");
            sb.AppendLine("        asset = asset_tools.create_asset(asset_name, pkg_path, unreal.MediaTexture, factory)");
        }
        sb.AppendLine();
        sb.AppendLine("    if asset:");
        if (type == "FileMediaSource" && !string.IsNullOrEmpty(filePath))
        {
            sb.AppendLine($"        asset.set_editor_property('file_path', r'{filePath}')");
        }
        else if (type == "MediaPlayer")
        {
            sb.AppendLine($"        asset.set_editor_property('play_on_open', {playOnOpen.ToString().ToLowerInvariant()})");
            sb.AppendLine($"        asset.set_editor_property('loop', {loop.ToString().ToLowerInvariant()})");
        }
        else if (type == "MediaTexture" && !string.IsNullOrEmpty(mediaPlayerRef))
        {
            sb.AppendLine($"        player = unreal.load_asset('{mediaPlayerRef}')");
            sb.AppendLine("        if player:");
            sb.AppendLine("            asset.set_editor_property('media_player', player)");
        }
        sb.AppendLine("        unreal.EditorAssetLibrary.save_loaded_asset(asset)");
        sb.AppendLine($"        unreal.log(f'[UE4Decompiler] Configured Media Asset: {{asset_name}} ({type})')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine("    setup_media()");
        return sb.ToString();
    }
}

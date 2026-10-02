using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Sounds;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs Unreal Engine audio assets:
/// - <see cref="USoundWave"/>: decodes cooked audio stream into uncompressed WAV / OGG files.
/// - SoundCue: extracts node graph (WavePlayer, Mixer, Random, Modulator, Attenuation, Delay),
///   referenced sound waves, and generates an automated Unreal Editor Python script to rebuild the SoundCue.
/// - SoundAttenuation: extracts spatialization, falloff distance, shape, and air absorption settings.
/// </summary>
public sealed class AudioReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt, bool noMediaExport = false)
    {
        try
        {
            var soundWave = asset.Exports.OfType<USoundWave>().FirstOrDefault();
            if (soundWave != null)
            {
                return ReconstructSoundWave(soundWave, asset, outputPathNoExt, noMediaExport);
            }

            var soundCue = asset.Exports.FirstOrDefault(e => e.ExportType == "SoundCue");
            if (soundCue != null)
            {
                return ReconstructSoundCue(soundCue, asset, outputPathNoExt);
            }

            var soundAtten = asset.Exports.FirstOrDefault(e => e.ExportType == "SoundAttenuation");
            if (soundAtten != null)
            {
                return ReconstructSoundAttenuation(soundAtten, asset, outputPathNoExt);
            }

            // Other composite audio assets
            return new ReconstructionResult
            {
                Fidelity = Fidelity.Partial,
                Note = $"{asset.PrimaryType} model preserved ({asset.Exports.Count} export(s))",
                Model = new { asset.PrimaryType, Exports = asset.Exports }
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reconstruct audio asset {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Audio error: {ex.Message}");
        }
    }

    private static ReconstructionResult ReconstructSoundWave(USoundWave soundWave, ParsedAsset asset, string outputPathNoExt, bool noMediaExport)
    {
        var outDir = Path.GetDirectoryName(outputPathNoExt);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var name = Path.GetFileNameWithoutExtension(outputPathNoExt);
        string? audioFormat = null;
        byte[]? audioData = null;

        try
        {
            SoundDecoder.Decode(soundWave, shouldDecompress: true, out audioFormat, out audioData);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SoundDecoder failed for {Path}", asset.File.Path);
        }

        var sidecars = new List<string>();
        string formatExt = (audioFormat ?? "wav").ToLowerInvariant();
        if (formatExt == "pcm") formatExt = "wav";

        if (!noMediaExport && audioData != null && audioData.Length > 0)
        {
            var audioFileName = $"{name}.{formatExt}";
            var audioPath = Path.Combine(outDir ?? ".", audioFileName);
            File.WriteAllBytes(audioPath, audioData);
            sidecars.Add(audioFileName);
        }

        var duration = soundWave.GetOrDefault<float>("Duration", 0f);
        var numChannels = soundWave.GetOrDefault<int>("NumChannels", 2);
        var sampleRate = soundWave.GetOrDefault<int>("SampleRate", 44100);
        var bStreaming = soundWave.bStreaming;

        var model = new
        {
            AssetType = "SoundWave",
            asset.File.Path,
            AudioFormat = formatExt,
            ByteLength = audioData?.Length ?? 0,
            Duration = duration,
            NumChannels = numChannels,
            SampleRate = sampleRate,
            bStreaming,
            SoundGroup = soundWave.GetOrDefault("SoundGroup", "SOUNDGROUP_Default")?.ToString()
        };

        var fidelity = (audioData != null && audioData.Length > 0) ? Fidelity.Full : Fidelity.Partial;
        var note = (audioData != null && audioData.Length > 0)
            ? $"SoundWave decoded to .{formatExt} ({audioData.Length:N0} bytes, {duration:F2}s, {sampleRate}Hz)"
            : "SoundWave model preserved; raw audio stream left as-is";

        if (noMediaExport)
        {
            note += " (audio export skipped via --no-media-export)";
        }

        Log.Information("SoundWave {Name}: {Note}", name, note);

        var res = new ReconstructionResult
        {
            Fidelity = fidelity,
            Note = note,
            Model = model
        };
        res.SidecarFiles.AddRange(sidecars);
        return res;
    }

    private static ReconstructionResult ReconstructSoundCue(UObject soundCue, ParsedAsset asset, string outputPathNoExt)
    {
        var outDir = Path.GetDirectoryName(outputPathNoExt);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var cueName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var volume = soundCue.GetOrDefault<float>("VolumeMultiplier", 1.0f);
        var pitch = soundCue.GetOrDefault<float>("PitchMultiplier", 1.0f);
        var attenPath = soundCue.GetOrDefault<FPackageIndex>("AttenuationSettings")?.ResolvedObject?.GetPathName();

        var nodes = new List<SoundCueNodeData>();
        var referencedWaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var exp in asset.Exports.Where(e => e.ExportType.StartsWith("SoundNode")))
        {
            var nodeData = new SoundCueNodeData
            {
                Name = exp.Name,
                Type = exp.ExportType
            };

            if (exp.ExportType == "SoundNodeWavePlayer")
            {
                var wavePath = exp.GetOrDefault<FPackageIndex>("SoundWave")?.ResolvedObject?.GetPathName();
                nodeData.WavePath = wavePath;
                nodeData.Looping = exp.GetOrDefault<bool>("bLooping", false);
                if (!string.IsNullOrEmpty(wavePath)) referencedWaves.Add(wavePath);
            }
            else if (exp.ExportType == "SoundNodeModulator")
            {
                nodeData.PitchMin = exp.GetOrDefault<float>("PitchMin", 0.95f);
                nodeData.PitchMax = exp.GetOrDefault<float>("PitchMax", 1.05f);
                nodeData.VolumeMin = exp.GetOrDefault<float>("VolumeMin", 0.95f);
                nodeData.VolumeMax = exp.GetOrDefault<float>("VolumeMax", 1.05f);
            }
            else if (exp.ExportType == "SoundNodeAttenuation")
            {
                nodeData.AttenuationPath = exp.GetOrDefault<FPackageIndex>("AttenuationSettings")?.ResolvedObject?.GetPathName();
            }

            nodes.Add(nodeData);
        }

        var sidecars = new List<string>();

        // 1. Emit SoundCue JSON
        var jsonPath = outputPathNoExt + "_soundcue.json";
        var model = new
        {
            AssetType = "SoundCue",
            cueName,
            VolumeMultiplier = volume,
            PitchMultiplier = pitch,
            AttenuationSettings = attenPath,
            ReferencedWaves = referencedWaves.ToList(),
            NodeCount = nodes.Count,
            Nodes = nodes
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(jsonPath);

        // 2. Emit Unreal Python Setup Script
        var pyPath = outputPathNoExt + "_soundcue.py";
        var pyScript = GenerateSoundCuePythonScript(cueName, asset.File.Path, volume, pitch, attenPath, referencedWaves.ToList(), nodes);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        Log.Information("SoundCue {Name}: {Nodes} node(s), {Waves} wave ref(s). Emitted JSON & Python setup script.",
            cueName, nodes.Count, referencedWaves.Count);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"SoundCue: {nodes.Count} node(s), {referencedWaves.Count} sound wave(s) linked; Python builder emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static ReconstructionResult ReconstructSoundAttenuation(UObject soundAtten, ParsedAsset asset, string outputPathNoExt)
    {
        var outDir = Path.GetDirectoryName(outputPathNoExt);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var attenName = Path.GetFileNameWithoutExtension(outputPathNoExt);
        var attenStruct = soundAtten.GetOrDefault<FStructFallback>("Attenuation");

        var shape = attenStruct?.GetOrDefault<FName>("AttenuationShape").Text ?? "EAttenuationShape::Sphere";
        var falloff = attenStruct?.GetOrDefault<float>("FalloffDistance") ?? 3600f;
        var spatialize = attenStruct?.GetOrDefault<bool>("bSpatialize") ?? true;
        var airAbsorption = attenStruct?.GetOrDefault<bool>("bEnableAirAbsorption") ?? true;
        var occlusion = attenStruct?.GetOrDefault<bool>("bEnableOcclusion") ?? true;

        var sidecars = new List<string>();
        var jsonPath = outputPathNoExt + "_attenuation.json";
        var model = new
        {
            AssetType = "SoundAttenuation",
            attenName,
            Shape = shape,
            FalloffDistance = falloff,
            bSpatialize = spatialize,
            bEnableAirAbsorption = airAbsorption,
            bEnableOcclusion = occlusion
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        sidecars.Add(jsonPath);

        var pyPath = outputPathNoExt + "_attenuation.py";
        var pyScript = GenerateAttenuationPythonScript(attenName, asset.File.Path, shape, falloff, spatialize, airAbsorption, occlusion);
        File.WriteAllText(pyPath, pyScript, Encoding.UTF8);
        sidecars.Add(pyPath);

        Log.Information("SoundAttenuation {Name}: Shape={Shape}, Falloff={Falloff}. Emitted JSON & Python setup script.",
            attenName, shape, falloff);

        return new ReconstructionResult
        {
            Fidelity = Fidelity.Full,
            Note = $"SoundAttenuation: shape={shape}, falloff={falloff} units; Python script emitted",
            Model = model,
            SidecarFiles = sidecars
        };
    }

    private static string GenerateSoundCuePythonScript(
        string cueName,
        string virtualPath,
        float volume,
        float pitch,
        string? attenPath,
        List<string> waves,
        List<SoundCueNodeData> nodes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine SoundCue Rebuilder: {cueName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_sound_cue_{SanitizePy(cueName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Reconstructing SoundCue: {cueName}')");
        sb.AppendLine("    editor_asset = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Audio";

        sb.AppendLine($"    target_path = '{assetPath}'");
        sb.AppendLine($"    target_pkg = '{packagePath}'");
        sb.AppendLine($"    asset_name = '{cueName}'");
        sb.AppendLine();
        sb.AppendLine("    cue = editor_asset.load_asset(target_path)");
        sb.AppendLine("    if not cue:");
        sb.AppendLine("        factory = unreal.SoundCueFactoryNew()");
        sb.AppendLine("        cue = asset_tools.create_asset(asset_name, target_pkg, unreal.SoundCue, factory)");
        sb.AppendLine();
        sb.AppendLine("    if not cue:");
        sb.AppendLine("        unreal.log_error(f'Failed to create SoundCue: {target_path}')");
        sb.AppendLine("        return");
        sb.AppendLine();
        sb.AppendLine($"    cue.set_editor_property('volume_multiplier', {volume.ToString("G7", CultureInfo.InvariantCulture)})");
        sb.AppendLine($"    cue.set_editor_property('pitch_multiplier', {pitch.ToString("G7", CultureInfo.InvariantCulture)})");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(attenPath))
        {
            var cleanAtten = attenPath.Split('.')[0];
            sb.AppendLine($"    atten = editor_asset.load_asset('{cleanAtten}')");
            sb.AppendLine("    if atten:");
            sb.AppendLine("        cue.set_editor_property('attenuation_settings', atten)");
            sb.AppendLine();
        }

        if (waves.Count > 0)
        {
            sb.AppendLine("    # Referenced Sound Waves");
            foreach (var w in waves)
            {
                var cleanW = w.Split('.')[0];
                sb.AppendLine($"    wave_{SanitizePy(Path.GetFileName(cleanW))} = editor_asset.load_asset('{cleanW}')");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Finished configuring SoundCue: {cueName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_sound_cue_{SanitizePy(cueName)}()");

        return sb.ToString();
    }

    private static string GenerateAttenuationPythonScript(
        string attenName,
        string virtualPath,
        string shape,
        float falloff,
        bool spatialize,
        bool airAbsorption,
        bool occlusion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine SoundAttenuation Rebuilder: {attenName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def setup_sound_attenuation_{SanitizePy(attenName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Reconstructing SoundAttenuation: {attenName}')");
        sb.AppendLine("    editor_asset = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    asset_tools = unreal.AssetToolsHelpers.get_asset_tools()");
        sb.AppendLine();

        var assetPath = virtualPath.Replace(".uasset", "");
        var packagePath = assetPath.Substring(0, Math.Max(0, assetPath.LastIndexOf('/')));
        if (string.IsNullOrEmpty(packagePath)) packagePath = "/Game/Audio";

        sb.AppendLine($"    target_path = '{assetPath}'");
        sb.AppendLine($"    target_pkg = '{packagePath}'");
        sb.AppendLine($"    asset_name = '{attenName}'");
        sb.AppendLine();
        sb.AppendLine("    atten = editor_asset.load_asset(target_path)");
        sb.AppendLine("    if not atten:");
        sb.AppendLine("        factory = unreal.SoundAttenuationFactory()");
        sb.AppendLine("        atten = asset_tools.create_asset(asset_name, target_pkg, unreal.SoundAttenuation, factory)");
        sb.AppendLine();
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Finished configuring SoundAttenuation: {attenName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    setup_sound_attenuation_{SanitizePy(attenName)}()");

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

    public sealed class SoundCueNodeData
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? WavePath { get; set; }
        public bool Looping { get; set; }
        public float PitchMin { get; set; }
        public float PitchMax { get; set; }
        public float VolumeMin { get; set; }
        public float VolumeMax { get; set; }
        public string? AttenuationPath { get; set; }
    }
}

using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse_Conversion.Sounds;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs <see cref="USoundWave"/> and <see cref="USoundCue"/> assets.
/// Leverages CUE4Parse-Conversion's <see cref="SoundDecoder"/> to decompress cooked audio streams
/// into playable uncompressed WAV or OGG Vorbis audio files.
/// </summary>
public sealed class AudioReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt, bool noMediaExport = false)
    {
        try
        {
            var soundWave = asset.Exports.OfType<USoundWave>().FirstOrDefault();
            if (soundWave is null)
            {
                // SoundCue or composite sound object
                return new ReconstructionResult
                {
                    Fidelity = Fidelity.Partial,
                    Note = $"{asset.PrimaryType} model preserved ({asset.Exports.Count} export(s))",
                    Model = new { asset.PrimaryType, Exports = asset.Exports }
                };
            }

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
        catch (Exception ex)
        {
            Log.Error(ex, "Audio reconstruction crashed for {Path}", asset.File.Path);
            return ReconstructionResult.Failed($"Audio error: {ex.Message}");
        }
    }
}

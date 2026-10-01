using System.Runtime.InteropServices;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Utils;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Provides system health diagnostics and actionable troubleshooting for 'doctor' (Phase 26).
/// </summary>
public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly IAssetDiscoveryService _discovery;

    public DiagnosticsService(IAssetDiscoveryService? discovery = null)
    {
        _discovery = discovery ?? new AssetDiscoveryService();
    }

    public Task<DiagnosticReport> RunDiagnosticsAsync(string? inputPath, string? outputPath, CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();

        // 1. Runtime check
        items.Add(new DiagnosticItem(
            Category: "Runtime",
            CheckName: ".NET Environment",
            Level: DiagnosticLevel.Pass,
            Message: $".NET Version: {Environment.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})"
        ));

        // 2. Output write access check
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                var fullOut = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(fullOut);
                var testFile = Path.Combine(fullOut, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                items.Add(new DiagnosticItem(
                    Category: "Filesystem",
                    CheckName: "Output Writable",
                    Level: DiagnosticLevel.Pass,
                    Message: $"Output path is writable: {fullOut}"
                ));
            }
            catch (Exception ex)
            {
                items.Add(new DiagnosticItem(
                    Category: "Filesystem",
                    CheckName: "Output Writable",
                    Level: DiagnosticLevel.Error,
                    Message: $"Cannot write to output path: {ex.Message}",
                    SuggestedFix: "Ensure you have write permissions for the specified directory or run with appropriate user privileges."
                ));
            }
        }

        // 3. Input path and container check
        if (!string.IsNullOrWhiteSpace(inputPath))
        {
            if (File.Exists(inputPath) || Directory.Exists(inputPath))
            {
                items.Add(new DiagnosticItem(
                    Category: "Input",
                    CheckName: "Input Path",
                    Level: DiagnosticLevel.Pass,
                    Message: $"Input target exists: {inputPath}"
                ));

                var containers = _discovery.DiscoverContainers(inputPath);
                if (containers.Count > 0)
                {
                    var paks = containers.Count(c => c.Type == ContainerType.Pak);
                    var utocs = containers.Count(c => c.Type == ContainerType.IoStoreUtoc);
                    items.Add(new DiagnosticItem(
                        Category: "Containers",
                        CheckName: "Container Discovery",
                        Level: DiagnosticLevel.Pass,
                        Message: $"Found {containers.Count} container(s): {paks} PAK, {utocs} IoStore (.utoc/.ucas)"
                    ));

                    // Check for missing .ucas sibling
                    foreach (var c in containers.Where(c => c.Type == ContainerType.IoStoreUtoc))
                    {
                        var ucas = Path.ChangeExtension(c.FilePath, ".ucas");
                        if (!File.Exists(ucas))
                        {
                            items.Add(new DiagnosticItem(
                                Category: "Containers",
                                CheckName: "IoStore Integrity",
                                Level: DiagnosticLevel.Error,
                                Message: $"Missing .ucas container data for {Path.GetFileName(c.FilePath)}",
                                SuggestedFix: "Ensure both .utoc and .ucas files are in the same directory."
                            ));
                        }
                    }
                }
                else
                {
                    items.Add(new DiagnosticItem(
                        Category: "Containers",
                        CheckName: "Container Discovery",
                        Level: DiagnosticLevel.Warning,
                        Message: "No .pak or .utoc containers found directly in input path.",
                        SuggestedFix: "Point to the game's Content/Paks folder containing .pak or .utoc files."
                    ));
                }
            }
            else
            {
                items.Add(new DiagnosticItem(
                    Category: "Input",
                    CheckName: "Input Path",
                    Level: DiagnosticLevel.Error,
                    Message: $"Input path does not exist: {inputPath}",
                    SuggestedFix: "Verify the path to your Unreal Engine game files or Paks directory."
                ));
            }
        }

        // 4. Graphics & Native Decoders check (SkiaSharp)
        try
        {
            using var bmp = new SkiaSharp.SKBitmap(1, 1);
            using var canvas = new SkiaSharp.SKCanvas(bmp);
            canvas.Clear(SkiaSharp.SKColors.Red);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var encoded = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

            items.Add(new DiagnosticItem(
                Category: "Dependencies",
                CheckName: "SkiaSharp Native Graphics",
                Level: DiagnosticLevel.Pass,
                Message: "SkiaSharp native rendering and PNG encoding operational"
            ));
        }
        catch (Exception ex)
        {
            items.Add(new DiagnosticItem(
                Category: "Dependencies",
                CheckName: "SkiaSharp Native Graphics",
                Level: DiagnosticLevel.Error,
                Message: $"SkiaSharp initialization failed: {ex.Message}",
                SuggestedFix: "Ensure native libSkiaSharp library is installed or package runtimes are present."
            ));
        }

        // 5. CUE4Parse core capabilities check
        items.Add(new DiagnosticItem(
            Category: "Capabilities",
            CheckName: "Engine Support",
            Level: DiagnosticLevel.Info,
            Message: "UE 4.0-4.27 (Parsing & Asset Recovery); UE 4.21 (Editor-Loadable Uncooked Package Writing); UE 5.0-5.5 (Parsing, glTF, JSON IR, PNG exports)"
        ));

        return Task.FromResult(new DiagnosticReport { Items = items });
    }
}

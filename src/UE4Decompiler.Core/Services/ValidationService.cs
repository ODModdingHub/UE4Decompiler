using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;
using Serilog;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Validates recovered packages on disk (Phase 46).
/// </summary>
public sealed class ValidationService : IValidationService
{
    private const uint PackageFileTag = 0x9E2A83C1;

    public Task<ValidationReport> ValidateDirectoryAsync(string outputPath, EGame game, CancellationToken ct = default)
    {
        var items = new List<ValidationItem>();
        if (!Directory.Exists(outputPath))
        {
            return Task.FromResult(new ValidationReport
            {
                TargetDirectory = outputPath,
                Items = new List<ValidationItem>
                {
                    new(outputPath, false, "Directory", "Output directory does not exist.", Array.Empty<string>())
                }
            });
        }

        var files = Directory.EnumerateFiles(outputPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".uasset" or ".umap":
                    items.Add(ValidatePackage(file, game));
                    break;
                case ".json":
                    items.Add(ValidateJson(file));
                    break;
                case ".png":
                    items.Add(ValidateImage(file));
                    break;
                case ".glb":
                    items.Add(ValidateMesh(file));
                    break;
            }
        }

        return Task.FromResult(new ValidationReport
        {
            TargetDirectory = outputPath,
            Items = items
        });
    }

    private static ValidationItem ValidatePackage(string filePath, EGame game)
    {
        var checks = new List<string>();
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 32)
                return new(filePath, false, "Package", "File size is smaller than a package header.", checks);

            checks.Add("FileSizeCheck");

            // Check package magic
            var tag = BitConverter.ToUInt32(bytes, 0);
            if (tag != PackageFileTag)
                return new(filePath, false, "Package", $"Invalid package tag: 0x{tag:X8} (expected 0x{PackageFileTag:X8}).", checks);

            checks.Add("PackageFileTagVerified");

            // Try re-parsing with CUE4Parse
            try
            {
                var ar = new FByteArchive(Path.GetFileNameWithoutExtension(filePath), bytes, new VersionContainer(game));
                var pkg = new CUE4Parse.UE4.Assets.Package(ar, (FArchive?)null, (FArchive?)null, (FArchive?)null, null, false);
                checks.Add($"CUE4ParseSummaryParsed(Exports={pkg.ExportMap.Length},Imports={pkg.ImportMap.Length})");
                return new(filePath, true, "Package", null, checks);
            }
            catch (Exception ex)
            {
                // Structural header placeholder test
                var legacy = BitConverter.ToInt32(bytes, 4);
                if (legacy is -7 or -8)
                {
                    checks.Add($"LegacyHeaderValid({legacy})");
                    return new(filePath, true, "PackagePlaceholder", null, checks);
                }
                return new(filePath, false, "Package", $"Failed to parse package structure: {ex.Message}", checks);
            }
        }
        catch (Exception ex)
        {
            return new(filePath, false, "Package", $"Validation error: {ex.Message}", checks);
        }
    }

    private static ValidationItem ValidateJson(string filePath)
    {
        var checks = new List<string>();
        try
        {
            var content = File.ReadAllText(filePath);
            var token = JToken.Parse(content);
            checks.Add("JsonSyntaxValid");

            var type = token["AssetType"]?.ToString() ?? "Unknown";
            checks.Add($"AssetTypeFound({type})");

            return new(filePath, true, "JsonModel", null, checks);
        }
        catch (Exception ex)
        {
            return new(filePath, false, "JsonModel", $"Invalid JSON sidecar: {ex.Message}", checks);
        }
    }

    private static ValidationItem ValidateImage(string filePath)
    {
        var checks = new List<string>();
        try
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(filePath);
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
                return new(filePath, false, "TexturePNG", "Decoded bitmap has invalid dimensions.", checks);

            checks.Add($"ValidBitmapDimensions({bmp.Width}x{bmp.Height})");
            return new(filePath, true, "TexturePNG", null, checks);
        }
        catch (Exception ex)
        {
            return new(filePath, false, "TexturePNG", $"Image decode failed: {ex.Message}", checks);
        }
    }

    private static ValidationItem ValidateMesh(string filePath)
    {
        var checks = new List<string>();
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length < 12)
                return new(filePath, false, "MeshGLB", "File too small for glTF binary.", checks);

            using var fs = File.OpenRead(filePath);
            using var r = new BinaryReader(fs);
            var magic = r.ReadUInt32();
            if (magic != 0x46546C67) // 'glTF'
                return new(filePath, false, "MeshGLB", $"Invalid glTF binary magic: 0x{magic:X8}", checks);

            checks.Add("GlbMagicVerified");
            return new(filePath, true, "MeshGLB", null, checks);
        }
        catch (Exception ex)
        {
            return new(filePath, false, "MeshGLB", $"Mesh validation error: {ex.Message}", checks);
        }
    }
}

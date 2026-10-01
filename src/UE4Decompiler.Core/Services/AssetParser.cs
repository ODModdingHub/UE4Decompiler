using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using Serilog;

namespace UE4Decompiler.Core;

/// <summary>A package loaded + walked into a structured model the reconstructors consume.</summary>
public sealed class ParsedAsset
{
    public required GameFile File { get; init; }
    public required IPackage Package { get; init; }
    public required IReadOnlyList<UObject> Exports { get; init; }
    public required string PrimaryType { get; init; }

    /// <summary>Import map entries as "/Game/...|/Engine/..." object paths, used to rewrite references.</summary>
    public required IReadOnlyList<string> Imports { get; init; }

    public bool IsMap => File.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Loads packages through CUE4Parse and walks their export/import tables.</summary>
public sealed class AssetParser
{
    private readonly AbstractFileProvider _provider;

    public AssetParser(AbstractFileProvider provider) => _provider = provider;

    /// <summary>
    /// Load + parse one package. Returns null (and logs) on hard failure so the pipeline continues.
    /// All property deserialization is forced eagerly here so per-asset errors surface in one place.
    /// </summary>
    public ParsedAsset? Parse(GameFile file)
    {
        try
        {
            var package = _provider.LoadPackage(file);
            var exports = package.GetExports().ToList(); // forces lazy deserialization of every export

            var primary = ClassifyPrimary(exports);
            var imports = ExtractImports(package);

            return new ParsedAsset
            {
                File = file,
                Package = package,
                Exports = exports,
                PrimaryType = primary,
                Imports = imports
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse package {Path}", file.Path);
            return null;
        }
    }

    /// <summary>Pick the most descriptive export class to drive reconstructor routing.</summary>
    private static string ClassifyPrimary(IReadOnlyList<UObject> exports)
    {
        if (exports.Count == 0) return "None";

        // Prefer the export whose class is the package's "asset" rather than a sub-object.
        // Any *BlueprintGeneratedClass (Anim/Widget/plain) is the package's main object — match by suffix.
        var bgc = exports.FirstOrDefault(e =>
            ClassName(e).EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase));
        if (bgc is not null) return ClassName(bgc);

        string[] priority =
        {
            "World", "Level", "Blueprint",
            "Material", "MaterialInstanceConstant", "SkeletalMesh", "StaticMesh",
            "Texture2D", "TextureCube", "SoundWave", "AnimSequence", "Skeleton"
        };
        foreach (var want in priority)
        {
            var hit = exports.FirstOrDefault(e => ClassName(e).Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return ClassName(hit);
        }
        return ClassName(exports[^1]); // last export is conventionally the package's main object
    }

    public static string ClassName(UObject obj) => obj.Class?.Name.Text ?? obj.ExportType ?? "Object";

    private static IReadOnlyList<string> ExtractImports(IPackage package)
    {
        var result = new List<string>();
        // Walk every export's outer chain + properties is overkill; the resolved import names are
        // exposed lazily. We surface what CUE4Parse resolves for soft-reference rewriting downstream.
        try
        {
            for (var i = 1; i <= package.ImportMapLength; i++)
            {
                var resolved = package.ResolvePackageIndex(new CUE4Parse.UE4.Objects.UObject.FPackageIndex(package, -i));
                var name = resolved?.GetPathName();
                if (!string.IsNullOrEmpty(name)) result.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Import map walk incomplete for {Pkg}", package.Name);
        }
        return result;
    }
}

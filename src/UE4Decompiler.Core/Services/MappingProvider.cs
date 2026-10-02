using CUE4Parse.FileProvider;
using Serilog;
using UE4Decompiler.Core.Abstractions;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Discovers and loads Unreal Engine .usmap unversioned property mapping files.
/// Essential for UE5 unversioned package recovery (Phase 6).
/// </summary>
public sealed class MappingProvider : IMappingProvider
{
    public bool TryLoadMapping(string? mappingPath, AbstractFileProvider provider, out string? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(mappingPath))
        {
            return false;
        }

        if (!File.Exists(mappingPath))
        {
            message = $"Mapping file not found at: {mappingPath}";
            Log.Warning(message);
            return false;
        }

        try
        {
            Log.Information("Loading property mappings from {Path}", mappingPath);
            var usmap = new CUE4Parse.MappingsProvider.FileUsmapTypeMappingsProvider(mappingPath);
            usmap.Reload();

            // provider.MappingsContainer is assigned the loaded provider / mappings
            provider.MappingsContainer = usmap;
            message = $"Successfully loaded mappings from {Path.GetFileName(mappingPath)}";
            Log.Information(message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Exception loading mapping file: {ex.Message}";
            Log.Error(ex, "Failed to load mapping file {Path}", mappingPath);
            return false;
        }
    }

    public IReadOnlyList<string> FindMappingCandidates(string searchDirectory)
    {
        if (string.IsNullOrWhiteSpace(searchDirectory) || !Directory.Exists(searchDirectory))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(searchDirectory, "*.usmap", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Utils;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Discovers containers (.pak, .utoc/.ucas, loose files) and enumerates content assets (Phase 4 &amp; 7).
/// </summary>
public sealed class AssetDiscoveryService : IAssetDiscoveryService
{
    public IReadOnlyList<ContainerInfo> DiscoverContainers(string inputPath)
    {
        var containers = new List<ContainerInfo>();
        if (string.IsNullOrWhiteSpace(inputPath)) return containers;

        if (File.Exists(inputPath))
        {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
            var type = ext switch
            {
                ".pak" => ContainerType.Pak,
                ".utoc" or ".ucas" => ContainerType.IoStoreUtoc,
                _ => ContainerType.Loose
            };
            containers.Add(new ContainerInfo(
                FilePath: Path.GetFullPath(inputPath),
                Type: type,
                IsEncrypted: false,
                EncryptionKeyGuid: null,
                FileCount: 1,
                MountPoint: "/"
            ));
            return containers;
        }

        if (Directory.Exists(inputPath))
        {
            var fullDir = Path.GetFullPath(inputPath);
            try
            {
                // Recursive search for .pak and .utoc containers (Phase 4 improvement)
                foreach (var file in Directory.EnumerateFiles(fullDir, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".pak")
                    {
                        containers.Add(new ContainerInfo(
                            FilePath: file,
                            Type: ContainerType.Pak,
                            IsEncrypted: false,
                            EncryptionKeyGuid: null,
                            FileCount: 0,
                            MountPoint: Path.GetFileNameWithoutExtension(file)
                        ));
                    }
                    else if (ext == ".utoc")
                    {
                        // Check for matching .ucas sibling
                        var ucas = Path.ChangeExtension(file, ".ucas");
                        var hasUcas = File.Exists(ucas);
                        containers.Add(new ContainerInfo(
                            FilePath: file,
                            Type: ContainerType.IoStoreUtoc,
                            IsEncrypted: false,
                            EncryptionKeyGuid: null,
                            FileCount: 0,
                            MountPoint: hasUcas ? Path.GetFileNameWithoutExtension(file) : $"{Path.GetFileNameWithoutExtension(file)} (missing .ucas)"
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Error scanning directory {Dir} for containers", fullDir);
            }
        }

        return containers;
    }

    public IReadOnlyList<DiscoveredAsset> EnumerateAssets(AbstractFileProvider provider, string? filterGlob = null)
    {
        var result = new List<DiscoveredAsset>();
        var filter = filterGlob is not null ? PakExtractor.GlobToRegex(filterGlob) : null;

        foreach (var file in provider.Files.Values)
        {
            if (!file.IsUePackage) continue;
            if (file.Extension is "uexp" or "ubulk" or "uptnl") continue;
            if (filter is not null && !filter.IsMatch(file.Path)) continue;

            var mount = ContentWriter.MapToMount(file.Path).mount;
            result.Add(new DiscoveredAsset(
                VirtualPath: file.Path,
                Extension: file.Extension,
                MountPoint: mount,
                Size: file.Size,
                IsPackage: true,
                IsMap: file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase)
            ));
        }

        return result;
    }
}

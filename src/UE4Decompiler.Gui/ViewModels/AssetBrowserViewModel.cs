using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class AssetBrowserViewModel : ViewModelBase
{
    private readonly List<DiscoveredAsset> _allAssets = new();

    [ObservableProperty]
    private string _searchFilter = "";

    [ObservableProperty]
    private string _selectedTypeFilter = "All";

    [ObservableProperty]
    private DiscoveredAsset? _selectedAsset;

    [ObservableProperty]
    private string _assetPreviewText = "Select an asset to view details and metadata.";

    public ObservableCollection<string> AvailableTypes { get; } = new()
    {
        "All",
        "Blueprint",
        "StaticMesh",
        "SkeletalMesh",
        "Texture2D",
        "Material",
        "World",
        "SoundWave"
    };

    public ObservableCollection<DiscoveredAsset> FilteredAssets { get; } = new();

    public void LoadAssets(IEnumerable<DiscoveredAsset> assets)
    {
        _allAssets.Clear();
        _allAssets.AddRange(assets);
        ApplyFilters();
    }

    partial void OnSearchFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedTypeFilterChanged(string value) => ApplyFilters();

    partial void OnSelectedAssetChanged(DiscoveredAsset? value)
    {
        if (value == null)
        {
            AssetPreviewText = "Select an asset to view details and metadata.";
            return;
        }

        AssetPreviewText =
            $"Asset: {value.VirtualPath}\n" +
            $"Type: {value.Extension.ToUpperInvariant()}\n" +
            $"Mount: {value.MountPoint}\n" +
            $"Size: {value.Size:N0} bytes\n" +
            $"Is Level Map: {value.IsMap}\n" +
            $"Is Package: {value.IsPackage}\n\n" +
            $"Recovery Strategy: Standard {value.Extension.ToUpperInvariant()} pipeline with JSON IR model and metadata preservation.";
    }

    private void ApplyFilters()
    {
        FilteredAssets.Clear();
        var query = SearchFilter?.Trim() ?? "";
        var type = SelectedTypeFilter;

        foreach (var asset in _allAssets)
        {
            if (!string.IsNullOrEmpty(query) && !asset.VirtualPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            if (type != "All")
            {
                if (type == "World" && !asset.IsMap) continue;
                if (type != "World" && !asset.VirtualPath.Contains(type, StringComparison.OrdinalIgnoreCase) && !asset.Extension.Contains(type, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            FilteredAssets.Add(asset);
            if (FilteredAssets.Count >= 500) break; // Limit UI rendering for responsiveness
        }
    }
}

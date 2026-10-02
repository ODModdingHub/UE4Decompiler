using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Gui.Services;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class AssetBrowserViewModel : ViewModelBase
{
    private readonly List<DiscoveredAsset> _allAssets = new();
    private readonly MainWindowViewModel? _mainVm;
    private readonly IClipboardService _clipboardService;

    [ObservableProperty]
    private string _searchFilter = "";

    [ObservableProperty]
    private string _selectedTypeFilter = "All";

    [ObservableProperty]
    private DiscoveredAsset? _selectedAsset;

    [ObservableProperty]
    private string _selectedVirtualPath = "";

    [ObservableProperty]
    private string _selectedExtension = "";

    [ObservableProperty]
    private string _selectedMountPoint = "";

    [ObservableProperty]
    private string _selectedSizeFormatted = "";

    [ObservableProperty]
    private string _selectedTypeBadge = "";

    [ObservableProperty]
    private bool _hasSelectedAsset;

    [ObservableProperty]
    private bool _canInspectBlueprint;

    [ObservableProperty]
    private string _filterStatsText = "0 assets discovered";

    [ObservableProperty]
    private string _statusMessage = "Select an asset to view package properties.";

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

    public AssetBrowserViewModel(MainWindowViewModel? mainVm = null, IClipboardService? clipboard = null)
    {
        _mainVm = mainVm;
        _clipboardService = clipboard ?? new AvaloniaClipboardService();
    }

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
            HasSelectedAsset = false;
            CanInspectBlueprint = false;
            SelectedVirtualPath = "";
            SelectedExtension = "";
            SelectedMountPoint = "";
            SelectedSizeFormatted = "";
            SelectedTypeBadge = "";
            StatusMessage = "Select an asset to view package properties.";
            return;
        }

        HasSelectedAsset = true;
        SelectedVirtualPath = value.VirtualPath;
        SelectedExtension = value.Extension.ToUpperInvariant();
        SelectedMountPoint = value.MountPoint;
        SelectedSizeFormatted = FormatFileSize(value.Size);

        var isBp = value.VirtualPath.Contains("Blueprint", StringComparison.OrdinalIgnoreCase) ||
                   value.VirtualPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase);
        CanInspectBlueprint = isBp;

        SelectedTypeBadge = ClassifyAsset(value);
        StatusMessage = $"{SelectedTypeBadge}: {value.VirtualPath}";
    }

    [RelayCommand]
    public void SelectCategory(string category)
    {
        SelectedTypeFilter = category;
    }

    [RelayCommand]
    public async Task CopyPathAsync()
    {
        if (SelectedAsset != null)
        {
            await _clipboardService.SetTextAsync(SelectedAsset.VirtualPath);
            StatusMessage = $"Copied '{SelectedAsset.VirtualPath}' to clipboard.";
        }
    }

    [RelayCommand]
    public void InspectBlueprint()
    {
        if (SelectedAsset == null || _mainVm == null) return;

        var homeVm = _mainVm.HomeVm;
        _mainVm.GraphVm.LoadFromAssetBrowser(
            SelectedAsset,
            homeVm.InputPath,
            homeVm.SelectedEngine,
            homeVm.AesKey);

        _mainVm.CurrentPage = _mainVm.GraphVm;
    }

    private void ApplyFilters()
    {
        FilteredAssets.Clear();
        var query = SearchFilter?.Trim() ?? "";
        var type = SelectedTypeFilter;

        int totalMatches = 0;
        foreach (var asset in _allAssets)
        {
            if (!string.IsNullOrEmpty(query) && !asset.VirtualPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            if (type != "All")
            {
                if (type == "World" && !asset.IsMap) continue;
                if (type != "World" &&
                    !asset.VirtualPath.Contains(type, StringComparison.OrdinalIgnoreCase) &&
                    !asset.Extension.Contains(type, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            totalMatches++;
            if (FilteredAssets.Count < 500)
            {
                FilteredAssets.Add(asset);
            }
        }

        FilterStatsText = totalMatches > 500
            ? $"Showing first 500 of {totalMatches:N0} matching ({_allAssets.Count:N0} total assets)"
            : $"Showing {FilteredAssets.Count:N0} of {_allAssets.Count:N0} assets";
    }

    private static string ClassifyAsset(DiscoveredAsset a)
    {
        if (a.IsMap) return "World Map";
        var vp = a.VirtualPath;
        if (vp.Contains("Texture", StringComparison.OrdinalIgnoreCase) || a.Extension.Equals("png", StringComparison.OrdinalIgnoreCase)) return "Texture2D";
        if (vp.Contains("Material", StringComparison.OrdinalIgnoreCase)) return "Material";
        if (vp.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) return "SkeletalMesh";
        if (vp.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase)) return "StaticMesh";
        if (vp.Contains("Sound", StringComparison.OrdinalIgnoreCase) || a.Extension.Equals("ogg", StringComparison.OrdinalIgnoreCase)) return "SoundWave";
        if (vp.Contains("Blueprint", StringComparison.OrdinalIgnoreCase)) return "Blueprint";
        return a.IsPackage ? "Unreal Package" : a.Extension.ToUpperInvariant();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB ({bytes:N0} B)";
        return $"{bytes / (1024.0 * 1024.0):F2} MB ({bytes:N0} B)";
    }
}

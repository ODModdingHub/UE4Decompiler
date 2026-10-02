using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Gui.Services;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IDecompilerService _decompilerService;
    private readonly MainWindowViewModel _mainVm;
    private readonly IFileDialogService _fileDialogService;

    [ObservableProperty]
    private string _inputPath = "";

    [ObservableProperty]
    private string _outputPath = "";

    [ObservableProperty]
    private string _selectedEngine = "Auto-detect";

    [ObservableProperty]
    private string _aesKey = "";

    [ObservableProperty]
    private string _mappingPath = "";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "Select a container (.pak, .utoc) or cooked content folder to inspect.";

    [ObservableProperty]
    private int _discoveredAssetCount;

    [ObservableProperty]
    private string _detectedEngineInfo = "";

    [ObservableProperty]
    private string _containerSummary = "";

    public ObservableCollection<string> AvailableEngines { get; } = new()
    {
        "Auto-detect",
        "4.21",
        "4.26",
        "4.27",
        "5.0",
        "5.1",
        "5.2",
        "5.3",
        "5.4",
        "5.5"
    };

    public ObservableCollection<ContainerInfo> Containers { get; } = new();

    public HomeViewModel(IDecompilerService service, MainWindowViewModel mainVm, IFileDialogService? fileDialogService = null)
    {
        _decompilerService = service;
        _mainVm = mainVm;
        _fileDialogService = fileDialogService ?? new AvaloniaFileDialogService();
    }

    [RelayCommand]
    public async Task BrowseInputFileAsync()
    {
        var file = await _fileDialogService.OpenContainerFileAsync();
        if (!string.IsNullOrWhiteSpace(file))
        {
            InputPath = file;
            StatusMessage = $"Selected container: {Path.GetFileName(file)}";
        }
    }

    [RelayCommand]
    public async Task BrowseInputFolderAsync()
    {
        var folder = await _fileDialogService.OpenFolderAsync("Select Game Content or Paks Folder");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            InputPath = folder;
            StatusMessage = $"Selected directory: {folder}";
        }
    }

    [RelayCommand]
    public async Task BrowseOutputFolderAsync()
    {
        var folder = await _fileDialogService.OpenFolderAsync("Select Project Output Directory");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputPath = folder;
        }
    }

    [RelayCommand]
    public async Task BrowseMappingFileAsync()
    {
        var file = await _fileDialogService.OpenMappingFileAsync();
        if (!string.IsNullOrWhiteSpace(file))
        {
            MappingPath = file;
            StatusMessage = $"Selected mapping: {Path.GetFileName(file)}";
        }
    }

    [RelayCommand]
    public async Task ScanContainersAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            StatusMessage = "Please specify an input path or browse for a container.";
            return;
        }

        try
        {
            IsScanning = true;
            StatusMessage = "Mounting VFS and enumerating packages...";
            Containers.Clear();

            var hintedGame = SelectedEngine == "Auto-detect" ? null : VersionDetector.FromHint(SelectedEngine);

            var opts = new DecompileOptions
            {
                InputPath = InputPath,
                OutputRoot = Path.GetTempPath(),
                Game = hintedGame ?? EGame.GAME_UE4_27,
                EngineAssociation = SelectedEngine == "Auto-detect" ? "4.27" : SelectedEngine,
                AesKey = string.IsNullOrWhiteSpace(AesKey) ? null : AesKey,
                MappingPath = string.IsNullOrWhiteSpace(MappingPath) ? null : MappingPath
            };

            var scan = await _decompilerService.ScanAsync(InputPath, opts);

            foreach (var c in scan.Containers)
            {
                Containers.Add(c);
            }

            DiscoveredAssetCount = scan.Assets.Count;
            DetectedEngineInfo = $"{scan.EngineAssociation} ({scan.DetectedGame})";
            ContainerSummary = $"{scan.Containers.Count} container(s) mounted, {scan.Assets.Count:N0} packages indexed.";
            StatusMessage = $"Scan complete. {ContainerSummary}";

            _mainVm.AssetBrowserVm.LoadAssets(scan.Assets);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    public async Task StartRecoveryAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusMessage = "Both input path and output directory are required.";
            return;
        }

        _mainVm.CurrentPage = _mainVm.JobQueueVm;
        await _mainVm.JobQueueVm.RunRecoveryAsync(InputPath, OutputPath, SelectedEngine, AesKey, MappingPath);
    }
}

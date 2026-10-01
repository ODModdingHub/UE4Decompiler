using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IDecompilerService _decompilerService;
    private readonly MainWindowViewModel _mainVm;

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
    private string _statusMessage = "Ready. Select an input container (.pak, .utoc) or game directory to begin.";

    [ObservableProperty]
    private int _discoveredAssetCount;

    [ObservableProperty]
    private string _detectedEngineInfo = "";

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

    public HomeViewModel(IDecompilerService service, MainWindowViewModel mainVm)
    {
        _decompilerService = service;
        _mainVm = mainVm;
    }

    [RelayCommand]
    public async Task ScanContainersAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            StatusMessage = "Please provide an input path.";
            return;
        }

        try
        {
            IsScanning = true;
            StatusMessage = "Scanning containers and assets...";
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
            StatusMessage = $"Scan completed: {scan.Assets.Count:N0} assets found across {scan.Containers.Count} container(s).";

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

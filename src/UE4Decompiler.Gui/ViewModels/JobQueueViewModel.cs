using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class JobQueueViewModel : ViewModelBase
{
    private readonly IDecompilerService _service;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _currentStage = "Idle";

    [ObservableProperty]
    private string _currentAsset = "";

    [ObservableProperty]
    private string _elapsedTimeText = "00:00:00";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _summaryText = "No active recovery jobs.";

    public ObservableCollection<string> LogEntries { get; } = new();

    public JobQueueViewModel(IDecompilerService service)
    {
        _service = service;
    }

    public async Task RunRecoveryAsync(string inputPath, string outputPath, string engineHint, string? aesKey, string? mappingPath)
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ProgressPercentage = 0;
        CurrentStage = "Initializing Pipeline...";
        LogEntries.Clear();
        LogEntries.Add($"[INFO] Starting asset recovery for {inputPath}");
        LogEntries.Add($"[INFO] Destination directory: {outputPath}");

        var sw = Stopwatch.StartNew();
        var timer = new System.Timers.Timer(500);
        timer.Elapsed += (_, _) =>
        {
            ElapsedTimeText = sw.Elapsed.ToString(@"hh\:mm\:ss");
        };
        timer.Start();

        try
        {
            var hintedGame = engineHint == "Auto-detect" ? null : VersionDetector.FromHint(engineHint);
            var mountGame = hintedGame ?? EGame.GAME_UE4_27;

            var opts = new DecompileOptions
            {
                InputPath = inputPath,
                OutputRoot = Path.GetFullPath(outputPath),
                Game = mountGame,
                EngineAssociation = engineHint == "Auto-detect" ? "4.27" : engineHint,
                AesKey = string.IsNullOrWhiteSpace(aesKey) ? null : aesKey,
                MappingPath = string.IsNullOrWhiteSpace(mappingPath) ? null : mappingPath
            };

            var progress = new Progress<RecoveryProgress>(p =>
            {
                ProgressPercentage = p.Percentage;
                CurrentStage = p.Stage;
                CurrentAsset = $"{p.CurrentAsset} ({p.ProcessedCount:N0}/{p.TotalCount:N0})";
            });

            var report = await _service.RecoverAsync(inputPath, outputPath, opts, progress, _cts.Token);

            timer.Stop();
            sw.Stop();
            ElapsedTimeText = sw.Elapsed.ToString(@"hh\:mm\:ss");

            ProgressPercentage = 100;
            CurrentStage = "Completed";
            SummaryText = $"Recovery finished: {report.RecoveredCount:N0} recovered, {report.PartialCount:N0} partial, {report.FailedCount:N0} failed.";
            LogEntries.Add($"[SUCCESS] {SummaryText}");
        }
        catch (OperationCanceledException)
        {
            CurrentStage = "Cancelled by user";
            LogEntries.Add("[WARN] Recovery operation was cancelled.");
        }
        catch (Exception ex)
        {
            CurrentStage = "Failed";
            LogEntries.Add($"[ERROR] Recovery crashed: {ex.Message}");
        }
        finally
        {
            timer.Dispose();
            IsRunning = false;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _cts?.Cancel();
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class DoctorViewModel : ViewModelBase
{
    private readonly IDecompilerService _service;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _overallStatus = "Ready to run health diagnostics.";

    public ObservableCollection<DiagnosticItem> DiagnosticItems { get; } = new();

    public DoctorViewModel(IDecompilerService service)
    {
        _service = service;
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        try
        {
            IsRunning = true;
            OverallStatus = "Executing system and environment checks...";
            DiagnosticItems.Clear();

            var report = await _service.RunDoctorAsync();

            foreach (var item in report.Items)
            {
                DiagnosticItems.Add(item);
            }

            if (report.HasErrors)
            {
                OverallStatus = "Diagnostics identified critical environment issues.";
            }
            else if (report.HasWarnings)
            {
                OverallStatus = "Diagnostics passed with warnings.";
            }
            else
            {
                OverallStatus = "All environment and capability checks passed successfully!";
            }
        }
        catch (Exception ex)
        {
            OverallStatus = $"Diagnostics failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _threads = Math.Max(2, Environment.ProcessorCount);

    [ObservableProperty]
    private bool _enableDiskCache = true;

    [ObservableProperty]
    private string _defaultEngine = "Auto-detect";

    [ObservableProperty]
    private bool _isDarkTheme = true;
}

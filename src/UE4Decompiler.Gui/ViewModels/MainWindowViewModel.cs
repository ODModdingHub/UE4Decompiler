using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE4Decompiler.Core.Services;

namespace UE4Decompiler.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private string _statusText = "UE4Decompiler ready.";

    public HomeViewModel HomeVm { get; }
    public AssetBrowserViewModel AssetBrowserVm { get; }
    public JobQueueViewModel JobQueueVm { get; }
    public DoctorViewModel DoctorVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public MainWindowViewModel()
    {
        var service = new DecompilerService();

        HomeVm = new HomeViewModel(service, this);
        AssetBrowserVm = new AssetBrowserViewModel();
        JobQueueVm = new JobQueueViewModel(service);
        DoctorVm = new DoctorViewModel(service);
        SettingsVm = new SettingsViewModel();

        _currentPage = HomeVm;
    }

    [RelayCommand]
    public void NavigateHome() => CurrentPage = HomeVm;

    [RelayCommand]
    public void NavigateBrowser() => CurrentPage = AssetBrowserVm;

    [RelayCommand]
    public void NavigateQueue() => CurrentPage = JobQueueVm;

    [RelayCommand]
    public void NavigateDoctor() => CurrentPage = DoctorVm;

    [RelayCommand]
    public void NavigateSettings() => CurrentPage = SettingsVm;
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace UE4Decompiler.Gui.Services;

public sealed class AvaloniaFileDialogService : IFileDialogService
{
    private static IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }
        return null;
    }

    public async Task<string?> OpenContainerFileAsync()
    {
        var sp = GetStorageProvider();
        if (sp == null) return null;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Unreal Engine Container",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Unreal Containers (*.pak, *.utoc, *.ucas)")
                {
                    Patterns = new[] { "*.pak", "*.utoc", "*.ucas" }
                },
                new("Unreal Pak Archives (*.pak)")
                {
                    Patterns = new[] { "*.pak" }
                },
                new("Unreal IoStore Containers (*.utoc)")
                {
                    Patterns = new[] { "*.utoc" }
                },
                new("All Files (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var sp = GetStorageProvider();
        if (sp == null) return null;

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    public async Task<string?> OpenMappingFileAsync()
    {
        var sp = GetStorageProvider();
        if (sp == null) return null;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Unversioned Property Mapping (.usmap)",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Unreal Mapping Files (*.usmap)")
                {
                    Patterns = new[] { "*.usmap" }
                },
                new("All Files (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    public async Task<string?> SaveFileAsync(string title, string defaultFileName, string extension)
    {
        var sp = GetStorageProvider();
        if (sp == null) return null;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = extension,
            SuggestedFileName = defaultFileName
        });

        return file?.Path.LocalPath;
    }
}

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

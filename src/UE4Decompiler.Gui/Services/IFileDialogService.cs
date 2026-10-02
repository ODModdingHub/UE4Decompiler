namespace UE4Decompiler.Gui.Services;

public interface IFileDialogService
{
    Task<string?> OpenContainerFileAsync();
    Task<string?> OpenFolderAsync(string title);
    Task<string?> OpenMappingFileAsync();
    Task<string?> SaveFileAsync(string title, string defaultFileName, string extension);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

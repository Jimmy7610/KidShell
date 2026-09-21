using KidShell.Core.Diagnostics;
using Windows.Storage.Pickers;

namespace KidShell.App.Services;

/// <summary>
/// Opens the ordinary Windows file picker so a parent can point KidShell at a
/// program. Only reachable from Parent Mode, and only ever used to read a
/// path - KidShell never copies, moves or modifies the chosen file.
/// </summary>
public interface IFilePickerService
{
    nint WindowHandle { get; set; }

    Task<string?> PickProgramAsync();
}

public sealed class FilePickerService : IFilePickerService
{
    private readonly IKidShellLogger _logger;

    public FilePickerService(IKidShellLogger logger) => _logger = logger;

    public nint WindowHandle { get; set; }

    public async Task<string?> PickProgramAsync()
    {
        if (WindowHandle == 0)
        {
            _logger.Warning("Picker", "No window handle available; cannot show the file picker.");
            return null;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };

            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".lnk");
            picker.FileTypeFilter.Add(".bat");
            picker.FileTypeFilter.Add(".cmd");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            _logger.Error("Picker", "The file picker could not be shown.", ex);
            return null;
        }
    }
}

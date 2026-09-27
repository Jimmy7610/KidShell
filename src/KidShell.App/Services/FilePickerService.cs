using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;
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

            // Programs only.
            //
            // This used to offer .lnk, .bat and .cmd as well. A batch file is
            // not a program: running one runs cmd.exe, which is an interpreter
            // that runs anything - and which KidShell's own AppLocker policy
            // refuses by name, so a parent adding one would have got a tile
            // that Secure Mode then blocked. A shortcut is not a program
            // either: what was approved and what would run are two different
            // files, and the target can be repointed afterwards.
            //
            // The filter is a convenience, not the rule. A parent can type a
            // path, and the type list can be defeated by typing *.* into the
            // name box, so ManualProgramPolicy checks the answer as well.
            foreach (var extension in ManualProgramPolicy.PickerFilter)
            {
                picker.FileTypeFilter.Add(extension);
            }

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

using System.Runtime.InteropServices;
using KidShell.Core.Apps;
using KidShell.Core.Diagnostics;

namespace KidShell.App.Services.Apps;

/// <summary>
/// Reads the Start Menu shortcut folders.
///
/// STRICTLY READ-ONLY: enumerates .lnk files and resolves them through
/// IShellLink. Nothing is created, moved or deleted.
///
/// The Start Menu is the best single source for "programs a person actually
/// has", because it is what the installer chose to advertise. It is not
/// complete — some programs never add a shortcut — which is why the registry
/// scanners exist alongside it.
/// </summary>
public sealed class StartMenuScanner : IApplicationScanner
{
    private readonly IKidShellLogger _logger;

    public StartMenuScanner(IKidShellLogger logger) => _logger = logger;

    public DiscoverySource Source => DiscoverySource.StartMenu;

    public Task<IReadOnlyList<DiscoveredApplication>> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<DiscoveredApplication>>([]);
        }

        var results = new List<DiscoveredApplication>();

        foreach (var root in StartMenuRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectShortcuts(root, results, cancellationToken);
        }

        _logger.Info("Apps", $"Start Menu scan found {results.Count} shortcuts.");
        return Task.FromResult<IReadOnlyList<DiscoveredApplication>>(results);
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.StartMenu
                 })
        {
            var path = Environment.GetFolderPath(folder);

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private void CollectShortcuts(string root, List<DiscoveredApplication> results, CancellationToken cancellationToken)
    {
        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.Warning("Apps", $"Could not enumerate {root}.", ex);
            return;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Describe(file) is { } app)
                {
                    results.Add(app);
                }
            }
            catch (Exception ex)
            {
                // One bad shortcut must not end the scan.
                _logger.Debug("Apps", $"Shortcut skipped ({ex.GetType().Name}): {Path.GetFileName(file)}");
            }
        }
    }

    private static DiscoveredApplication? Describe(string shortcutPath)
    {
        var name = Path.GetFileNameWithoutExtension(shortcutPath);

        if (string.IsNullOrWhiteSpace(name) || IsNoise(name))
        {
            return null;
        }

        var (target, arguments, icon) = ShellLink.Resolve(shortcutPath);

        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        // Uninstallers, help files and web links are not things a child runs.
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var exists = File.Exists(target);

        return new DiscoveredApplication
        {
            Key = string.Empty,     // assigned by the catalogue
            DisplayName = name,
            Kind = ApplicationKind.Win32,
            Source = DiscoverySource.StartMenu,
            ExecutablePath = target,
            Arguments = arguments,
            IconPath = string.IsNullOrWhiteSpace(icon) ? target : icon,
            TargetExists = exists
        };
    }

    /// <summary>
    /// Entries no parent is looking for. Filtering these is the difference
    /// between a usable list and a wall of uninstallers.
    /// </summary>
    private static bool IsNoise(string name)
    {
        string[] noise =
        [
            "uninstall", "avinstallera", "readme", "läs mig", "help", "hjälp",
            "documentation", "dokumentation", "release notes", "website",
            "webbplats", "support", "licence", "license", "licens", "changelog"
        ];

        return noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------ interop

    /// <summary>
    /// Minimal IShellLink wrapper. Read-only: only Load and the Get* methods
    /// are declared, so this file has no way to write a shortcut.
    /// </summary>
    private static class ShellLink
    {
        public static (string Target, string Arguments, string Icon) Resolve(string path)
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);

            var target = new char[260];
            link.GetPath(target, target.Length, IntPtr.Zero, 0);

            var arguments = new char[1024];
            link.GetArguments(arguments, arguments.Length);

            var icon = new char[260];
            link.GetIconLocation(icon, icon.Length, out _);

            return (Clean(target), Clean(arguments), Clean(icon));
        }

        private static string Clean(char[] buffer) => new string(buffer).TrimEnd('\0').Trim();

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPArray)] char[] file, int maxPath, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPArray)] char[] name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray)] char[] dir, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPArray)] char[] args, int maxArgs);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray)] char[] iconPath, int iconPathLength, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }
    }
}

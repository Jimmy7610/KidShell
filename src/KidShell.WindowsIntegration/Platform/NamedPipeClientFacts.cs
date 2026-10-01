using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KidShell.WindowsIntegration.Platform;

/// <summary>
/// What Windows will say about the far end of a named pipe.
///
/// WHY IT IS IN Platform/ AND NOT NEXT TO THE LISTENER
/// ---------------------------------------------------
/// Because every P/Invoke in this assembly is, and the repository has a gate
/// that fails the build if one appears anywhere else. The gate's pattern is
/// blunt - it matches DllImport without asking whether the call mutates
/// anything - and this one only reads. Arguing the exception would have been
/// cheaper than moving the code and worse: a rule with one documented
/// exception has two, shortly.
///
/// So the call lives where calls live, and the broker listener asks for a
/// fact instead of making one.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NamedPipeClientFacts
{
    /// <summary>
    /// The Windows session the connected client is in.
    ///
    /// Zero when Windows will not say. A parent capability is bound to this
    /// value, so it has to come from the operating system rather than from
    /// anything the caller sent - and an unanswerable question has to resolve
    /// to something that does not accidentally match a real session a
    /// capability was issued in.
    /// </summary>
    public static int SessionIdOf(SafeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);

        return GetNamedPipeClientSessionId(pipeHandle, out var session) ? (int)session : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafeHandle pipe, out uint clientSessionId);
}

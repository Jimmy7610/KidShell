using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Storage;

/// <summary>Why a protected store could not be used.</summary>
public enum ProtectedStoreStatus
{
    /// <summary>Present, correctly permissioned, usable.</summary>
    Ready = 0,

    /// <summary>Not set up yet. Expected before a dedicated device is prepared.</summary>
    NotProvisioned = 1,

    /// <summary>
    /// There, but the child could write to it. Worse than absent: it looks
    /// like protection and is not.
    /// </summary>
    PermissionsWrong = 2,

    /// <summary>Unreadable - missing, locked, or on a drive that went away.</summary>
    Unavailable = 3,

    /// <summary>
    /// A development stand-in, in the signed-in user's own profile.
    ///
    /// Not a protected store at all, and says so.
    /// </summary>
    DevelopmentOnly = 4
}

public sealed record ProtectedStoreState(ProtectedStoreStatus Status, string Detail)
{
    /// <summary>Whether authoritative policy may be read from or written to this store.</summary>
    public bool IsTrustworthy => Status == ProtectedStoreStatus.Ready;
}

/// <summary>
/// Decides whether KidShell may run on the policy it just loaded.
///
/// THE RULE THIS EXISTS TO ENFORCE
/// -------------------------------
/// A production build must never quietly fall back to child-writable storage.
/// That is the failure the whole finding is about: falling back is the
/// comfortable thing to do, it always works, and it silently removes the
/// protection a parent was told they had.
///
/// So the fallback is a decision with a name, taken in one place, and it is
/// only ever allowed in a development build - where it is accompanied by the
/// watermark that already tells a developer nothing here is real.
/// </summary>
public static class ProtectedStoreGate
{
    /// <summary>
    /// Whether policy may be loaded from an ordinary, child-writable file.
    /// </summary>
    public static bool MayUseUnprotectedStorage(
        ProtectedStoreState state,
        bool isDevelopmentBuild,
        IKidShellLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.IsTrustworthy)
        {
            return false;   // nothing to fall back to; the real store works
        }

        if (isDevelopmentBuild)
        {
            logger?.Warning("Storage",
                $"Protected policy storage is {state.Status} ({state.Detail}). " +
                "A development build continues on the unprotected file; a shipped build would refuse.");
            return true;
        }

        logger?.Error("Storage",
            $"Protected policy storage is {state.Status} ({state.Detail}). " +
            "A production build will not fall back to storage the child can write to.");

        return false;
    }
}

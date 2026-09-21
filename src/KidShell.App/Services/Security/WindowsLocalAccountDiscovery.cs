using System.Runtime.InteropServices;
using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;

namespace KidShell.App.Services.Security;

/// <summary>
/// Lists local Windows accounts through the netapi32 enumeration functions.
///
/// STRICTLY READ-ONLY. Only NetUserEnum, NetLocalGroupGetMembers and
/// NetApiBufferFree are imported. The mutating siblings — NetUserAdd,
/// NetUserDel, NetUserSetInfo, NetLocalGroupAddMembers — are deliberately not
/// declared here, so this file has no vocabulary for changing an account.
///
/// Group membership is resolved by well-known SID rather than by the name
/// "Administrators", which is localized (on this machine it is
/// "Administratörer") and would silently return nothing.
/// </summary>
public sealed class WindowsLocalAccountDiscovery : IWindowsAccountDiscovery
{
    private const int UserInfoLevel3 = 3;
    private const int MaxPreferredLength = -1;
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;

    /// <summary>UF_ACCOUNTDISABLE.</summary>
    private const uint AccountDisabled = 0x0002;

    /// <summary>RIDs below this are Windows' own built-in accounts.</summary>
    private const uint FirstNonBuiltInRid = 1000;

    private readonly IKidShellLogger _logger;

    public WindowsLocalAccountDiscovery(IKidShellLogger logger) => _logger = logger;

    public Task<IReadOnlyList<WindowsAccount>> ListLocalAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<WindowsAccount>>([]);
        }

        try
        {
            return Task.FromResult(Enumerate());
        }
        catch (Exception ex)
        {
            // An unreadable account list degrades the report; it never breaks
            // the app or the Säkerhet page.
            _logger.Error(SecurityAuditEvents.Category, "Could not enumerate local accounts.", ex);
            return Task.FromResult<IReadOnlyList<WindowsAccount>>([]);
        }
    }

    private IReadOnlyList<WindowsAccount> Enumerate()
    {
        var administratorSids = ReadAdministratorSids();
        var accounts = new List<WindowsAccount>();

        var handle = 0;
        var buffer = IntPtr.Zero;

        try
        {
            var status = NetUserEnum(
                servername: null,
                level: UserInfoLevel3,
                filter: 0,
                bufptr: out buffer,
                prefmaxlen: MaxPreferredLength,
                entriesread: out var read,
                totalentries: out _,
                resume_handle: ref handle);

            if (status is not (NerrSuccess or ErrorMoreData))
            {
                _logger.Warning(SecurityAuditEvents.Category, $"NetUserEnum returned status {status}.");
                return [];
            }

            var size = Marshal.SizeOf<UserInfo3>();

            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<UserInfo3>(buffer + (i * size));
                var account = Describe(entry, administratorSids);

                if (account is not null)
                {
                    accounts.Add(account);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }

        return [.. accounts.OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)];
    }

    private WindowsAccount? Describe(UserInfo3 entry, HashSet<string> administratorSids)
    {
        var username = entry.usri3_name ?? string.Empty;

        if (username.Length == 0)
        {
            return null;
        }

        var sid = ResolveSid(username);

        return new WindowsAccount
        {
            Username = username,
            DisplayName = entry.usri3_full_name ?? string.Empty,
            Sid = sid ?? string.Empty,
            IsEnabled = (entry.usri3_flags & AccountDisabled) == 0,
            IsAdministrator = sid is not null && administratorSids.Contains(sid),
            IsBuiltIn = entry.usri3_user_id < FirstNonBuiltInRid
        };
    }

    /// <summary>Resolves an account name to its SID string, or null.</summary>
    private string? ResolveSid(string username)
    {
        try
        {
            var account = new NTAccount(Environment.MachineName, username);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex)
        {
            _logger.Debug(SecurityAuditEvents.Category, $"Could not resolve a SID for an account: {ex.GetType().Name}.");
            return null;
        }
    }

    /// <summary>
    /// SIDs in the built-in Administrators group, found by well-known SID so
    /// the localized group name does not matter.
    /// </summary>
    private HashSet<string> ReadAdministratorSids()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = IntPtr.Zero;

        try
        {
            var wellKnown = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);
            var groupName = ((NTAccount)wellKnown.Translate(typeof(NTAccount))).Value;

            // "BUILTIN\Administratörer" -> "Administratörer"
            var slash = groupName.LastIndexOf('\\');
            if (slash >= 0)
            {
                groupName = groupName[(slash + 1)..];
            }

            var handle = 0;
            var status = NetLocalGroupGetMembers(
                servername: null,
                localgroupname: groupName,
                level: 0,
                bufptr: out buffer,
                prefmaxlen: MaxPreferredLength,
                entriesread: out var read,
                totalentries: out _,
                resume_handle: ref handle);

            if (status is not (NerrSuccess or ErrorMoreData))
            {
                _logger.Warning(SecurityAuditEvents.Category, $"NetLocalGroupGetMembers returned status {status}.");
                return result;
            }

            var size = Marshal.SizeOf<LocalGroupMembersInfo0>();

            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupMembersInfo0>(buffer + (i * size));

                if (entry.lgrmi0_sid != IntPtr.Zero)
                {
                    result.Add(new SecurityIdentifier(entry.lgrmi0_sid).Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not read Administrators group membership.", ex);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- interop
    // Read-only netapi32 surface. Nothing that writes is declared.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_password;
        public uint usri3_password_age;
        public uint usri3_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_comment;
        public uint usri3_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_script_path;
        public uint usri3_auth_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_full_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_usr_comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_parms;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_workstations;
        public uint usri3_last_logon;
        public uint usri3_last_logoff;
        public uint usri3_acct_expires;
        public uint usri3_max_storage;
        public uint usri3_units_per_week;
        public IntPtr usri3_logon_hours;
        public uint usri3_bad_pw_count;
        public uint usri3_num_logons;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_logon_server;
        public uint usri3_country_code;
        public uint usri3_code_page;
        public uint usri3_user_id;
        public uint usri3_primary_group_id;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_profile;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri3_home_dir_drive;
        public uint usri3_password_expired;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr lgrmi0_sid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserEnum(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        int level,
        int filter,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        ref int resume_handle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupGetMembers(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        [MarshalAs(UnmanagedType.LPWStr)] string localgroupname,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        ref int resume_handle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

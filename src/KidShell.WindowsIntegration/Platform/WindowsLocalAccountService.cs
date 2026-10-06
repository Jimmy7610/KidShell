using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Validation;

namespace KidShell.WindowsIntegration.Platform;

/// <summary>
/// Local account management through netapi32.
///
/// THIS FILE CAN CHANGE WINDOWS. It is the first one in KidShell's history
/// that can, and every method is reachable only from an
/// <c>ISecurityOperation</c> running inside the elevated helper with an Apply
/// context - which no development build can construct.
///
/// netapi32 rather than PowerShell's <c>New-LocalUser</c>: it is the documented
/// API the cmdlets themselves wrap, it returns a structured status code instead
/// of text to parse, and it needs no PowerShell execution policy, no module and
/// no command string. "Run this PowerShell" is exactly the IPC shape the
/// privilege design rules out.
///
/// See: https://learn.microsoft.com/windows/win32/api/lmaccess/
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLocalAccountService : ILocalAccountService
{
    private const int UserInfoLevel1 = 1;
    private const int UserInfoLevel3 = 3;
    private const int UserInfoLevel1008 = 1008;
    private const int LocalGroupMembersLevel0 = 0;
    private const int MaxPreferredLength = -1;
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int NerrUserNotFound = 2221;

    /// <summary>USER_PRIV_USER: a standard user, not an administrator.</summary>
    private const uint UserPrivUser = 1;

    /// <summary>UF_SCRIPT is required by NetUserAdd; UF_NORMAL_ACCOUNT is the account type.</summary>
    private const uint UfScript = 0x0001;

    private const uint UfAccountDisable = 0x0002;
    private const uint UfNormalAccount = 0x0200;
    private const uint UfDontExpirePasswd = 0x10000;

    private const uint FirstNonBuiltInRid = 1000;

    private readonly IKidShellLogger _logger;

    public WindowsLocalAccountService(IKidShellLogger logger) => _logger = logger;

    // ------------------------------------------------------------- reading

    public Task<IReadOnlyList<LocalAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Enumerate());
    }

    public async Task<LocalAccount?> FindBySidAsync(string sid, CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<LocalAccount?> FindByNameAsync(string username, CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<LocalAccount> Enumerate()
    {
        var administrators = ReadAdministratorSids();
        var accounts = new List<LocalAccount>();

        var handle = 0;
        var buffer = IntPtr.Zero;

        try
        {
            var status = NetUserEnum(null, UserInfoLevel3, 0, out buffer, MaxPreferredLength,
                out var read, out _, ref handle);

            if (status is not (NerrSuccess or ErrorMoreData))
            {
                throw new InvalidOperationException($"NetUserEnum returned {status}.");
            }

            var size = Marshal.SizeOf<UserInfo3>();

            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<UserInfo3>(buffer + (i * size));

                if (string.IsNullOrEmpty(entry.usri3_name))
                {
                    continue;
                }

                var sid = ResolveSid(entry.usri3_name);

                accounts.Add(new LocalAccount
                {
                    Username = entry.usri3_name,
                    Sid = sid ?? string.Empty,
                    FullName = entry.usri3_full_name ?? string.Empty,
                    IsEnabled = (entry.usri3_flags & UfAccountDisable) == 0,
                    IsBuiltIn = entry.usri3_user_id < FirstNonBuiltInRid,

                    // By SID, never by the group name: "Administrators" is
                    // localized (Administratörer here) and a name comparison
                    // would silently report every account as standard.
                    IsAdministrator = sid is not null && administrators.Contains(sid)
                });
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

    private HashSet<string> ReadAdministratorSids() =>
        ReadGroupMemberSids(WellKnownSecurityGroups.AdministratorsSid) ?? [];

    /// <summary>
    /// The SIDs in a built-in group, by the group's SID, or null when the group
    /// could not be read.
    ///
    /// NULL RATHER THAN EMPTY when the read fails. An empty set means "the group
    /// is here and nobody is in it"; the difference matters because an unreadable
    /// Administrators group returning empty would report every administrator as a
    /// standard user.
    /// </summary>
    private HashSet<string>? ReadGroupMemberSids(string groupSid)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupName = GroupNameFromSid(groupSid);
        var buffer = IntPtr.Zero;
        var handle = IntPtr.Zero;

        if (groupName is null)
        {
            // There is no name to ask for. Previously this fell back to the
            // literal "Administrators", which on this Swedish machine names no
            // group at all - so the call would fail and every administrator
            // would be reported as a standard user.
            _logger.Warning(SecurityAuditEvents.Category,
                $"The group {groupSid} could not be named on this machine, so its membership was not read.");

            return null;
        }

        try
        {
            var status = NetLocalGroupGetMembers(null, groupName, LocalGroupMembersLevel0,
                out buffer, MaxPreferredLength, out var read, out _, ref handle);

            if (status is not (NerrSuccess or ErrorMoreData))
            {
                _logger.Warning(SecurityAuditEvents.Category,
                    $"NetLocalGroupGetMembers returned {status} for {groupSid}, so its membership is unknown.");

                return null;
            }

            var size = Marshal.SizeOf<LocalGroupMembersInfo0>();

            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupMembersInfo0>(buffer + (i * size));

                if (entry.lgrmi0_sid != IntPtr.Zero && ConvertSidToStringSid(entry.lgrmi0_sid, out var sid))
                {
                    result.Add(sid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, $"Could not read the group {groupSid}.", ex);

            return null;
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

    /// <inheritdoc />
    public Task<bool?> IsInStandardUsersGroupAsync(
        string sid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        cancellationToken.ThrowIfCancellationRequested();

        var members = ReadGroupMemberSids(WellKnownSecurityGroups.UsersSid);

        // Null all the way out. The caller has to decide what an unanswered
        // membership question means, rather than being handed a false.
        return Task.FromResult(members is null ? null : (bool?)members.Contains(sid));
    }

    /// <summary>
    /// A built-in group's name on this Windows, from its SID, or null.
    ///
    /// NEVER A GUESSED ENGLISH NAME. The net API takes a name, so a name has to
    /// be produced - but on this machine the Administrators group is called
    /// Administratörer, and a lookup of "Administrators" does not fail loudly: it
    /// returns no members, and no members reads as "nobody is an administrator".
    /// </summary>
    private static string? GroupNameFromSid(string groupSid)
    {
        try
        {
            var sid = new SecurityIdentifier(groupSid);
            return ((NTAccount)sid.Translate(typeof(NTAccount))).Value.Split('\\').Last();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The local Administrators group under whatever name this Windows uses,
    /// resolved from the well-known SID, or null when it cannot be resolved.
    ///
    /// NULL, NOT "Administrators". The net API takes a name, so a name has to be
    /// produced - but guessing the English one is how this family of defects
    /// works: on this machine the group is called Administratörer, and a lookup
    /// of "Administrators" does not fail loudly, it returns no members, and no
    /// members reads as "no administrators".
    /// </summary>
    private static string? AdministratorsGroupName() =>
        GroupNameFromSid(WellKnownSecurityGroups.AdministratorsSid);

    private static string? ResolveSid(string username)
    {
        try
        {
            var account = new NTAccount(Environment.MachineName, username);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------- writing

    public Task<LocalAccount> CreateStandardAccountAsync(
        string username,
        string? fullName,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        cancellationToken.ThrowIfCancellationRequested();

        var info = new UserInfo1
        {
            usri1_name = username,
            usri1_password = password,

            // USER_PRIV_USER. A child account is created standard and is never
            // created as an administrator and demoted afterwards: that would
            // leave a window in which it had rights nobody intended.
            usri1_priv = UserPrivUser,
            usri1_home_dir = null,
            usri1_comment = fullName,
            usri1_flags = UfScript | UfNormalAccount | UfDontExpirePasswd,
            usri1_script_path = null
        };

        var status = NetUserAdd(null, UserInfoLevel1, ref info, out var parameterError);

        if (status != NerrSuccess)
        {
            throw new InvalidOperationException(
                $"NetUserAdd returned {status} (parameter {parameterError}).");
        }

        _logger.Info(SecurityAuditEvents.Category, $"Created local standard account '{username}'.");

        if (!string.IsNullOrEmpty(fullName))
        {
            SetFullName(username, fullName);
        }

        var sid = ResolveSid(username)
                  ?? throw new InvalidOperationException($"Account '{username}' was created but its SID could not be read.");

        return Task.FromResult(new LocalAccount
        {
            Username = username,
            Sid = sid,
            FullName = fullName ?? string.Empty,
            IsEnabled = true,
            IsAdministrator = false,
            IsBuiltIn = false
        });
    }

    public async Task DeleteAccountAsync(string sid, CancellationToken cancellationToken = default)
    {
        var account = await FindBySidAsync(sid, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            // Already gone. Rolling back a creation that did not happen is a
            // success, not a failure.
            return;
        }

        if (account.IsBuiltIn)
        {
            throw new InvalidOperationException("Refusing to delete a built-in Windows account.");
        }

        var status = NetUserDel(null, account.Username);

        if (status is not (NerrSuccess or NerrUserNotFound))
        {
            throw new InvalidOperationException($"NetUserDel returned {status}.");
        }

        _logger.Warning(SecurityAuditEvents.Category, $"Deleted local account '{account.Username}' (rollback).");
    }

    public async Task SetAdministratorAsync(string sid, bool isAdministrator, CancellationToken cancellationToken = default)
    {
        var account = await FindBySidAsync(sid, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"No local account with SID {sid}.");

        // Refusing beats guessing. Changing administrator membership against a
        // guessed group name either does nothing or does it somewhere else.
        var group = AdministratorsGroupName()
                    ?? throw new InvalidOperationException(
                        $"The Administrators group ({WellKnownSecurityGroups.AdministratorsSid}) could not be named " +
                        "on this machine, so its membership was not changed.");

        var member = new LocalGroupMembersInfo3 { lgrmi3_domainandname = $"{Environment.MachineName}\\{account.Username}" };

        var status = isAdministrator
            ? NetLocalGroupAddMembers(null, group, 3, ref member, 1)
            : NetLocalGroupDelMembers(null, group, 3, ref member, 1);

        // 1378 = member already in group; 1377 = member not in group. Both
        // mean the requested end state already holds.
        if (status is not (NerrSuccess or 1377 or 1378))
        {
            throw new InvalidOperationException(
                $"{(isAdministrator ? "NetLocalGroupAddMembers" : "NetLocalGroupDelMembers")} returned {status}.");
        }

        _logger.Info(SecurityAuditEvents.Category,
            $"Account '{account.Username}' administrator membership set to {isAdministrator}.");
    }

    public async Task SetEnabledAsync(string sid, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var account = await FindBySidAsync(sid, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"No local account with SID {sid}.");

        var flags = UfScript | UfNormalAccount | UfDontExpirePasswd;

        if (!isEnabled)
        {
            flags |= UfAccountDisable;
        }

        var info = new UserInfo1008 { usri1008_flags = flags };
        var status = NetUserSetInfo(null, account.Username, 1008, ref info, out var parameterError);

        if (status != NerrSuccess)
        {
            throw new InvalidOperationException($"NetUserSetInfo returned {status} (parameter {parameterError}).");
        }
    }

    private static void SetFullName(string username, string fullName)
    {
        var info = new UserInfo1011 { usri1011_full_name = fullName };
        NetUserSetInfo(null, username, 1011, ref info, out _);
    }

    // ------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public uint usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1008
    {
        public uint usri1008_flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1011
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1011_full_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr lgrmi0_sid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lgrmi3_domainandname;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserEnum(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, int level, int filter,
        out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resume_handle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserAdd(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, int level, ref UserInfo1 buf, out int parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserDel(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, [MarshalAs(UnmanagedType.LPWStr)] string username);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserSetInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, [MarshalAs(UnmanagedType.LPWStr)] string username,
        int level, ref UserInfo1008 buf, out int parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserSetInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, [MarshalAs(UnmanagedType.LPWStr)] string username,
        int level, ref UserInfo1011 buf, out int parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupGetMembers(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, [MarshalAs(UnmanagedType.LPWStr)] string localgroupname,
        int level, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref IntPtr resumehandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupAddMembers(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, [MarshalAs(UnmanagedType.LPWStr)] string groupname,
        int level, ref LocalGroupMembersInfo3 buf, int totalentries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupDelMembers(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername, [MarshalAs(UnmanagedType.LPWStr)] string groupname,
        int level, ref LocalGroupMembersInfo3 buf, int totalentries);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out string stringSid);
}

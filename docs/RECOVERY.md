# KidShell recovery

**If you are reading this because something went wrong: nothing KidShell has
shipped so far changes Windows. If you cannot sign in, the cause is not
KidShell.**

That will stop being true when the security milestones run. This document
describes how to get back, and is written to be usable from a different account
on a bad day.

---

## Nothing has been applied yet

At 1.0.0-rc.1 the operations that change Windows exist and are tested. None of
them has run.

Every build runs in `AuditOnly`, and not as a setting: `SecurityExecutionContext`
has a private constructor and one public factory that returns audit mode, so no
KidShell build can construct the Apply context the operations require. Reflection
tests assert it, and every operation refuses a non-Apply context in a sealed
method no subclass can weaken.

So, today:

* no Windows account has been created, changed or disabled;
* no AppLocker policy has been written;
* no Assigned Access configuration exists;
* no service has been installed;
* UAC, the Winlogon shell and the Task Manager policy are untouched.

If you are reading this on a machine where secure setup was never run, that is
still the complete answer: **KidShell is not why anything is wrong.**

---

## Getting KidShell out of the way

### The app will not close

Alt+F4, or Task Manager (Ctrl+Shift+Esc) → end `KidShell`. Nothing prevents
either.

### You do not know the parent PIN

**In a developer (Debug) build** the fallback PIN `246810` works while no real
PIN has been set.

**In a release build** there is no bypass, by design. Reset it by deleting the
configuration:

1. Close KidShell.
2. Delete `kidshell.config.json` from
   `%LOCALAPPDATA%\Packages\<KidShell package>\LocalState\`.
3. Start KidShell. It runs first-run setup again.

That loses the child profile, app list and settings — everything is in that one
file. It does not touch Windows.

### Start clean

Delete the whole `LocalState` folder, or uninstall KidShell from Settings →
Apps. Both remove every trace; KidShell writes nothing outside that folder.

---

## When a security transaction exists (0.3 and later)

### Before anything is applied

A **recovery manifest** is written first, to:

```
%LOCALAPPDATA%\Packages\<KidShell package>\LocalState\recovery\recovery-<id>.json
```

It is indented, human-readable JSON in plain language, and contains the
previous value of everything about to change plus a manual rollback hint per
step. It deliberately holds **no PIN, hash, salt or password** — a file meant to
be read during a crisis is the worst place for a secret.

A transaction whose manifest could not be written does not proceed. That is
enforced by the coordinator, not by a caller remembering: the manifest store is
a required constructor dependency, the write happens between the last snapshot
and the first change, and a write that fails or throws refuses the transaction
outright.

### How rollback works

```
Preflight all → Snapshot all → Write manifest → Apply each → Verify each
any failure   → Rollback everything applied, newest first
```

### If you cancel

Cancelling is safe at any point, and what it means depends on one thing: had
anything been applied yet?

* **Before the first change** — the transaction stops and reports `Cancelled`.
  Nothing was altered, so there is nothing to undo.
* **After any change** — cancelling rolls everything back, newest first,
  exactly as a failure would. You get `RolledBack`, or `RollbackFailed` if the
  undo itself failed.
* Rollback runs on its own clock. Cancelling does not cancel the recovery —
  that would stop the undo halfway and leave the machine in the state the whole
  design exists to avoid.

A cancellation that arrives after the last operation has applied *and* verified
commits normally. There is no work left to stop, and undoing a configuration
that wholly succeeded would be worse than ignoring a request that arrived too
late.

Verification reads the change back rather than trusting that applying it
returned success, because Windows can accept a write and not honour it.

Three outcomes matter:

| State | Meaning | What to do |
| --- | --- | --- |
| `Committed` | Applied and verified | Nothing |
| `RolledBack` | Failed, everything undone | Nothing. Read the failure message. |
| `RollbackFailed` | Failed **and** the undo failed | Follow the manifest by hand. This is the only state that needs a human. |
| `Cancelled` | Stopped before any change | Nothing |
| `Refused` | KidShell declined to start | Nothing. Read the reason. |

### Undoing by hand

1. Sign in as the **recovery administrator** — the manifest names it under
   `machine.recoveryAdministrator`. Pre-flight refuses to run a transaction
   without one precisely so this account exists.
2. Open the newest `recovery-*.json`.
3. Work through `steps` **in reverse order**. Each has `previousValue`,
   `existedBefore` and `manualRollbackHint`.
   * `existedBefore: false` → the thing was created; delete it.
   * `existedBefore: true` → restore `previousValue`.
4. Sign out and back in.

---

## If a child account has been created and you cannot sign in

This is the scenario the whole design exists to prevent, so it should not
happen — pre-flight blocks any plan that would leave no enabled administrator.
If it does:

1. Sign in as the recovery administrator named in the manifest.
2. Settings → Accounts → Family & other users → remove or fix the child
   account.
3. Delete the KidShell `LocalState` folder.

If no administrator can sign in at all, that is beyond what KidShell can
repair: use Windows' own recovery (Shift + Restart → Troubleshoot) or a local
administrator you created outside KidShell. **Make sure you have one before
running secure setup on a real device** — it is the first pre-flight check for
this reason.

---

## The recovery tool

`KidShell.Recovery.exe` reads the manifests and prints what happened, plus
numbered steps in reverse order. It needs neither KidShell nor the child's
configuration, because those are exactly what is unavailable when it is needed.

```powershell
KidShell.Recovery.exe --list          # every recorded change
KidShell.Recovery.exe --outstanding   # only those needing attention
KidShell.Recovery.exe --show <id>     # one change, step by step
KidShell.Recovery.exe --export a.txt  # save the instructions to a file
```

Run it as the recovery administrator. The manifests live machine-wide, under
`C:\ProgramData\KidShell\security\recovery`, precisely so an administrator
who is not the account KidShell ran under can read them.

`--restore` exists and, in this build, says plainly that it cannot undo
automatically because this build cannot change Windows at all. That is
deliberate: a tool that appeared to work and did nothing would be worse than
one that admits its limits.

---

## Where everything lives

| | |
| --- | --- |
| Settings | `…\LocalState\kidshell.config.json` |
| Previous settings | the same path with `.bak` |
| Screen-time counter | `…\LocalState\screentime.json` |
| Recovery manifests | `…\LocalState\recovery\` |
| Log | `…\LocalState\logs\kidshell.log` |

The exact paths for the current install are shown in Parent Mode → Säkerhet →
Avancerat.

---

## Before running secure setup on a real device

The list pre-flight enforces, worth checking yourself first:

- [ ] A second administrator account exists, is enabled, and you know its
      password.
- [ ] It is **not** the account intended for the child.
- [ ] UAC is on.
- [ ] You have read the generated plan.
- [ ] The device is one you can afford to reset.
- [ ] It is not the only computer in the house.

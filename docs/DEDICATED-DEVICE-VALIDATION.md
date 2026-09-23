# Dedicated-device validation

**Read this before running KidShell's security on any machine you care about.**

Everything in KidShell's security architecture is written and tested. None of it
has ever run. This document is how that changes: an exact, ordered, repeatable
procedure for a machine you are willing to lose.

---

## Before you start

### The device

Use a **sacrificial** Windows laptop or a VM with a snapshot. Not the family
computer, not your work machine, not the only computer in the house.

| | |
| --- | --- |
| Minimum | Windows 10 version 2004 (build 19041) or later |
| For Secure Mode | Windows **Pro**, Enterprise, Education or IoT Enterprise |
| Recommended | A VM with a snapshot, so step 15 is one click |

Windows **Home** cannot do Assigned Access. Steps 9 and the Secure Mode rows of
step 10 will not apply, and KidShell will say so rather than failing oddly.

### What you need to hand

- [ ] An administrator account you will **not** give to the child, with a
      password you know works. Sign in as it once before you start.
- [ ] The RC artifacts from `tools\build-release.ps1`.
- [ ] A phone or second device to read this document on — the machine under
      test may become hard to read things on.
- [ ] Two hours. Do not start this at bedtime.

### The rule that outranks everything

**A second enabled administrator account must exist at every moment.**

KidShell's preflight refuses to proceed without one, and this is why. If you
find yourself about to click past a warning about it, stop.

---

## The procedure

Work through these in order. Each step says what to check before moving on.

### 1. Clean Windows, snapshotted

1. Install or reset Windows on the device.
2. Create your administrator account. Sign in. Set a password you have written
   down.
3. Apply all pending Windows updates, then reboot. You do not want Windows
   Update restarting the machine mid-test.
4. **Take a snapshot** if this is a VM. Name it `before-kidshell`.

**Check:** `Get-LocalUser` shows your administrator and nothing unexpected.

### 2. Verify the recovery administrator

Run as your administrator account:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-windows-state.ps1 -Out before.txt
```

Then confirm by hand:

- [ ] `Get-LocalUser | Where-Object Enabled` lists your account.
- [ ] `net localgroup Administrators` includes it.
- [ ] **Sign out and sign back in as it.** An account you have not signed into
      is an account you are guessing about.

**Check:** `before.txt` exists. Keep it — step 14 compares against it.

### 3. Install the RC

Copy the artifact directory to the device.

```powershell
Add-AppxPackage -Path .\package\KidShell.App_1.0.0.0_x64_Test\KidShell.App_1.0.0.0_x64.msix
```

An unsigned package needs developer mode enabled, or the test certificate
trusted. If you enable developer mode, note it — it is a machine change and
step 14 will see it.

Then copy `tools\KidShell.SecurityHost`, `tools\KidShell.Watchdog` and
`tools\KidShell.Recovery` to `C:\Program Files\KidShell\`.

**Check:** KidShell starts. First-run setup appears. Om KidShell reports
version `1.0.0-rc.1`.

### 4. Create the child account

Complete first-run setup, including a **real parent PIN** you will remember.

In Föräldraläge → Säkerhet, run secure setup as far as the child account step.
Choose *Skapa nytt barnkonto*.

**Check, before going further:**

- [ ] `Get-LocalUser` shows the new account, enabled.
- [ ] `net localgroup Administrators` does **not** include it.
- [ ] Your administrator account is still in that group.
- [ ] **Sign in as the child account once.** This loads its registry hive,
      which the autostart step needs — KidShell's preflight checks for it, and
      this is the step that satisfies it.

### 5. Apply Standard Mode

Back as the administrator, in Föräldraläge → Säkerhet, review the plan and
apply Standard Mode.

Read the plan before confirming. It lists every change. If something in it
surprises you, stop and work out why.

**Check:**

- [ ] The transaction reports `Committed`.
- [ ] A recovery manifest exists:
      `C:\ProgramData\KidShell\security\recovery\recovery-*.json`
- [ ] Open it. It is readable. It names your administrator account under
      `machine.recoveryAdministrator`.
- [ ] It contains **no** PIN, hash, salt or password. Search it.

### 6. Test recovery before you need it

This is the step people skip and regret.

```powershell
"C:\Program Files\KidShell\KidShell.Recovery\KidShell.Recovery.exe" --list
"C:\Program Files\KidShell\KidShell.Recovery\KidShell.Recovery.exe" --show <id>
```

**Check:**

- [ ] The tool lists the transaction from step 5.
- [ ] `--show` prints numbered steps in reverse order, in Swedish, with no
      error codes.
- [ ] Follow **one** step by hand and confirm the instruction is accurate.
      Then put it back.

### 7. Fail a transaction on purpose, and prove rollback

Force a failure and confirm KidShell undoes everything.

The simplest reliable way: configure an AppLocker policy that references a path
that does not exist, so verification fails after apply.

**Check:**

- [ ] The transaction reports `RolledBack`, not `Committed`.
- [ ] The machine state matches what it was before the attempt.
- [ ] The manifest for that transaction reports `RolledBack` and
      `requiresAttention: false`.
- [ ] Nothing in the Säkerhet page claims protection that is not there.

If it reports `RollbackFailed`, **stop and investigate**. That is the outcome
the whole design exists to avoid, and finding it here is the point of testing
here.

### 8. AppLocker, where it can be deployed

Only if the Säkerhet page reports a supported deployment channel. On stock Home
it will not, and it will say so — that is correct behaviour, not a failure.

**Check:**

- [ ] `Get-AppLockerPolicy -Effective -Xml` contains the generated rules.
- [ ] At least one collection has `EnforcementMode="Enabled"`. Rules in audit
      mode protect nobody.
- [ ] `Get-Service AppIDSvc` shows Running, StartType Automatic.
- [ ] **Sign in as the child.** An approved app starts. A non-approved one does
      not.
- [ ] **Sign in as the administrator.** Everything still starts. If it does
      not, roll back immediately — this is the lockout scenario.

### 9. Assigned Access, on Pro and above

Skip on Home.

**Check:**

- [ ] The child signs in to a restricted experience with the approved apps.
- [ ] The tailored Start menu shows only those apps.
- [ ] Your administrator account signs in to a normal desktop, unaffected.
- [ ] Rolling it back returns the child to an ordinary desktop.

### 10. Run the escape matrix

Work through every row in [`SECURITY.md`](SECURITY.md), signed in **as the
child**, and record what actually happens.

The matrix in code is a set of claims. This step is what turns them into
results. Where reality disagrees with the table, **the table is wrong** —
update `EscapeMatrix.cs` and let the tests re-check it.

Record for each row: what you did, what happened, and which mode was active.

### 11. Reboot

**Check:**

- [ ] The child account signs in.
- [ ] KidShell starts automatically.
- [ ] Screen time continues from where it was — a reboot is not a fresh day.
- [ ] The watchdog service is running.

### 12. Sleep and resume

Close the lid or sleep for at least 30 minutes.

**Check:**

- [ ] KidShell is still running and still in Barnläge.
- [ ] **Screen time did not advance while asleep.** This is the one row the
      matrix already claims as Protected, so it had better hold.
- [ ] The remaining allowance is what it was before sleeping.

### 13. Windows Update

Let Windows install updates and restart, or trigger it.

**Check:**

- [ ] The machine returns to the child's session with KidShell running.
- [ ] AppLocker rules survive.
- [ ] Assigned Access survives, if configured.
- [ ] The watchdog service is still installed and running.

An update that removes protection silently is worse than no protection, because
nobody would know.

### 14. Compare the machine state

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-windows-state.ps1 -Out after.txt
Compare-Object (Get-Content before.txt) (Get-Content after.txt)
```

**Every difference must be one you can name.** The child account, the AppLocker
policy, the service, the autostart value, the Edge policy — each should
correspond to a line in the recovery manifest.

A difference you cannot explain is a finding. Write it down.

### 15. Uninstall and restore

1. In Föräldraläge → Säkerhet, roll back the security configuration.
2. Confirm with the recovery tool that no manifest reports `requiresAttention`.
3. Uninstall KidShell: `Get-AppxPackage *KidShell* | Remove-AppxPackage`
4. Remove `C:\Program Files\KidShell\`.
5. Run the audit again and compare with `before.txt`.

**Check:**

- [ ] The child account is gone, or explicitly kept because you chose to.
- [ ] `Get-AppLockerPolicy -Effective -Xml` is back to what it was.
- [ ] `Get-Service KidShellWatchdog` reports the service does not exist.
- [ ] No Edge policy remains under `HKLM\SOFTWARE\Policies\Microsoft\Edge`.
- [ ] `Get-Service AppIDSvc` start type is back to Manual.

Then restore the `before-kidshell` snapshot anyway, and confirm the machine is
genuinely clean.

---

## What to bring back

For each of steps 4 through 14:

- what you did
- what happened
- whether it matched what KidShell claimed
- any difference in the state audit you could not account for

Where the escape matrix was wrong, correct `src/KidShell.Core/Security/EscapeMatrix.cs`
and set `VerifiedOnDevice = true` on the rows you actually verified. The tests
refuse a `Protected` claim without it, which is what makes that flag mean
something.

---

## When 1.0 becomes honest

Stable 1.0 needs all of:

- [ ] Every step above completed on a dedicated device
- [ ] Steps 8 and 9 completed on a Pro machine
- [ ] The escape matrix updated from observation, not intention
- [ ] A real code-signing certificate, and a signed package verified on a clean
      machine
- [ ] Step 15 leaving the machine genuinely clean
- [ ] At least one person who is not the author completing first-run setup
      unaided

Until then the version stays a release candidate, and the README says so.

---

## If something goes wrong

**The child cannot sign in, or the machine is unusable.**

1. Sign in as the recovery administrator named in the manifest.
2. Run the recovery tool: `KidShell.Recovery.exe --outstanding`
3. Follow the steps for any transaction needing attention.

**You cannot sign in as anybody.**

That is the scenario this entire design exists to prevent, and if it happens
the design failed — write down exactly what you did before it, because that is
the most valuable bug report this project could receive.

Then: Shift + Restart → Troubleshoot → Advanced options, or restore the
snapshot.

**KidShell will not start at all.**

The recovery tool does not need KidShell. Run it directly, from the
administrator account, and it will still read the manifests.

# Dedicated-device validation

**Do not run any part of this on your own computer.**

Everything in KidShell's security architecture is written and tested. None of it
has ever run on a real machine. This document is how that changes — and the
reason it opens with a warning is that the procedure deliberately takes away the
ability to sign in, and on the wrong machine it would simply do that.

The tooling under [`tools/device-validation/`](../tools/device-validation/)
refuses to change anything until six separate conditions hold at once. That is
not belt-and-braces; it is the acknowledgement that one copied command line
should not be able to destroy a family's computer.

---

## Part 0 — which machine, and nothing else

### The device

| | |
| --- | --- |
| Use | A **sacrificial** laptop, or a VM with a snapshot |
| Do not use | The family computer, your work machine, your development machine, the only computer in the house |
| Minimum | Windows 10 version 2004 (build 19041) or later |
| For Secure Mode | Windows **Pro**, Enterprise, Education or IoT Enterprise |
| Best | A VM with a snapshot, so undoing all of it is one click |

Windows **Home** cannot do Assigned Access and has no supported channel for
enforcing AppLocker. The capability scripts report `NOT_SUPPORTED` with a reason
rather than failing oddly later, and that is the correct result on Home — not a
bug to work around.

### Take the snapshot now

If this is a VM, snapshot it before you read any further and call it
`before-kidshell`. Every later step is easier knowing that exists.

### What you need to hand

- [ ] An administrator account you will **not** give to the child, with a
      password you know works. **Sign in as it once before you start** — an
      account you have never signed into is not a recovery account.
- [ ] The KidShell release artifacts from `tools\build-release.ps1`.
- [ ] A phone or second device to read this on. The machine under test may
      become hard to read things on.
- [ ] Two hours. Do not start this at bedtime.

### The rule that outranks everything

**A second enabled administrator account must exist at every moment.**

KidShell's preflight refuses to proceed without one. So does
`apply\01-create-child-account.ps1`. If you find yourself about to click past a
warning about it, stop.

---

## Part 1 — prove you are on the right machine

Open **PowerShell as Administrator**. Everything below assumes an elevated
window unless it says otherwise.

```powershell
cd C:\Path\To\KidShell\tools\device-validation
powershell -NoProfile -ExecutionPolicy Bypass -File .\00-preflight.ps1
```

This is read-only and safe anywhere. It prints the edition, the build, the
accounts, what KidShell already has on the machine, and — at the end — whether
the interlock would open.

**Expected on a machine that is not ready yet:**

```
INTERLOCK: CLOSED. Nothing will be changed.
  - There is no validation config, so nothing has been declared about this machine.
  - There is no dedicated-device marker at C:\ProgramData\KidShell-TestDevice\ALLOW-KIDSHELL-VALIDATION.txt.
  ...
```

That is correct. Closed is the default.

### Write the config

```powershell
Copy-Item .\device-validation.example.json .\device-validation.json
notepad .\device-validation.json
```

Fill in `machineName` (this computer), `parentAdminUser`, `childUser`, and set
`dedicatedTestDevice` to `true`. Leave `expectedChildSid` empty for now — you
will record it after the account exists. There is no password field and there is
no field a password could live in.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\02-verify-test-machine.ps1
```

**Stop immediately if** it lists signs that this is somebody's actual computer —
a browser profile, mail data, a git checkout, a domain membership — and you
cannot account for every one of them. That list is the last thing standing
between this procedure and the wrong machine.

### Declare the machine

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\00-declare-test-device.ps1
```

Without `-Apply` this is a dry run. It will ask you to type, in full:

```
I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST DEVICE
```

Then run it again with `-Apply` to actually write the marker. **Deleting that
file at any point closes the interlock again**, which is the fastest way to stop
the tooling if you change your mind.

---

## Part 2 — baseline

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\01-capture-baseline.ps1
```

It prints a run id and an evidence directory. **Write the evidence path down** —
every later script takes it as `-RunPath`.

```powershell
$run = 'C:\KidShell-Validation\20261001-120000-KIDSHELL-TEST-01'
```

- [ ] `before-machine-state.txt` exists in that directory.
- [ ] The `BEFORE` hash is printed. Step 15 compares against it.

---

## Part 3 — accounts

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\01-create-child-account.ps1
# then, once the dry run looks right:
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\01-create-child-account.ps1 -Apply
```

It asks for a password without echoing it, and never writes it anywhere.

Record the SID it prints as `expectedChildSid` in `device-validation.json`, then:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\03-verify-accounts.ps1 -RunPath $run
```

- [ ] The parent account is an **enabled administrator**.
- [ ] The child account is **enabled, standard, in no privileged group**.
- [ ] The two are **different accounts**. If this fails, stop — every denial
      later would pass for the wrong reason.
- [ ] **Sign in as the child once.** This creates its registry hive, which the
      autostart step needs.

---

## Part 4 — install KidShell and the broker service

### 4a. Build the bundle, on the development PC

```powershell
cd C:\Path\To\KidShell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-release.ps1
```

It refuses a dirty working tree, so commit first. The bundle appears under
`release-artifacts\kidshell-<version>-UNSIGNED-<arch>-<stamp>\` and contains
`components\`, `install\`, `device-validation\`, `release-manifest.json` and
`hashes.sha256`.

**Check:**

- [ ] The build printed all four components with a size — `KidShell.SecurityHost`,
      `KidShell.Watchdog`, `KidShell.Recovery`, `KidShell.DeviceValidation`. If
      any is absent the build now **stops**; it used to copy stale binaries
      silently.
- [ ] `release-manifest.json` exists and `hashes.sha256` covers it.

### 4b. Copy it to the dedicated machine

Any way you like — USB, a share, a VM folder. Then, **on the test machine**:

```powershell
cd <bundle>
Get-Content .\hashes.sha256 | ForEach-Object {
    $parts = $_ -split '\s+', 2
    $actual = (Get-FileHash $parts[1] -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $parts[0]) { "MISMATCH: $($parts[1])" }
}
```

**Stop immediately if** that prints anything. The bundle was altered in transit.

### 4c. Dry run the installer

Open **PowerShell as Administrator**:

```powershell
cd <bundle>\install
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KidShellLab.ps1
```

**Expected output ends with:**

```
MANIFEST: KidShell 1.0.0-rc.1 (x64, Release)
  commit    : <40 hex characters>
  channel   : DedicatedLabUnsigned
  signed    : False
  components: 5
  verified  : 103 file(s), 0 problem(s)

PLAN: FreshInstall
  Nothing is installed. 1.0.0-rc.1 will be installed fresh.
  THIS BUNDLE IS UNSIGNED. ...

DRY RUN COMPLETE. Nothing was written.
```

**Stop immediately if** it says `DIRTY`, reports any `PROBLEM`, any
`HASH MISMATCH`, or `PLAN: Refused` for a reason you do not understand.

### 4d. Install

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KidShellLab.ps1 -Apply
```

It will ask for the confirmation phrase. It needs the marker from Part 1, an
elevated window, and a bundle that is not a development build.

**This installs FILES ONLY.** It creates no account, registers no service,
applies no access list, deploys no AppLocker, configures no Assigned Access and
changes no shell. The installer prints that list when it finishes, because the
difference between "the binaries are on the machine" and "this computer is
locked down" is the whole shape of the next six parts.

### 4e. Verify the installation

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-KidShellInstallation.ps1
```

- [ ] `INSTALLATION: PASS`, or `INCOMPLETE` **only** because signatures were not
      verified — this is an unsigned lab build, and the verifier says so rather
      than calling a skipped check a pass.
- [ ] All four expected binaries are where the validation scripts look for them.
- [ ] No ordinary account has write access to the install root.

**Stop immediately if** the permissions section prints
`WRITABLE BY ORDINARY ACCOUNTS`. A LocalSystem service whose image the child can
replace is a privilege escalation with a service name, and nothing later in this
procedure would make up for it.

### 4f. Register the broker service

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\02-install-securityhost-service.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\02-install-securityhost-service.ps1 -Apply

powershell -NoProfile -ExecutionPolicy Bypass -File .\04-verify-securityhost-service.ps1 -RunPath $run
```

**Expected:**

```
SECURITYHOST SERVICE: NOT RUN
  [PASS   ] ...
  [NOT RUN] The child's stop attempt was not probed.
```

`NOT RUN` here is right and not a problem: the two checks that matter — whether
the **child** is refused when it tries to stop or reconfigure the service —
need the child's own session, and come in Part 6. The tooling will not report
them as passing until they have actually been tried.

---

## Part 5 — the protected store

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\03-apply-protected-store-acl.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\apply\03-apply-protected-store-acl.ps1 -Apply

powershell -NoProfile -ExecutionPolicy Bypass -File .\05-verify-protected-store.ps1 -RunPath $run
```

- [ ] `SYSTEM` and `Administrators` have `FullControl`.
- [ ] The child has `Read` and nothing else.
- [ ] `Users` has nothing.
- [ ] Inheritance is **removed**.
- [ ] The probes line says they have not run yet. That is Part 6.

---

## Part 6 — what the child cannot do

**Sign out. Sign in as the child.** Open an ordinary PowerShell window — **not**
elevated. If you elevate here, the script will tell you that nothing it observes
says anything about the child, and record `NOT RUN`.

```powershell
cd C:\Path\To\KidShell\tools\device-validation
powershell -NoProfile -ExecutionPolicy Bypass -File .\07-verify-child-denials.ps1 `
    -OutFile C:\KidShell-Validation\child-probes.json
```

It attempts five operations against a **probe file** in the protected store —
never against `parent-policy.json` or any other authoritative document — and
then tries to stop and reconfigure the service.

- [ ] `create`, `overwrite`, `append`, `rename`, `delete` are all **refused**.
- [ ] `sc stop KidShellSecurityHost` is **refused**.
- [ ] `sc config KidShellSecurityHost` is **refused**.

**Stop immediately if any of them succeed.** That is the finding; write down
exactly which one and what it said.

Then, still as the child:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\06-verify-pipe.ps1 -RunPath $run
```

- [ ] `endpoint` — the service created the pipe.
- [ ] `reachable` — a probe was answered.
- [ ] `slow-reader` — a client that waited 250 ms still got its reply.
      **This is a regression check**: the listener used to discard unread
      replies, and the symptom was a refusal arriving as "the service is
      unavailable".
- [ ] `malformed-request`, `unknown-operation`, `unsupported-protocol`,
      `oversized-frame` — all refused.
- [ ] `silent-client` — a client that connected and said nothing did not stop
      the broker for everybody.
- [ ] `instance-creation` — **the child could not create an instance of the
      pipe**. If it could, it could answer a client as though it were the
      service, including about the PIN.

Sign out, sign back in as the administrator, and feed the probe results back:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\05-verify-protected-store.ps1 `
    -RunPath $run -ProbeResultsPath C:\KidShell-Validation\child-probes.json
```

Now `PROTECTED STORE ACL` can read `PASS`, because the denials were observed
rather than inferred from an access list.

---

## Part 7 — screen time, as the child

Sign in as the child. Use KidShell normally for a few minutes, then, in a child
PowerShell window:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\09-verify-screen-time.ps1 `
    -RunPath $run -Snapshot before-restart
```

Then close KidShell, reopen it, and:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\09-verify-screen-time.ps1 `
    -RunPath $run -Snapshot after-app-restart `
    -Against "$run\screen-time-before-restart.json"
```

- [ ] Used time did **not** go down.
- [ ] The sequence did not roll back.
- [ ] Bonus minutes did not appear.
- [ ] Today did not become unlimited.

Repeat after signing out and in, and again after the reboot in Part 10. The
invariant is one sentence: **a persistence failure may make KidShell stricter
and must never make it more permissive.**

---

## Part 8 — the PIN throttle

As the child, enter the wrong PIN four times. Then:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\10-verify-pin-throttle.ps1 `
    -RunPath $run -Snapshot after-failures
```

Close KidShell, reopen it, and compare:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\10-verify-pin-throttle.ps1 `
    -RunPath $run -Snapshot after-app-restart `
    -Against "$run\pin-throttle-after-failures.json"
```

- [ ] The cooldown is **still active**.
- [ ] The failure count did not fall.

No PIN, hash, salt or document content is recorded — only a digest, a count, and
whether a cooldown is in the future. That redaction is automatic, so you cannot
accidentally attach a parent's PIN hash to a bug report.

---

## Part 9 — parent policy authority

Sign in as the child, open KidShell, and work through this with the shell in
front of you:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\08-verify-parent-actions.ps1 -RunPath $run
```

It asks about each observation and defaults every answer to **not observed**.
Pressing return through it produces a report saying nothing was checked, which is
the truth. The observations that matter:

- [ ] Changing settings raises **exactly one** consent prompt.
- [ ] **Cancelling** it leaves the previous settings in effect.
- [ ] **Approving** it applies the change.
- [ ] Granting fifteen extra minutes raises **no** prompt — that is the
      capability model working, not a hole.
- [ ] Changing the PIN **does** require the prompt.

---

## Part 10 — reboot

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\12-verify-reboot-persistence.ps1 `
    -RunPath $run -Save
```

**Now reboot the machine yourself.** No script here will reboot anything.

Sign in as the child, then as the administrator:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\12-verify-reboot-persistence.ps1 `
    -RunPath $run -Compare
```

- [ ] The security service is **running**, and was running before the child
      signed in. (If it starts late, something running as the child could claim
      the pipe name first.)
- [ ] No protected document vanished.
- [ ] Screen time continued — a reboot is not a fresh day.
- [ ] A PIN cooldown that was active is still active.

---

## Part 11 — fail closed

As the administrator:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\11-verify-service-recovery.ps1 -RunPath $run
# dry run first; then:
powershell -NoProfile -ExecutionPolicy Bypass -File .\11-verify-service-recovery.ps1 -RunPath $run -Apply
```

It stops the service, asks you four questions, and **always starts it again**.

- [ ] Screen time **refuses** rather than becoming unlimited.
- [ ] The policy does **not** fall back to the child's own JSON file.
- [ ] A PIN cooldown is still enforced.
- [ ] The UI says plainly that enforcement is unavailable.

A product that gets any of these wrong is worse than one with no protection,
because a parent has been told it is protected.

---

## Part 12 — AppLocker and Assigned Access

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\13-check-applocker-capability.ps1 -RunPath $run
powershell -NoProfile -ExecutionPolicy Bypass -File .\14-check-assigned-access-capability.ps1 -RunPath $run
```

These **report** and apply nothing. On Pro they should say `SUPPORTED`; on Home
they will say `NOT_SUPPORTED` with a reason. Deploying a real AppLocker policy
and configuring Assigned Access belong to KidShell's own transaction model,
which writes a recovery manifest before it changes anything — see
[RECOVERY.md](RECOVERY.md). Follow the product's own Säkerhet page for those, not
a script here.

---

## Part 13 — close the run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\15-capture-final-state.ps1 -RunPath $run
powershell -NoProfile -ExecutionPolicy Bypass -File .\16-generate-report.ps1 -RunPath $run
```

The machine state **will** differ from the baseline — you applied real security.
Every difference must correspond to a line in a recovery manifest, and
`15-capture-final-state.ps1` prints them so you can account for them one by one.

`report.md` is the deliverable. A stage you skipped reads `NOT RUN`, never
`PASS`, and the overall result is `INCOMPLETE` until every stage has an answer.
**Do not edit the report to make it look better.** An incomplete honest report is
worth more than a complete invented one.

---

## When to stop immediately

- Any child probe **succeeds** where it should have been refused.
- The child can create an instance of `KidShell.Security.v1`.
- The child can stop or reconfigure `KidShellSecurityHost`.
- Screen time goes **down** across any transition.
- A PIN cooldown disappears across a restart.
- The second administrator account stops being enabled or stops being an
  administrator.
- You cannot sign in as the administrator.

In each case write down exactly what you did immediately beforehand. That is the
most valuable bug report this project could receive.

---

## If something goes wrong

**The child cannot sign in, or the machine is unusable.**

1. Sign in as the recovery administrator named in the manifest.
2. `"C:\Program Files\KidShell\KidShell.Recovery\KidShell.Recovery.exe" --outstanding`
3. Follow the steps for any transaction needing attention.

**You cannot sign in as anybody.**

That is the scenario this entire design exists to prevent, and if it happens the
design failed. Shift + Restart → Troubleshoot → Advanced options, or restore the
snapshot.

**KidShell will not start at all.** The recovery tool does not need it. Run it
directly from the administrator account; it still reads the manifests.

---

## Undoing it

Use the uninstaller, not a recursive delete. From an elevated session:

```powershell
cd <bundle>\install
powershell -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-KidShellLab.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-KidShellLab.ps1 -Apply -StopServices
```

It removes exactly what the install receipt says was installed, reports anything
under the install root that the receipt does not mention and leaves it alone, and
**does not touch `C:\ProgramData\KidShell`**.

That last part is deliberate, and it is why this replaced the
`Remove-Item C:\ProgramData\KidShell -Recurse -Force` that used to be here.
Under that path live the recovery manifests — the record of how to undo each
security change — and the protected store, which may still hold the policy the
device is running on. A blind recursive delete takes away the one thing you need
if the uninstall itself goes wrong.

When you genuinely want the installation record gone as well:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-KidShellLab.ps1 `
    -Apply -StopServices -RemoveState
```

That still keeps the recovery manifests and the protected store. Remove those by
hand, after reading [RECOVERY.md](RECOVERY.md), and only once you are certain.

Then the account and the marker:

```powershell
Remove-LocalUser -Name '<the child account>'
Remove-Item -Recurse -Force 'C:\ProgramData\KidShell-TestDevice'
```

- [ ] `Get-Service *KidShell*` returns nothing.
- [ ] `C:\ProgramData\KidShell` is gone.
- [ ] `Get-Service AppIDSvc` start type is back to Manual.
- [ ] `15-capture-final-state.ps1` shows no remaining differences you cannot
      account for.

Then restore the `before-kidshell` snapshot anyway and confirm the machine is
genuinely clean.

---

## What to bring back

For each part: what you did, what happened, whether it matched what KidShell
claimed, and any state difference you could not account for. Bring the whole
evidence directory — it contains no secrets by construction.

Where the escape matrix was wrong, correct
`src/KidShell.Core/Security/EscapeMatrix.cs` and set `VerifiedOnDevice = true`
on the rows you actually verified. The tests refuse a `Protected` claim without
it, which is what makes that flag mean something.

---

## When 1.0 becomes honest

- [ ] Every part above completed on a dedicated device
- [ ] Parts 12's AppLocker and Assigned Access rows completed on a **Pro** machine
- [ ] The escape matrix updated from observation, not intention
- [ ] A real code-signing certificate, and a signed package verified on a clean
      machine
- [ ] The undo section leaving the machine genuinely clean
- [ ] At least one person who is not the author completing first-run setup
      unaided

Until then the version stays a release candidate, and the README says so.

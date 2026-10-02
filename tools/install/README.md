# tools/install

The **lab** installer for a dedicated validation device.

LAB is in the name because this is not the consumer installer. It exists so a
sacrificial test machine can be given a known build, and it refuses to run
anywhere else.

Read [docs/DEDICATED-DEVICE-VALIDATION.md](../../docs/DEDICATED-DEVICE-VALIDATION.md)
Part 4 before running any of it.

| Script | What it does |
| --- | --- |
| `Install-KidShellLab.ps1` | Installs a bundle. Dry run unless `-Apply`. **Files only.** |
| `Uninstall-KidShellLab.ps1` | Removes what the receipt lists. Dry run unless `-Apply`. |
| `Test-KidShellInstallation.ps1` | Read-only. PASS / FAIL / INCOMPLETE. |

## It installs files and nothing else

The installer does not create a child account, register the security service,
install the watchdog, write an access list, deploy AppLocker, configure Assigned
Access, change the shell, touch autostart or alter any registry policy.

That separation is the design, not an omission. A person must be able to put the
binaries on a machine without locking it down, because those are two decisions
and bundling them would make the second happen by accident. Enabling the
security lives in [tools/device-validation/apply/](../device-validation/apply/),
one reviewable stage at a time.

## Gates

All of these must hold before `-Apply` writes anything:

- the six-condition dedicated-device interlock (marker, phrase, config,
  machine name, elevation, not-a-development-build)
- a release manifest that parses, is structurally sound and names no unsafe path
- every file in the bundle matching its SHA-256 **and** its length
- a full 40-character Git SHA, and a clean working tree
- an upgrade policy that permits this bundle over whatever is installed
- a destination that is the fixed install root from `InstallationLayout`, never
  a path the manifest supplied

## Where the decisions live

In `KidShell.Core.Deployment`, exercised by `KidShell.DeviceValidation.exe`.

The staging, the two verifications and above all the **rollback** are the parts
that have to be right, and a rollback can only be tested by making an install
fail halfway — which cannot be done against a real Program Files by a test that
is not allowed to write to Program Files. So the transaction is written against
an interface, tested against a fake filesystem, and run by the tool.

A PowerShell reimplementation would be a second rollback with no tests, and it
would be the one that actually ran.

## Signing

Nothing this build produces is signed. `manifests/signing-manifest.json` in each
bundle lists what must be signed before any of it is a production release, and
separately lists what is **not** signed and why — including these scripts, which
are covered by `hashes.sha256` rather than by Authenticode. That is a weaker
guarantee and the manifest says so rather than implying otherwise.

An unsigned bundle cannot be installed by a production install path at all. On
the lab path it is allowed, labelled in the bundle name, labelled by the
installer, and recorded as unsigned in the install receipt.

# KidShell deployment

How KidShell is built, packaged and — eventually — signed and shipped. Written
now so the boundaries are clear before anything is published.

---

## Current status

| | |
| --- | --- |
| Packaging | MSIX, single-project, via the Windows App SDK |
| Architecture | x64 (ARM64 not yet attempted) |
| Signing | **None.** No certificate exists. |
| Distribution | **None.** No release has been published. |
| Auto-update | **Disabled in code** (`UpdatePolicy.UpdatesEnabled = false`) |

KidShell installs today only as a development deployment on a machine with
developer mode enabled. That is the honest state, and the sections below are
what stands between it and a real installer.

---

## Building

```bash
# Developer build: fallback PIN, debug shortcut, watermark
dotnet build KidShell.sln -p:Platform=x64 -c Debug

# Production build: none of those
dotnet build KidShell.sln -p:Platform=x64 -c Release

dotnet test KidShell.sln
```

The Debug/Release distinction is the *whole* mechanism for developer mode. See
`BuildRuntimeEnvironment` for why there is no runtime switch.

### Prerequisites

* .NET 10 SDK
* Windows App SDK 2.5.1 (restored as a NuGet package)
* Windows 10 2004 / build 19041 or later
* Visual Studio is not required; `dotnet build` is enough

---

## Packaging

KidShell uses single-project MSIX: `Package.appxmanifest` lives in the app
project and the package is produced by the build rather than by a separate
`.wapproj`.

```bash
dotnet publish src/KidShell.App/KidShell.App.csproj `
  -c Release -p:Platform=x64 -p:PublishProfile=win-x64.pubxml
```

### Package identity

The development identity is `KidShell.Barnlage.Dev`, deliberately distinct from
any future production identity so a developer build and a real install can
coexist and never overwrite one another's `LocalState`.

### ARM64

Not attempted. The code has no x64 assumptions, and the P/Invoke surface
(`netapi32`, `advapi32` via the registry APIs) is architecture-neutral, so it
is expected to work — but "expected to" is not "verified", and it will not be
claimed until a build runs on an ARM64 device.

---

## Code signing

**Nothing is signed, and no certificate or key exists in this repository.**

A private key must never be committed. `.gitignore` excludes `*.pfx`, `*.snk`
and `*.p12` as a backstop, but the real rule is the one in a reviewer's head.

### What signing will need

1. An Authenticode certificate from a recognised CA — EV if SmartScreen
   reputation matters, which for a product parents install it does.
2. The certificate in a hardware token or a signing service. Not on a
   developer's disk, and not in CI.
3. A timestamp server, so signatures outlive the certificate.
4. `signtool` in the release pipeline, reading the certificate from the secure
   store rather than a file.

### The boundary

Until all four exist:

* the package is **unsigned**;
* installing it requires developer mode or a manually trusted certificate;
* SmartScreen will warn, correctly;
* **auto-update stays disabled**, because an updater that installs unsigned
  packages is remote code execution with a friendly name.

A self-signed certificate is fine for local testing and must never be presented
as a trusted publisher.

---

## Updates

The rules exist and are tested; the machinery is off.

`UpdatePolicy.UpdatesEnabled` is a hard `false` constant — a constant rather
than a setting so that enabling it shows up in a diff. When it is enabled, an
update must satisfy all of:

* served over **https**;
* **signed**;
* **SHA-256 verified** against the manifest, compared in constant time;
* a genuinely newer version (numeric comparison, and a release outranks its own
  pre-release);
* a supported upgrade path from the installed version.

Any failure means no install. There is no "install anyway" path.

---

## CI

`.github/workflows/build.yml` runs on `windows-latest`: restore, build in
Release for x64, test.

It deliberately does **not** sign, publish, deploy, or hold any secret. It also
scans `src/` for obviously machine-changing calls (`Set-AppLockerPolicy`,
`New-LocalUser`, `ExitWindowsEx`, …) as a second line of defence behind the
reflection tests — those are authoritative; the scan catches a new file nobody
tested.

The scan enumerates files explicitly with `Get-ChildItem -Recurse` and asserts
that it found some. It previously passed `src\**\*.cs` to `Select-String
-Path`, which does not recurse — so it had been quietly matching nothing, and a
guard that finds nothing is indistinguishable from one that passes. It skips
comment lines, because the source documents these APIs at length precisely to
say it does not call them.

---

## Before publishing anything

- [ ] A signing certificate exists and is stored securely
- [ ] The package is signed and the signature verifies on a clean machine
- [ ] A Release build has been installed on a machine that never had a
      developer build
- [ ] First-run setup completes and demands a real parent PIN
- [ ] The escape matrix in `SECURITY.md` has been run on a dedicated device
- [ ] `README.md` states the real lockdown status
- [ ] No private key is in the repository or in CI
- [ ] Uninstall leaves nothing behind

Until every box is ticked, KidShell is not released and is not described as
parental-control security software.

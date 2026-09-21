# KidShell / Barnläge

KidShell turns an ordinary Windows laptop into something a six-year-old can use
on their own. The child sees **Barnläge** — a bright, illustrated home screen
with a handful of large, obvious cards. A parent holds one button for three
seconds, enters a PIN, and gets **Föräldraläge**: a calmer screen for deciding
what the child may use.

> **Development status: MVP 0.1 — visual application shell.**
>
> **WINDOWS LOCKDOWN STATUS: NOT ENABLED.**
> KidShell has not changed anything about this machine's Windows configuration
> and does not restrict the child in any way yet. See
> [What MVP 0.1 deliberately does not secure](#what-mvp-01-deliberately-does-not-secure).

---

## Screenshots

| Barnläge | Föräldraläge |
| --- | --- |
| ![Child Mode](docs/screenshots/child-mode.png) | ![Parent overview](docs/screenshots/parent-overview.png) |
| ![Parent PIN](docs/screenshots/parent-pin.png) | ![Allowed apps](docs/screenshots/parent-apps.png) |

The approved visual direction lives in
[`docs/design/child-home-reference.png`](docs/design/child-home-reference.png)
and
[`docs/design/parent-mode-reference.png`](docs/design/parent-mode-reference.png).
Every screen in the app is built against those two images.

---

## What MVP 0.1 does

* A real, packaged WinUI 3 desktop application on .NET 10 and Windows App SDK 2.5.1.
* **Barnläge** — illustrated vector scene, child profile, live clock/battery/network
  readout and a responsive 4x2 grid of large app cards with hover, pressed and
  keyboard-focus states.
* **Föräldraläge** — six working pages (Översikt, Appar, Skärmtid, Webb,
  Säkerhet, Profil) behind a PIN.
* **Configuration-driven app grid.** The cards come from a JSON configuration
  document, never from XAML. Toggling an app in Parent Mode and pressing
  *Spara ändringar* changes what the child sees.
* **A launcher abstraction** (`IAppLauncher`). Rita launches Paint and
  Miniräknare launches Windows Calculator through it; missing or unconfigured
  programs produce a friendly card-level message instead of a crash.
* **A parent PIN service** with PBKDF2 hashing, plus a clearly marked
  development fallback PIN.
* **Durable local configuration** with atomic-ish writes, a `.bak` copy and
  recovery from a corrupt file.
* **Local-only logging.** No telemetry, no analytics, no network calls at all.

---

## What MVP 0.1 deliberately does NOT secure

This milestone is a visual and architectural shell. It is **not** a child
lockdown product yet, and the Säkerhet page inside the app says so in the same
words:

* No Windows account is created or modified.
* Assigned Access / kiosk mode is **not** configured.
* AppLocker and WDAC are **not** configured.
* No Group Policy, registry or UAC changes are made.
* Explorer is **not** replaced; Task Manager and Windows shortcuts still work.
* No service or watchdog is installed.
* Screen-time limits are **stored but not enforced**.
* The web filter choice is **stored but changes no browser settings** and sets
  no Edge policies.
* A child can minimise or close KidShell like any other program.

If you need a locked-down machine today, KidShell is not that yet.

---

## Requirements

| | |
| --- | --- |
| OS | Windows 10 1809 (10.0.17763) or later; developed on Windows 11 |
| SDK | .NET SDK 10.0.300 or later (`dotnet --version`) |
| Runtime | Windows App Runtime 2.5.1 (installed automatically with Visual Studio, or from Microsoft) |
| For the CLI run script | Developer Mode enabled in Windows Settings → System → For developers |

Developer Mode is needed only so that an unsigned local package layout can be
registered. It is a setting you turn on for yourself; KidShell never changes it.

---

## How to build

```bash
dotnet build KidShell.sln -p:Platform=x64
```

Core and the test project build for any platform; the app project targets
`x64` and `ARM64`.

## How to run

The app is a packaged (MSIX) WinUI 3 application, so the loose build output has
to be registered before it can start. The repository ships a script that builds,
registers and launches in one step:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1
```

Useful switches:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1 -SkipBuild
```

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1 -Unregister
```

From Visual Studio, set **KidShell.App** as the startup project and press F5 —
the normal packaged-app F5 loop works as usual.

## How to run the tests

```bash
dotnet test tests\KidShell.Core.Tests\KidShell.Core.Tests.csproj
```

---

## ⚠️ Development PIN

Until a parent sets their own PIN, Parent Mode accepts the built-in
development PIN:

```text
246810
```

This is **development only**:

* it is a compiled-in constant in `KidShell.Core/Security/DevelopmentPin.cs`,
* it is never written to the configuration file,
* it is only accepted while `DeveloperOptions.DeveloperMode` is `true`,
* the PIN screen and the Säkerhet page both say out loud that it is in use.

Shipping to real families requires removing the fallback and forcing PIN setup
during first run. That is tracked for the Windows integration milestone.

While `DeveloperMode` is true there is also a keyboard shortcut,
**Ctrl+Shift+P**, that opens the PIN prompt without the three-second hold, so
development is never blocked by the gesture.

---

## Architecture overview

```text
KidShell.App  (WinUI 3, net10.0-windows)   views, view models, design system
      │
      ▼
KidShell.Core (net10.0, no UI)             configuration, launching, PIN, logging
      ▲
      │
KidShell.Core.Tests (xunit)                68 tests, no UI and no real processes
```

* **KidShell.Core** deliberately has no Windows TFM and no WinUI reference, so
  everything important is testable without a window.
* **`IAppStateService`** is the single source of truth. Child Mode reads the
  live configuration; Parent Mode edits a detached clone and only `Commit`
  makes it real.
* **`IAppLauncher`** is the only route to another program. No view or view
  model calls `Process.Start`.
* **`IParentPinService`** hides how PINs are stored so the file-based
  implementation can be swapped for Credential Manager or Hello later.

Full detail: [`docs/architecture/MVP-0.1.md`](docs/architecture/MVP-0.1.md).

---

## Where the data lives

| | |
| --- | --- |
| Configuration | `%LOCALAPPDATA%\Packages\KidShell.Barnlage.Dev_…\LocalState\kidshell.config.json` |
| Backup | the same path with `.bak` |
| Log | `…\LocalState\logs\kidshell.log` |

Nothing is written to Program Files, and nothing leaves the machine. The exact
paths for the current install are shown on the Säkerhet page inside the app.

KidShell does not collect keystrokes, document contents, browsing contents,
passwords or chats, and has no cloud component.

---

## Assets

Every image and icon in this repository is generated or drawn for the project.
See [`assets/README.md`](assets/README.md) for provenance and licensing.

---

## Roadmap

| Milestone | Scope |
| --- | --- |
| **0.1 — Visual shell** ✅ | Child Mode, Parent Mode, PIN, configuration, launcher abstraction, tests |
| 0.2 — Fullscreen & polish | Borderless full-screen child mode behind a real `DeveloperMode` switch, first-run wizard, forced PIN setup, theme work |
| 0.3 — Screen time | Session watchdog that actually enforces the stored weekday/weekend limits, warnings before time runs out |
| 0.4 — Web | A real allowlist browser or Edge policy integration for the stored web mode |
| 0.5 — Windows integration | Dedicated child account, Assigned Access / kiosk, AppLocker or WDAC policy, secure sign-out for *Avsluta till Windows* |
| 0.6 — Hardening | Watchdog service, tamper resistance, signed MSIX, real deployment story |

Milestones 0.5 and later are the ones that change the machine. They are kept
strictly out of 0.1 on purpose.

---

## Licence

MIT — see [LICENSE](LICENSE).

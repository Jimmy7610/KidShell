# KidShell roadmap

Authoritative version progression. The README links here rather than
duplicating it.

**Current status: 1.0.0-rc.1 — code complete, not validated on hardware.**

> **WINDOWS LOCKDOWN STATUS: NOT ENABLED.**
>
> No KidShell build has ever applied a Windows restriction. What changed at the
> release candidate is that the code to do so now exists and is tested — nine
> operations, each with preflight, snapshot, apply, verify and rollback.
>
> They have never run. Every build is structurally incapable of running them:
> the Apply context they require cannot be constructed, and reflection tests
> keep it that way. Enabling it is a visible code change, not a flag.
>
> The remaining work is validation on a dedicated device. See
> [`DEDICATED-DEVICE-VALIDATION.md`](DEDICATED-DEVICE-VALIDATION.md).

---

## Principles

Four rules that decide arguments, in priority order.

1. **A parent must never be locked out of their own computer.** Recovery beats
   every other property. If a change cannot be rolled back, it does not ship.
2. **Never claim protection that is not verified.** The UI says "Skyddad" only
   after enforcement has been applied *and* independently confirmed. Capability
   is not protection.
3. **Documented mechanisms only.** No undocumented registry paths, no policy
   hacks, no working around an edition limit by pretending it is not there.
   When Windows says no, KidShell reports that Windows said no.
4. **Local-first.** No telemetry, no accounts, no cloud dependency, no ads.

---

## Completed

### 0.1 — Application shell ✅

The product made visible. Packaged WinUI 3 app on .NET 10 and Windows App SDK
2.5.1; Child Mode and Parent Mode built against the approved design
references; configuration-driven app grid; `IAppLauncher` abstraction; parent
PIN gate; durable JSON configuration with atomic writes and corruption
recovery; local-only logging.

### 0.1.1 — First-run onboarding ✅

Removed the shipped placeholder child ("Alice"), which made a fresh install
greet a child who did not exist. Six-screen setup — welcome, name, avatar, age,
theme, finish — writing nothing until the final confirmation, so abandoning it
leaves no partial profile. 14 vector avatars, 5 scene themes, schema 2
migration, and re-run from Parent Mode.

### 0.1.5 — Security readiness ✅

The whole security architecture, built and exercised *before* being allowed to
touch Windows. Capability detection through one pure analyser; pre-flight
checks; a generated dry-run plan; and the `AuditOnly` guarantee — a private
constructor, one factory, and no implementation of the mutation interface
anywhere, asserted by tests.

Two real bugs found by building it: `ProductName` reports "Windows 10" on
Windows 11, and a UAC split token makes an administrator look like a standard
user.

Later corrected: AppLocker enforcement is **not** edition-gated. Since
KB 5024351 every Windows 10 2004+ and Windows 11 edition enforces AppLocker
policies. What varies is *deployment*, which is now modelled as five separate
questions.

---

## In progress

### 0.2 — Product UX and production onboarding

Turning a demonstrable app into an installable one.

* Runtime mode split: developer builds and production builds, where a
  production build **cannot** be talked into developer mode.
* Production parent PIN: set and confirmed during first run, with the
  development fallback structurally unavailable outside developer builds.
* Extended setup: parent security → PIN → child → apps → screen time → web →
  security summary.
* Installed-application discovery, so a parent never types an executable path.
* Application profiles describing how a program actually behaves.
* Full-screen child experience with a development escape that never traps.

### 0.3 — Transactional Windows integration foundation

Every future machine change crosses one boundary and one boundary only:
**Prepare → Preflight → Snapshot → Apply → Verify → Commit**, with rollback on
any verification failure. No `Registry.SetValue` scattered through view models.

Includes the recovery manifest, the arming rules that make `Apply` hard to
reach on purpose, and the watchdog architecture.

### 0.4 — Child account and application control preparation

Discovery, selection and planning for a dedicated standard-user child account.
Application-control policy generation as an **output artifact** — deterministic
AppLocker XML that is validated but never applied — plus the deployment-channel
abstraction that reports honestly when a machine has no supported channel.

### 0.5 — Screen time, web and watchdog

Application-level screen-time enforcement that survives restarts and clock
changes; browser policy generation; watchdog with crash-loop detection. All
enforcement that requires the OS stays behind the 0.3 boundary.

### 0.6 — Installer, updater, deployment

MSIX packaging, prerequisite detection, uninstall behaviour, and an updater
that verifies hashes and refuses unsigned payloads. Auto-update stays disabled
until signing infrastructure exists.

### 0.7 — Hardening and escape testing

The escape-test matrix run for real on a dedicated device. Every row marked
Protected, Mitigated, Not protected, Requires Secure Mode, or Requires
dedicated-device validation — never a green tick that was not earned.

### 0.8 — Release candidate preparation

Code signing, release verification, documentation freeze, and the first build
that may legitimately apply Windows restrictions — on a test device, with a
parent present, and with rollback proven first.

---

## 1.0 — Production release

**Blocked on validation that a development machine cannot provide.** 1.0
requires all of:

* a dedicated Windows test laptop that may be reset;
* a real child Windows account, created and destroyed repeatedly;
* Assigned Access exercised on a Pro or higher edition;
* the escape matrix passed with evidence;
* rollback proven from a deliberately failed transaction;
* a signing certificate and a verified installer;
* a real child using it for a sustained period.

Until every one of those is done, KidShell is not parental-control security
software and will not be described as such.

---

## Deliberately out of scope

Recorded so they are not re-proposed:

* **Telemetry and analytics.** Not by default, not opt-in, not "anonymous".
* **Cloud accounts.** The product works with no network at all.
* **Content inspection.** No keystrokes, chats, documents, page contents or
  screenshots. Screen *time* is a duration; it is not surveillance.
* **Anti-tamper arms races.** Detecting a backward clock and logging it is
  reasonable. Fighting a determined teenager is not a goal, and pretending
  otherwise would mislead parents about what supervision KidShell replaces.
* **UI-only "security".** Hiding the taskbar and trapping Alt+Tab stops a child
  who is not trying, and is never counted as protection.

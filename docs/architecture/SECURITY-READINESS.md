# KidShell security readiness — MVP 0.1.5

**WINDOWS LOCKDOWN STATUS: NOT ENABLED.**

This milestone builds and verifies the entire security architecture *before*
KidShell is allowed to modify Windows. Nothing here changes a machine. The
point of doing it this way round is that the risky milestone should start from
a design that has already been exercised, not from a blank page.

---

## 1. Threat model

### Who KidShell is defending against

A six-year-old, and the ordinary accidents of a shared family laptop:

* wandering out of KidShell into Explorer, Settings or the Store;
* launching something that is not meant for them;
* deleting or renaming a parent's files;
* buying something, installing something, or answering a prompt they cannot read;
* reaching the open web when the parent has not allowed it.

### Who KidShell is **not** defending against

Stated plainly, because a parental-control product that is vague here is
selling a feeling rather than a protection:

* a determined teenager with a search engine;
* anyone who can boot another operating system, or use recovery media;
* anyone who knows the parent's Windows password;
* anyone with physical access to the disk;
* malware already running on the machine.

Assigned Access, AppLocker and a standard user account raise the effort
required. They do not make a machine unescapable, and KidShell's UI never says
they do — the comparison dialog ends with exactly that sentence.

### The property that matters most

**A parent must never be locked out of their own computer.** Every design
decision below bends towards that: the recovery-administrator pre-flight check,
`CanRollback` on every planned action, and the refusal to recommend Secure mode
when UAC is off.

---

## 2. Capability detection

### Why detection is centralized

Edition checks scattered through the UI rot: one screen says a feature is
available, another says it is not, and a parent cannot tell which is lying.
Every edition and capability decision goes through one pure function:

```csharp
WindowsCapabilityAnalyzer.Analyze(facts, accounts) -> WindowsSecurityCapabilities
```

It takes data and returns data. No registry, no P/Invoke, no I/O — which is why
the whole matrix, including editions this machine is not, is covered by tests.

### The layering

| Layer | Type | Project | Touches Windows |
| --- | --- | --- | --- |
| Raw facts | `WindowsSystemFacts` | Core | no (a record) |
| Reading them | `ISystemFactsProvider` | Core (interface) | — |
| | `WindowsSystemFactsProvider` | App | **reads only** |
| Accounts | `IWindowsAccountDiscovery` | Core (interface) | — |
| | `WindowsLocalAccountDiscovery` | App | **reads only** |
| Deciding | `WindowsCapabilityAnalyzer` | Core | no |
| Composing | `SecurityReadinessService` | Core | no |

`KidShell.Core` targets plain `net10.0` and has no Windows dependency at all.
The two implementations that do touch Windows live in the App project, so a
Core test physically cannot read this machine's registry.

### Detecting the edition

Two traps, both of which the naive approach falls into:

**`ProductName` lies.** On Windows 11 the registry still reports
`ProductName = "Windows 10 Home"` for compatibility. The development machine
for this milestone reports exactly that while running build 26200. Anything
derived from that string is wrong on *every* Windows 11 machine.

So:

* the **edition** comes from `EditionID` (`Core` → Home, `Professional` → Pro, …);
* the **generation** comes from the build number (≥ 22000 → Windows 11);
* the display name is composed from the two: *"Windows 11 Home"*.

`winver` output is never parsed — it is localized, and it is a UI string.

**An unknown edition is never treated as capable.** `EditionID` values KidShell
does not recognise map to `WindowsEdition.Unknown`, whose capabilities are
`Unknown`, whose `SupportsSecureMode` is false, and which never recommends
Secure. Guessing upwards would mean promising a parent a protection the next
milestone then cannot deliver.

### Detecting the account type

The subtlest bug in this milestone, and worth recording.

With UAC on, an administrator runs on a **filtered split token**. The
Administrators SID is marked deny-only, and the managed
`WindowsIdentity.Groups` API omits it entirely — `IsInRole(Administrator)`
returns false. Reading only the token reports a real administrator as a
standard user, and produces a blocker that does not exist. The first build of
this page did exactly that.

KidShell therefore separates two different questions:

| Question | Answered by |
| --- | --- |
| Is this **process** elevated right now? | `IsProcessElevated` — the token |
| Is the signed-in **account** an administrator? | matched against the enumerated local accounts, by SID |

Secure setup needs the second. Elevation is requested at the moment setup runs,
which is what the UI says: *"Windows frågar om behörighet när installationen
körs."* The account is matched by SID, and when a SID is known a name match is
**not** accepted as a fallback — two accounts can share a display name, and
granting administrator on that basis would be a real vulnerability.

### Assigned Access and AppLocker are separate

They are different features with different edition requirements, and conflating
them would overstate what a Pro machine can do.

| Edition | Assigned Access | AppLocker enforcement |
| --- | --- | --- |
| Home | no | no |
| Pro / Pro Education / Pro for Workstations | **yes** | no |
| Enterprise / Education / IoT Enterprise | **yes** | **yes** |
| Unknown | unknown (treated as no) | unknown (treated as no) |

Pro can author AppLocker rules, but Microsoft supports enforcement only on
Enterprise, Education and IoT Enterprise. KidShell reports what is supported,
not what can be made to run.

A third capability is tracked separately: **KidShell's own app allowlist**,
which gates the child grid. It works on every edition and is the app-control
story for Standard mode. The UI labels it *"KidShells applista"* with the
explicit note *"Ersätter inte Windows egen appkontroll"*, because letting a
parent read it as an OS guarantee would be the exact overclaim this milestone
exists to avoid.

---

## 3. Standard versus Secure

Three modes, with deliberately different promises.

### Development

What every 0.1.x build runs. KidShell's UI only. Nothing about Windows is
changed and the child can minimise the app and reach the rest of the machine.
The Säkerhet page says so in those words.

### Standard

For editions without Assigned Access — which on the consumer market is most of
them, Home being the default on retail laptops.

* a separate standard Windows account for the child
* KidShell's own app allowlist
* UAC separation between the child's account and the parent's
* KidShell autostart on the child's account
* browser restrictions
* recovery watchdog

Standard is **not** described as equivalent to Secure anywhere in the product.
The comparison dialog shows the difference as a table, and marks the locked
environment as *Begränsad* rather than a tick.

### Secure

Pro and above: everything in Standard, plus Assigned Access restricting the
child's sign-in to KidShell, plus AppLocker where the edition supports it.

`SupportsSecureMode` requires Assigned Access **and** UAC. Without UAC the
separate child account provides no real separation, so recommending Secure
would be a claim KidShell could not back up — the analyzer records a blocker
instead.

### Recommended versus current

Two different questions, and the report answers both separately:

* `RecommendedMode` — the best this machine *could* reach. Standard on Home.
* `CurrentMode` — what is *actually* in force. Always `Development` in 0.1.x.

Collapsing them is how a security UI ends up claiming a machine is protected
because it is capable of being protected.

---

## 4. The AuditOnly design

The guarantee is structural, not a promise in a comment.

```csharp
public enum SecurityExecutionMode { AuditOnly, Apply }
```

`Apply` exists so the architecture has somewhere to grow. What matters is that
nothing can currently produce it:

```csharp
public sealed class SecurityExecutionContext
{
    private SecurityExecutionContext(SecurityExecutionMode mode) { ... }   // private
    public static SecurityExecutionContext AuditOnly() => new(AuditOnly);  // the only factory
}
```

* the constructor is private;
* the only public factory returns `AuditOnly`;
* `Mode` has no setter;
* a future milestone that wants `Apply` must **add a factory deliberately**, in
  a diff a reviewer will see.

A mutating call cannot appear by accident, by a flipped boolean, or by a
configuration value someone sets in a JSON file.

### The mutation boundary

```csharp
public interface ISecurityMutator
{
    Task ApplyAsync(PlannedAction action, SecurityExecutionContext context, ...);
}
```

Declared, and deliberately **left without any implementation anywhere in the
solution**. A reflection test asserts that no type implements it. While that
test passes, KidShell has no mutation path at all — not a disabled one, not a
guarded one, none.

The readiness service takes no such dependency either, which a second test
asserts by inspecting its constructor parameters.

### What is read-only by construction

* `WindowsSystemFactsProvider` opens every registry key with `writable: false`
  and only queries the token.
* `WindowsLocalAccountDiscovery` imports `NetUserEnum`,
  `NetLocalGroupGetMembers` and `NetApiBufferFree` — and nothing else. The
  mutating siblings (`NetUserAdd`, `NetUserDel`, `NetUserSetInfo`,
  `NetLocalGroupAddMembers`) are not declared, so the file has no vocabulary
  for changing an account.
* `IWindowsAccountDiscovery` has exactly one method, and a test asserts no
  member name contains Create/Add/Delete/Set/Update/Enable/Disable/Password.
  Account management will need a new, visibly named interface to exist at all.
* Pre-flight checks are computed from already-gathered data, so running them
  cannot touch Windows even by mistake.
* `SecurityReadinessReport.WindowsLockdownEnabled` is a computed property
  returning `false`, with no setter.

---

## 5. Recovery prerequisites

Before any future milestone applies anything, these must hold. They are
pre-flight checks today, run read-only.

| Check | Why it exists |
| --- | --- |
| **Admin recovery account** | An enabled administrator that is not the child's account. Without it, a failed Assigned Access configuration could leave nobody able to sign in. This is the single most important check on the page. |
| **UAC enabled** | Required for the child's account to be meaningfully separated. |
| **Windows edition** | Decides Secure versus Standard. A warning, not a blocker — Home is not broken, it is just Standard. |
| **Onboarding complete** | There is no point protecting a profile that does not exist. |
| **Child name valid** | The account and the greeting both need it. |
| **At least one enabled app** | Otherwise the child is locked into an empty screen — a lockout of a different kind. |
| **Package identity** | Autostart and Assigned Access both require an installed MSIX. |
| **Configuration writable** | Settings must survive the change. |

A disabled administrator account does **not** count as recovery, which is
tested: the built-in `Administratör` account is disabled by default on a retail
Windows install, and treating it as a way back in would be a trap.

Every planned action also carries `CanRollback`, and a `ChangeRiskLevel` that
classifies **how hard it is to undo** — explicitly not a rating of how secure
the result is. The plan dialog states that distinction, so nobody reads "Låg
ändringsrisk" as "low security".

---

## 6. Why Home and Pro differ

Assigned Access — the "restricted user experience" that pins a sign-in to a
single app — is a Pro-and-above feature. It is not a licensing detail KidShell
can route around: the underlying configuration service is simply not present on
Home.

This is why Standard mode exists rather than being a degraded error state. On a
Home machine KidShell can still:

* create a separate standard account for the child,
* start itself automatically when they sign in,
* gate which apps its own grid will launch,
* keep the parent's account behind UAC.

What it cannot do on Home is prevent the child from minimising KidShell and
using the rest of that account's desktop. That is the honest difference, and it
is the row the comparison dialog marks *Begränsad*.

The product does not show a purchase link. Telling a parent their laptop is
inadequate and pointing them at a Microsoft Store upgrade page would be a sales
funnel wearing a safety feature's clothes.

---

## 7. Why hiding UI is not security

Worth stating because it is the tempting shortcut, and because a future
contributor will propose it.

Hiding the taskbar, trapping `Alt+Tab`, covering the screen with a full-screen
window and swallowing `Win` are **cosmetic**. They stop a child who is not
trying. They do not stop:

* `Ctrl+Alt+Del`, which the OS owns and no application can intercept;
* the process being killed from another session;
* the machine being rebooted into anything else;
* an accidental key combination the app did not think of.

Worse, they *feel* like security, which is the actual danger: a parent who
believes the machine is locked supervises less than one who knows it is not.

KidShell's position is therefore that anything in the app's own UI is
convenience, and only OS-level mechanisms — a separate account, Assigned
Access, AppLocker, policy — are protection. The Säkerhet page reports the two
separately and never merges them into one reassuring number. It will not
display "Skyddad" until restrictions have been applied *and verified*, and no
0.1.x build can do either.

---

## 8. What MVP 0.2 will apply

The plan the dry run generates is the actual work list. Each step already
carries its `RequiresAdmin`, `CapabilityRequired`, `ChangeRiskLevel` and
`CanRollback`.

| # | Action | Needs | Risk |
| --- | --- | --- | --- |
| 1 | Create or select the child account | admin, local accounts | High |
| 2 | Verify it is a standard user | admin | Medium |
| 3 | Verify the parent's recovery account | admin | Low |
| 4 | Write the allowed-app list | — | Low |
| 5 | Configure AppLocker *(Enterprise/Education only)* | admin, AppLocker | High |
| 6 | Configure Assigned Access *(Secure only)* | admin, Assigned Access | High |
| 7 | Configure KidShell autostart | — | Medium |
| 8 | Configure browser policy | admin | Medium |
| 9 | Install the recovery watchdog | admin | High |

To make any of it run, 0.2 must:

1. add a way to construct an `Apply` context — deliberately, in a reviewed diff;
2. write the first `ISecurityMutator` implementation, which must refuse to act
   unless handed one;
3. register it in DI, where its absence is currently conspicuous;
4. implement rollback for every step that claims `CanRollback`;
5. re-verify after applying, and only then let `CurrentMode` leave
   `Development`.

Until all five happen, the honest answer stays the one this milestone reports.

---

## 9. Audit logging

Named events, written to the local log only. No telemetry, no network calls.

| Event | Recorded |
| --- | --- |
| `SecurityCapabilityDetected` | edition, build, capability states, UAC, admin, execution mode |
| `SecurityAccountsDiscovered` | account count and recovery-candidate count |
| `SecurityPreflightRun` | checks passed, blockers, warnings |
| `SecurityPlanGenerated` | action count, target mode, and that nothing executed |
| `SecurityScanFailed` | the failure, with the exception |

No PIN, hash, salt, password or token is ever logged; a test asserts that a
scan's log output contains none of them. Account **names** are recorded only
where a parent would see them anyway — SIDs are not written to the log.

---

## 10. Testing

The security work is covered by tests that never touch the machine:

* **Edition mapping** — every `EditionID` KidShell knows, case and padding
  insensitivity, and that unrecognised values become `Unknown` rather than Pro.
* **Generation** — build-number boundaries at 22000 and 10240.
* **The `ProductName` trap** — a fixture reporting `"Windows 10 Home"` on build
  26200 must display as *Windows 11 Home*.
* **Capability matrix** — Home, Pro, Enterprise, Education and Unknown, with
  Assigned Access and AppLocker asserted independently.
* **UAC** — enabled, disabled (blocks Secure even on Pro) and unreadable.
* **Split token** — an unelevated administrator is still an administrator; a
  genuine standard user is not; SID mismatch does not fall back to name.
* **Readiness states** — `Ready`, `ReadyWithWarnings`, `NotReady` and
  `DevelopmentOnly` all reachable and asserted.
* **Pre-flight** — each check present, and the blocker/warning distinction.
* **Plan** — Secure versus Standard contents, ordering, completeness of every
  field, and that no action is ever marked executed.
* **AuditOnly** — no public factory yields `Apply`, no constructor is public,
  no type implements `ISecurityMutator`, the service takes no mutating
  dependency, and `WindowsLockdownEnabled` has no setter.
* **Non-interference** — three consecutive scans leave the configuration object
  and the configuration file byte-identical.

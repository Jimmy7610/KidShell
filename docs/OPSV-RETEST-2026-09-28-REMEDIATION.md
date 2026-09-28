# OPSV retest remediation — 2026-09-28

The OPSV technical retest compared `6cfbbe3` with `26f2eb0` and confirmed that
several earlier findings were fixed. It also found six that remained, and they
share a shape worth naming before the table: **a correct class, a thorough test
suite for it, and a production application that used a different path.**

Protected storage was designed, tested, and registered nowhere. The packaged-app
model existed in discovery and was thrown away at the boundary. That is the
least useful combination available, because it reads as solved.

Every fix below is therefore judged by what the composed product does, not by
what a class does in isolation. `tools/check-composition.ps1` and
`OpsvIntegrationTests` exist for that reason.

---

## Finding 1 — Protected storage existed and was not used

**Reproduction.** `IProtectedPolicyStore`, `ProtectedStorePlan`,
`ProtectedStoreGate` and `PolicyDataClassification` were referenced only by
themselves and their own tests. The application read the parent's PIN, the
approved apps and the screen-time policy from one JSON file in the signed-in
user's LocalState — which in the shipped architecture is the child's profile,
and a standard user has full control of their own profile.

**Root cause.** The design was complete and the composition root never changed.
Nothing in the test suite could notice: `KidShell.App` is a WinUI project and
cannot be referenced from a test assembly, so no test could ask the container
what it would resolve.

**Fix.** `ProtectedConfigurationStore` routes each part of the configuration to
the store its trust class requires, and is the single place that decides what to
do when the protected store is unusable.

| Data | Class | Store |
| --- | --- | --- |
| apps, web, screen-time settings, PIN material | `ParentPolicy` / `Secret` | protected |
| usage counter | `EnforcementState` | protected |
| name, avatar, theme, age | `ChildPersonalisation` | the ordinary per-user file |

The split follows `PolicyDataClassification`, which is now consulted by code
rather than by a reader. Protecting the avatar would mean an elevation prompt to
change a picture, which is how a product teaches people to click through
elevation prompts.

`ProtectedStoreGate` answers once, and the answer differs by build: development
falls back loudly, production refuses. Refusing means a `Failed` load status and
no save at all — and specifically **nothing is written to the child-writable
file on the way out**, because a refusal that still wrote the policy would be
the original defect with an error message attached.

Malformed protected data does not fall back either. Damaging one file must not
remove the protection from all of it.

**Production wiring.** `App.xaml.cs` registers `JsonConfigurationStore` as a
*concrete type only*, so nothing can resolve the unprotected store by asking for
`IConfigurationStore` — which is exactly how the protected one came to be
bypassed. `IProtectedPolicyStore` resolves to `FileSystemProtectedPolicyStore`
in a production build and `DevelopmentProtectedPolicyStore` in a development
one, decided by the build rather than by configuration.

**Tests.** `ProtectedStorageWiringTests` (19), `OpsvIntegrationTests` (5),
`tools/check-composition.ps1`.

**Remaining device dependency.** The ACLs. `FileSystemProtectedPolicyStore`
never creates its directory: a directory created by KidShell would be owned by
whoever ran KidShell — on a locked-down machine, the child — and that is a store
that looks like protection and is not. Provisioning stays with the elevated
operation described by `ProtectedStorePlan`, on a dedicated device. Its probe
asks "can the account KidShell runs as *write* here" by trying and cleaning up,
rather than by reasoning about ACLs on paper.

**Status.** Fixed in the product. The boundary itself is unverified until a
dedicated device applies the plan.

---

## Finding 2 — UI updated from the wrong thread

**Reproduction.** Visible in the application's own log, not only in theory.
`ScreenTimeCoordinator` ticks on a `System.Threading.Timer`, and
`ChildHomeViewModel` set bound properties straight from that callback. The log
on `main` carries **50** entries of

```
[ERROR] ScreenTime | A screen-time tick failed. | COMException:
```

roughly one a minute — every tick on which the remaining minutes changed and the
event was actually raised.

**Root cause.** An ownership boundary that did not exist. WinUI does not
reliably throw for this: it sometimes updates, sometimes drops the change, and
sometimes fails, which is worse than a dependable crash — "the time-is-up screen
did not appear" became an intermittent report with nothing to reproduce.

**Fix.** `IUiDispatcher` in Core, so a view model can depend on it without
depending on WinUI and a unit test can supply one that runs inline. A view model
that needs a real `DispatcherQueue` in order to be constructed is a view model
with no tests, which is half of why this survived.

The production implementation is captured at composition time, on the UI thread.
It cannot be captured later — `GetForCurrentThread` returns null on a background
thread, which is exactly the situation it exists to rescue. It enqueues even
when already on the UI thread, so ordering is identical either way; running
inline there and queueing here would let "5 minutes left" overtake "15 minutes
left".

**Audit of the rest.** `SystemStatusService` already marshalled its network
callback and used a `DispatcherTimer`; the security scan resumes with
`ConfigureAwait(true)` deliberately; the watchdog is pure and has no UI
subscriber. The screen-time path was the only one. The parent-session expiry
added by Finding 5 rides the same tick, so it marshals too.

**Verification.** Zero tick failures in the 197 log entries written after the
fix, against 50 before it.

**Tests.** `UiDispatcherTests`, plus three in `ScreenTimeCoordinatorTests` that
prove the event is raised off the UI thread, that a posted callback does not run
until the dispatcher runs it, and that unsubscription stops later callbacks.

**Status.** Fixed.

---

## Finding 3 — Store apps rejected by the add-app flow

**Reproduction.** A packaged app selected from the browse list was refused on the
next screen. `PrefillFrom` wrote `DiscoveredApplication.LaunchTarget` — an AUMID
— into the same field a hand-typed path goes in, and `TryBuild` checked that
field with `ManualProgramPolicy`, which requires `.exe`.

**Root cause.** The launch kind was inferred from the string, late, by whatever
happened to be looking at it. Discovery already knew: `ApplicationKind.Packaged`
was on the record the parent picked, and it was discarded at the boundary and
guessed back afterwards, badly.

**Fix.** `KidAppDefinition.LaunchKind` is persisted, defaulting to
`Win32Executable` so a configuration written before the field existed reads as
what it holds — an upgrade must not reclassify apps a family already approved.
`LaunchTargetPolicy.Check(kind, target)` is one front door that picks the rule
belonging to the kind:

| Kind | Rule |
| --- | --- |
| `Win32Executable` | unchanged: `.exe` only, no scripts, no shortcuts, no escape surfaces |
| `PackagedApp` | `PackageFamilyName!ApplicationId`, and explicitly **not** a path |
| `UriProtocol` | an explicit list, which is empty |

A path cannot be smuggled in under the packaged kind, because that kind does not
apply the executable rules. Typing in the path field resets the kind to
`Win32Executable`: a parent who prefills from a Store app and then types over
the identity has typed a path.

The protocol list is empty on purpose and has a test saying so. A scheme names
whichever application is registered for it today, which can change after the
parent approved it.

Launching a packaged app goes through `shell:AppsFolder\<AUMID>` and never
reaches the executable resolver — asking the file system about an identity
always answers "not found".

**Tests.** `LaunchKindTests` (32), including persistence round-trip, clone, and
the launcher path.

**Status.** Fixed.

---

## Finding 4 — A failed save refunded used time

**Reproduction.** Use 60 seconds, make the save fail, restart, counter reads
zero. `ScreenTimePersistenceFailureTests.A_save_failure_after_sixty_seconds_cannot_restart_at_zero`
is that scenario.

**Root cause.** Two mistakes. An unreadable counter produced a *fresh* one — the
comment said losing today's count was a minor annoyance, which is true for a
crash and false for a child who deleted the file. And `Save` returned `bool`
while all six call sites in the engine discarded it.

A test asserted the wrong behaviour. `A_corrupt_state_file_starts_a_fresh_counter`
was the bug, written down and passing; it has been corrected rather than
adjusted.

**Fix / failure semantics.** `Load` now says where the counter came from,
because "there has never been a counter" and "there was one and it cannot be
read" are different facts:

| Outcome | Meaning | Behaviour |
| --- | --- | --- |
| `FirstRun` | nothing was ever written | zero, which is true |
| `Primary` | the authoritative file | used |
| `RecoveredFromBackup` | the previous known-good copy | used; at worst over-counts |
| `Unreadable` | a counter existed and neither copy parsed | **day treated as spent** |

The rule is one-directional: a failure may over-count and may cost the parent a
reset. It may never hand back time that was spent. A parent clears an unreadable
counter with the reset they already have; a child cannot clear it by deleting a
file.

Writes are durable, not merely atomic: the content is flushed to the device
before the replace, because otherwise the replace can be ordered ahead of the
data and a power loss leaves an empty file where the counter was — the same
refund by a different route. `File.Replace` keeps the superseded copy as a
backup, so after any successful save there are two readable counters. The
authoritative file is never truncated first.

The engine notices a failed save, logs it once rather than every tick, clears
when writing works again, and keeps counting in memory so the current session is
still limited.

**Tests.** `ScreenTimePersistenceFailureTests` (12): the reported reproduction,
failed replacement, corrupt primary, deleted primary, corrupt backup, both
invalid, parent reset, genuine first run, injected IO failure, repeated
failures, recovery, and a successful restart.

**Status.** Fixed.

---

## Finding 5 — Parent PIN and session hardening

### 5A — Failed-attempt throttling

A six-digit PIN is a million candidates and nothing slowed a child down. Three
wrong answers cost nothing, because mistyping twice is ordinary; after that the
delay doubles from five seconds to a two-minute cap, at which a million guesses
takes about four years of continuous tapping.

Bounded, and it always expires — a product that can permanently lock an adult out
of their own computer has invented a worse problem than the one it solved.

The throttle is checked *before* the PIN's shape, so a malformed entry is not a
free probe, and a throttled attempt compares nothing at all, so "this one was
refused differently" is not an oracle. A malformed entry does not count as a
failure: it never reached a comparison. The state is in memory, because
persisting it would let a child clear it by deleting a file.

`Throttled` is a distinct result from `Incorrect`; telling a parent who mistyped
four times that their correct PIN is wrong is how a product teaches somebody to
distrust it.

### 5B — Auto relock

Parent Mode stayed open until somebody closed it, which on a shared machine
means it stayed open. `ParentSession` ends on the first of: **fifteen idle
minutes**, **returning to Child Mode**, or **leaving explicitly**. Idle time is
measured from the last thing the parent did, so a long setup pass is not
interrupted mid-sentence.

It is evaluated on the screen-time tick rather than on a timer of its own —
something already wakes every thirty seconds — and therefore marshals through
`IUiDispatcher`.

### 5C — PBKDF2 iteration bounds

The count comes off disk, so it comes from whoever can write that file. Only
`> 0` was checked. A count of 1 makes the hash cheap to attack offline; the more
interesting direction is up, where `int.MaxValue` is not a weak hash but a denial
of service the victim triggers by typing their own PIN.

Bounds are `100_000` to `2_100_000`, checked **before** the derivation, never
after — the point of the ceiling is that the work must not be started. A test
asserts an `int.MaxValue` verify returns in under two seconds, which is only
possible if the order is right. The floor sits below the current default so
configurations written by earlier builds still open.

**Tests.** `ParentSessionHardeningTests` (28), covering 0, negative,
`int.MinValue`, 1, minimum − 1, minimum, default, maximum, maximum + 1 and
`int.MaxValue`.

**Status.** Fixed.

---

## Finding 6 — AppLocker activation chain

Prepared security code. Nothing here applies anything to any machine.

### 6A — One validator

`CanActivate` asked whether the policy carried blocking warnings; the deployment
side asked a different question. A policy could report that it was safe and then
be refused, and nobody could say which answer was real — two validators is one
too many, because whichever is laxer decides.

`AppControlValidator` is now the only answer. `CanActivate` delegates to it, the
writer's enforcing guard delegates to it, and the parent-facing report renders
it. The builder no longer computes rule-level judgements of its own.

### 6B — Administrator recovery rules

Nothing required a recovery path. Rules now carry their own principal, because
one SID for the whole document would mean the only way to give an administrator a
way back in was to give the child one too. The recovery rules are scoped to
`BUILTIN\Administrators` and exempt from the checks that judge the child's
policy; judging them by those would block every policy that should exist. The
child's restriction is unchanged by their presence, which is the point — the
answer was never to broaden the child's policy.

### 6C — XML encoding

The declaration said `utf-16` while every byte was UTF-8. Both the
`XDeclaration` and `XmlWriterSettings` said `utf-8` and neither was obeyed: a
`StringWriter` **is** UTF-16, and an `XmlWriter` takes its encoding from the
`TextWriter` it was given rather than from the settings. A parser that believes
the declaration gets nonsense from the first non-ASCII character, and every rule
name in this product is Swedish.

Written through a stream now, with no BOM.

### 6D — Store app rules

An AUMID is not a fully-qualified path, so a packaged app failed the path check
and produced no rule at all: the parent approved the app and the policy would
have blocked it. The publisher and package family name are now recorded when the
app is approved, and each one gets its own least-privilege rule with the version
wildcarded so an approval survives an update. The publisher is never wildcarded;
without a real one the honest outcome is no rule and a warning saying why.

### 6E — Child-writable allow paths

A policy permitting execution from a folder the child can write to was
activatable with a warning. That is a lock with the key left in it, described as
locked. **Blocking** now, for path rules — a publisher rule carries a signing
identity that a file copied into a writable folder does not inherit. Covered by
tests for Downloads, Desktop, Documents, Temp, AppData and the
environment-variable forms.

### 6F — Audit versus enforce

Audit generation is always allowed; refusing it would remove the tool for
diagnosing the very problem that blocked enforcement. An audit artifact for an
unsafe policy carries `NOT SAFE TO ENFORCE` on its face.

`NotConfigured` is now guarded exactly as `Enabled` is. It reads like "off" and
is not.

### Official Microsoft references

| Topic | URL | Conclusion used |
| --- | --- | --- |
| Default rules | [understanding-applocker-default-rules](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-applocker-default-rules) | The default `%WINDIR%` path rule covers `Windows\Temp`, where the Users group may create files; Microsoft warn that allowing execution from there "might conflict with your organization's security policy". This is the basis for 6E being blocking. The default set also includes an "All files" rule for `BUILTIN\Administrators` in every collection — the basis for 6B. |
| Rule collections | [understanding-applocker-rule-collections](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understanding-applocker-rule-collections) | Five collections: Exe, Msi, Script, Dll, Appx. The DLL collection is not enabled by default and is not generated here. |
| Packaged app rules | [packaged-apps-and-packaged-app-installer-rules-in-applocker](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/packaged-apps-and-packaged-app-installer-rules-in-applocker) | "AppLocker supports only publisher rules for Packaged apps", based on publisher name, package name and package version. One rule controls the whole app and survives updates. This is the basis for 6D. |
| Enforcement inheritance | [understand-applocker-rules-and-enforcement-setting-inheritance-in-group-policy](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/understand-applocker-rules-and-enforcement-setting-inheritance-in-group-policy) | "If enforcement isn't configured and rules are present in a rule collection, those rules are enforced." This is the basis for 6F guarding `NotConfigured`. |

**Tests.** `AppLockerActivationTests` (25), plus the updated
`AppLockerLeastPrivilegeTests` and `AppControlPolicyTests`.

**Remaining device dependency.** Everything. No policy is deployed, no
Application Identity service is started, and `IAppControlDeploymentChannel` still
has no implementation — a test asserts that. Whether a generated policy behaves
as intended is a dedicated-device question.

**Status.** Fixed as prepared code.

---

## Test portability

OPSV reported nine deviations on a non-Windows host: seven Windows-path, two
culture. Both classes are the same mistake — asking the machine running the code
a question about a different machine.

`ApplicationCatalog` and `ApplicationProfileLibrary` took the file name of a
program path with `System.IO.Path.GetFileName`, which on Linux returns the whole
of `C:\Windows\System32\mspaint.exe`. A profile that matched on Windows matched
nothing there, and the generated policy was missing the child processes the
profile knows about. Both use `WindowsPath` now.

Deliberately **not** changed: `JsonConfigurationStore`, `FileLogger`,
`JsonScreenTimeStateStore` and `ProcessRunner` all take paths on the machine they
are running on, where host semantics are the correct answer. Windows product
semantics and cross-platform test-host semantics are different things.

`ScreenTimeClockRollbackTests` parsed ISO-8601 instants with the ambient culture.
`BrowserPolicyGenerator` wrote a registry `dword` with an ambient-culture
`int.Parse` and format — a product bug found while looking for the test bug,
since a `.reg` file is read by Windows rather than by a person.

**Not verified by executing on Linux**: there is no non-Windows runtime on this
machine. The fixes are by construction.

---

## What is still not proven

Physical Windows validation is **not** complete and nothing in this pass
attempted it. The development machine's Windows security state is byte-identical
before and after.

The following need a dedicated device:

* the protected store's ACLs, and therefore whether the boundary in Finding 1
  actually holds;
* whether a generated AppLocker policy behaves as the validator predicts;
* whether the administrator recovery rules actually recover a machine;
* every row of the escape matrix in `docs/SECURITY.md` marked as needing one.

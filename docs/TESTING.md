# KidShell testing

## Running the tests

```bash
dotnet test KidShell.sln
```

No Windows edition, elevation, second account or network connection is
required. That is a design constraint, not a coincidence: the security tests in
particular assert that KidShell *cannot* change a machine, and a test that
needed a real machine to prove that would be proving the wrong thing.


## External audit regression suites

Added when an independent audit's findings were re-tested against a much newer
tree. Each suite exists because a specific bug was reproduced, or because a
finding no longer reproduced and something had to keep it that way.

| Suite | What it holds | Findings |
|---|---|---|
| `SecurityPartialApplyTests` | An operation whose Apply began is rolled back however it ended | 07 |
| `ScreenTimeClockRollbackTests` | Winding the clock back never returns the allowance; a real new day does | 05 |
| `ConfigurationMalformedInputTests` | No exception from Load, and nothing downstream sees a null | 04 |
| `ScreenTimeLaunchGateTests` | When screen time says no, nothing starts — counted, not assumed | 02 |
| `OnboardingPinContractTests` | A failed save leaves setup unfinished, and no PIN reaches a log or a file | 01 |
| `AppLockerLeastPrivilegeTests` | No blanket rules, no wildcard publisher, no interpreter allowed | 06 |
| `IntegrationGapTests` | Only programs KidShell can start; one spelling per website | 08 |
| `ProtectedStorageTests` | The child cannot write policy, the parent can still fix it, production never falls back | 03 |
| `WindowsPathPortabilityTests` | Windows path semantics are the same on any host | portability |

Several of these were **mutation-tested**: the fix was temporarily reverted and
the suite had to fail. A test that passes against the bug it was written for is
not a test.


## How the suite is organised

`KidShell.Core.Tests` covers everything in `KidShell.Core`, which targets plain
`net10.0` and has no Windows dependency at all. The Windows-specific
implementations — registry reads, account enumeration, shortcut resolution —
live in `KidShell.App` and are exercised by manual QA rather than unit tests,
because mocking the registry would test the mock.

| Area | File |
| --- | --- |
| Configuration defaults, serialisation, migration, corruption | `Configuration*Tests` |
| First-run onboarding | `OnboardingTests`, `ProductionPinTests` |
| Parent PIN policy and production rules | `ParentPinTests`, `ProductionPinTests` |
| Windows capability detection | `WindowsCapabilityTests`, `AppControlCapabilityTests` |
| Security readiness and pre-flight | `SecurityReadinessTests` |
| The AuditOnly guarantee | `SecurityExecutionModeTests`, `SecurityTransactionTests` |
| Transaction lifecycle and recovery manifests | `SecurityTransactionTests` |
| Child account planning | `ChildAccountPlannerTests` |
| App control policy and AppLocker XML | `AppControlPolicyTests` |
| Installed-app discovery and profiles | `ApplicationDiscoveryTests` |
| Screen time, DST, extensions, clock tampering | `ScreenTimeEngineTests` |
| Web allowlist and browser policy | `WebPolicyTests` |
| Watchdog crash-loop logic | `WatchdogTests` |
| Update verification | `UpdatePolicyTests` |
| Child session and session control | `ChildSessionTests`, `SessionControllerTests` |
| Launcher result mapping | `AppLauncherTests` |

## Structural tests

Several tests assert things about the *shape* of the code rather than its
behaviour, because the guarantee they protect has to survive future edits by
somebody who has not read this file:

* nothing implements `ISecurityOperation`, `ISecurityMutator` or
  `IAppControlDeploymentChannel`;
* `SecurityExecutionContext` has no public constructor and exactly one factory,
  which returns `AuditOnly`;
* `DeveloperOptions` has no constructor taking a bare `bool`;
* `IWindowsAccountDiscovery` has no member whose name suggests writing;
* `BrowserPolicyGenerator` has no `Apply`/`Write`/`Install` method;
* `DeploymentChannel` has no `Registry`/`Direct`/`Force` member;
* `DevelopmentSessionController` is the only `ISessionController`.

If one of those fails, the right response is almost never to change the test.

## Time in tests

`FakeTimeProvider` drives both clocks separately, because the difference
matters:

* `Advance` moves wall clock **and** monotonic clock, like real time passing.
* `AdvanceMonotonicOnly` moves only the monotonic clock — what the engine sees
  when the machine was asleep.
* `SetWallClock` moves only the wall clock — what a clock change looks like.

Screen time is credited from the monotonic clock and the day boundary comes
from the wall clock, so those three are genuinely different scenarios.

## What is not covered by automated tests

Stated so nobody assumes otherwise:

* **The WinUI layer, as behaviour.** There is no UI automation driving
  controls. Layout is a different question and is covered - see *Layout and
  text scaling* below - but whether a click does the right thing is verified by
  manual QA against the approved design references.
* **Real Windows mutation.** Nothing applies a policy, creates an account or
  signs anybody out, so nothing tests that those work. They are planning and
  artifact generation only.
* **Real deployment channels.** `DeploymentChannelPlanner` is tested from
  fixture data; whether `Set-AppLockerPolicy` actually installs a generated
  policy is a dedicated-device question.
* **Escape testing.** See `docs/SECURITY.md`. The matrix is honest about which
  rows cannot be verified without a test machine.

## Layout and text scaling

`LayoutAudit` is a developer-mode-only harness inside the app. It drives all 26
supported effective window sizes across every screen - onboarding, Child Mode
with 0 to 16 apps, the PIN overlay, all six Parent Mode pages, the web and app
editors at their extremes - and after each one walks the visual tree reporting
clipping, overlap, controls outside the window, squeezed containers and text
that has been cut.

It is run at 100 %, 125 %, 150 %, 175 % and 200 % text scaling. Windows text
scaling multiplies the rendered font size and leaves every other dimension
alone, so a sweep scales every font declaration in the app's XAML by the same
factor, builds, measures, and restores the tree afterwards.

**The restore is verified, not assumed.** A sweep that fails to restore leaves
scaled fonts in the tree and the next sweep scales them again; they reached
400 % that way once, and every "regression" measured after that was measured
against quadrupled text rather than against any code change. The guard checks
two things afterwards: that every font size matches the committed one, and that
no file contains double-encoded UTF-8 - a restore that read UTF-8 as ANSI and
wrote it back turned every Swedish string in three files into the doubled form
where each `ä` becomes two characters, and the font check passed cleanly
throughout because it only ever compared numbers.

### The audit is not automatically right

It found nothing wrong at 200 % while the navigation on screen read
**"Skärmtic"**. The rule for silently cut text compared a TextBlock's required
width against its `ActualWidth`, and a `NoWrap` TextBlock reports the extent of
its *glyphs* there, not the slot it was arranged into - 130.4 against a
required 131.0, a difference of 0.6 epx, while a whole letter was missing. The
slot is `DesiredSize.Width`, which `Measure` clamps to the parent's constraint;
it was 122.0.

So the audit is mutation-tested like the rest. `TextWrapping="Wrap"` was
removed from one navigation label, the screen was rendered and photographed to
confirm the bug was really back, and the sweep was re-run: the rule now reports
`needs 131 epx, given 122` at every size where that label is shown. A rule that
cannot be made to fail is not a rule that passes.

## Manual QA checklist

Run before any release-shaped commit:

1. Fresh install → first-run setup appears, not Child Mode
2. Setup: welcome → name → avatar → age → theme → finish
3. Child Mode greets the configured child with the chosen avatar and theme
4. Restart → setup does **not** reappear
5. Parent PIN: correct opens Parent Mode, wrong is rejected
6. Every Parent Mode page opens
7. App toggles change the child grid after saving
8. Profile edits reach Child Mode
9. Calculator and Paint launch
10. An unconfigured app shows a friendly message, never a crash
11. Säkerhet reports this machine's real edition, build and capabilities
12. "Kör introduktionen igen" clears the profile and keeps the app list
13. Windows text scaling at 200 % (Settings → Accessibility → Text size):
    the navigation, the PIN keypad and the footer actions are all still
    readable and reachable. The layout audit covers this mechanically; this
    step is here because the audit measures a simulation of text scaling and
    a person looking at the real thing is the check on that.


---

## The two suites

| Project | Target | What it covers |
| --- | --- | --- |
| `KidShell.Core.Tests` | `net10.0` | Rules, plans, transactions, configuration, screen time, the escape matrix. Runs anywhere. |
| `KidShell.WindowsIntegration.Tests` | `net10.0-windows` | Every operation that can change Windows, run against fakes. |

**1316 tests. None of them changes the machine they run on.**

### How the Windows suite stays safe

Every operation is written against an interface in `Platform/`, and the tests
supply a fake for each: an in-memory account directory, a registry that is a
dictionary, a service control manager that is a list, a tool runner that returns
canned output. The real implementations are never constructed by a test.

That is not a testing convenience. It is what makes the suite safe to run on a
developer's own computer, and it is why CI can run it on a build agent without
the workflow having to change any Windows setting.

### Reaching the apply path at all

No KidShell build can construct an Apply-mode `SecurityExecutionContext` — that
is the structural guarantee the whole security design rests on. It also meant
that for a long time **none of the apply, verify, cancel or rollback logic was
reachable by any test**, which is exactly how a cancellation path that skipped
rollback survived review.

The tests forge one by reflection, deliberately: reaching past the language
rather than calling an API, so the product guarantee stays literally true and
forging the token remains an obvious, ugly, test-only act. It buys nothing real,
because performing a mutation still needs a platform service, and every one in
the test project is a fake.

### Mutation testing the important fixes

Where a fix matters, it is verified by breaking it again. The cancellation fix
was confirmed by restoring the old `throw;` and watching three tests fail; the
CI guards were confirmed by planting a P/Invoke outside the platform layer, a
version mismatch and a broken documentation link, and watching the build fail
each time.

A test that passes before and after the fix is not testing the fix. The same
applies to the layout audit, which is why its text-clipping rule was confirmed
by putting the bug back - see *Layout and text scaling* above.

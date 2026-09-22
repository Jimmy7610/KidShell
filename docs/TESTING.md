# KidShell testing

## Running the tests

```bash
dotnet test KidShell.sln
```

No Windows edition, elevation, second account or network connection is
required. That is a design constraint, not a coincidence: the security tests in
particular assert that KidShell *cannot* change a machine, and a test that
needed a real machine to prove that would be proving the wrong thing.

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

* **The WinUI layer.** No UI automation; screens are verified by manual QA
  against the approved design references.
* **Real Windows mutation.** Nothing applies a policy, creates an account or
  signs anybody out, so nothing tests that those work. They are planning and
  artifact generation only.
* **Real deployment channels.** `DeploymentChannelPlanner` is tested from
  fixture data; whether `Set-AppLockerPolicy` actually installs a generated
  policy is a dedicated-device question.
* **Escape testing.** See `docs/SECURITY.md`. The matrix is honest about which
  rows cannot be verified without a test machine.

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
13. 1366×768 has no clipping

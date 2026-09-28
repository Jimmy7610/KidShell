# External audit remediation

An independent audit reviewed KidShell at commit `6cfbbe3`. This document
records what happened to each finding when it was re-tested against a much
newer tree.

Every finding was **reproduced against current HEAD before anything was
changed**. That matters: three of the eight no longer reproduced, and writing
"fixed" against those without checking would have been a guess. Where a finding
did not reproduce, the resolution is a regression test that would fail if it
came back — not an assurance.

Two findings turned up *new* bugs while being tested, and those are recorded
here too.

| # | Finding | Original version | Current reproduction | Resolution | Regression test | Status |
|---|---------|------------------|----------------------|------------|-----------------|--------|
| 01 | Release onboarding cannot finish — no parent PIN step | `6cfbbe3` | **Did not reproduce.** A ParentPin step exists and a Release build refuses to finish without one. Two parts of the contract were untested: a failed final save, and PIN material on the failure path | Behaviour already correct; the untested parts are now pinned down | `OnboardingPinContractTests` (14), plus the existing `ProductionPinTests` (18) | ALREADY FIXED + VERIFIED |
| 02 | Child Mode launches apps without checking screen time | `6cfbbe3` | **Did not reproduce.** Both launch paths already asked | The check was a convention that held because two authors remembered it. Moved to the single choke point every launch passes through: `ScreenTimeGuardedLauncher` decorates the real launcher and DI hands it out as `IAppLauncher`, so a third caller cannot bypass it | `ScreenTimeLaunchGateTests` (12) | ALREADY FIXED + HARDENED |
| 03 | Security-critical state is ordinary writable JSON | `6cfbbe3` | **Reproduced.** Everything still lives in one file in the signed-in user's LocalState, which in the shipped architecture is the child's own profile | Trust boundary designed, classified item by item, and validated. **Not applied to any machine** — see below | `ProtectedStorageTests` (18) | DESIGNED + TESTED, APPLICATION BLOCKED ON DEDICATED DEVICE |
| 04 | Well-formed JSON with nulls crashes the app | `6cfbbe3` | **Reproduced.** 13 of 49 new tests failed, including the audit's own `{"schemaVersion": 1, "child": null}` — one stage earlier than they saw it | Normalization hardened; sections filled in *before* migration runs | `ConfigurationMalformedInputTests` (49) | FIXED |
| 05 | Clock rollback across midnight resets usage | `6cfbbe3` | **Reproduced.** Winding the clock from a spent Thursday into Wednesday returned the whole allowance and recorded nothing | Rollover now compares *direction*; tamper detection runs first so it still has evidence to read | `ScreenTimeClockRollbackTests` (17) | FIXED |
| 06 | AppLocker policy is far too broad | `6cfbbe3` | **Reproduced.** All three rules still generated | Replaced with an explicit system dependency manifest, named publishers, and an activation gate | `AppLockerLeastPrivilegeTests` (29) | FIXED |
| 07 | Partial apply failure is not rolled back | `6cfbbe3` | **Reproduced.** 8 of 12 new tests failed | Apply *beginning* now makes an operation a rollback candidate | `SecurityPartialApplyTests` (12) | FIXED |
| 08A | Installed app catalogue not wired in | `6cfbbe3` | **Did not reproduce.** `AddAppFlow` uses the real catalogue | Covered by existing discovery suites | `ApplicationDiscoveryTests`, `AppDiscoveryDuplicateTests` | ALREADY FIXED + VERIFIED |
| 08B | Web input not validated | `6cfbbe3` | **Did not reproduce** — but testing it found a new bug: internationalised hostnames normalized to Unicode, so one site had two spellings | Hosts converted with `IdnMapping` to the ASCII form DNS and browser policy actually use | `IntegrationGapTests`, `WebAllowlistEntryTests` | ALREADY FIXED + NEW BUG FIXED |
| 08C | File picker offers `.lnk`, `.bat`, `.cmd` | `6cfbbe3` | **Reproduced.** All four extensions still offered | `ManualProgramPolicy` accepts `.exe` only, and refuses system tools even though they are `.exe` | `IntegrationGapTests` (26) | FIXED |
| — | Windows paths interpreted by host rules | `6cfbbe3` | **Reproduced** in the same line of code as finding 06 | `WindowsPath` decides Windows semantics regardless of host | `WindowsPathPortabilityTests` (32) | FIXED |
| — | Windows text scaling breaks the layout | (not in the audit) | **Reproduced.** 2,811 defects at supported sizes at 150% | Single-cell grids given columns; chrome measured rather than guessed | `LayoutAudit` sweep | SUBSTANTIALLY FIXED — see below |

---

## Finding 03 — what was designed, and what was deliberately not done

The problem is real and reproduces today: a child with full control of their own
Windows profile has full control of the file that says which apps they may use,
how long, and what the parent's PIN hashes to.

**No integrity check fixes this.** An HMAC whose key sits beside the data,
readable by the same account, is recomputable by whoever can edit the data. It
would be a speed bump with a ceremony attached, and shipping it would be worse
than shipping nothing, because a parent would believe it.

Windows already has the mechanism: an account that is not an administrator, and
file system ACLs.

### What crosses the boundary

| Stays in the child's profile | Moves behind the boundary |
|---|---|
| `child.name` | `apps` |
| `child.avatarId` | `web.mode`, `web.allowedDomains` |
| `child.themeId` | `screenTime.*` (policy) |
| `child.age` | `screenTimeState.*` (usage counter) |
| | `securityMode` |
| | `recovery.manifests` |
| | `parentPin.hash`, `parentPin.salt` |

The left column is deliberate. Protecting a child's chosen avatar would mean a
UAC prompt to change a picture, which is how a product teaches a family to click
through UAC prompts.

### The plan

`%ProgramData%\KidShell\policy`, with inheritance removed and:

| Principal | Rights | Why |
|---|---|---|
| Administrators | Full control | The parent, elevated. The only principal that may change policy |
| SYSTEM | Full control | Backup, servicing, the elevated helper |
| The child | **Read only** | KidShell runs as the child and must load the policy it enforces |
| Everyone else | *(no entry)* | Silence is denial |

Inheritance is removed because ProgramData grants CREATOR OWNER full control of
what is created in it — a store that kept inherited permissions would be fully
controlled by whoever wrote it, which on a child's machine could be the child.

### What was not done

**No ACL was applied to any machine.** `ProtectedStorePlan` is a description
that an elevated security operation would carry out on a dedicated device. The
tests check the description, which is the part that can be wrong silently.

`ProtectedStoreGate` is the single place the fallback decision is made, and it
**refuses in production**. Falling back is the comfortable thing to do — it
always works, and it silently removes the protection the parent was told they
had.

---

## Windows text scaling

Not an audit finding; carried over from the responsiveness pass, where it was
the sole reason that gate read FAIL.

Measured by scaling all 82 font-size declarations in the app and running the
`LayoutAudit` sweep over the whole supported matrix — which is what Windows text
scaling does to a layout.

| | Before | After |
|---|---|---|
| Defects at supported sizes (150%) | 2,811 | **1,316** |
| Sizes with any defect (of 25) | 25 | **3** |
| Clean from | *(nowhere)* | **1152 epx upward** |
| Defects at normal scale | 0 | **0** |

Most of it was one shape, and it is the shape behind the audit's own Webb
finding: a `Grid` with no columns holding content and a right-aligned button.
Both children share a cell, and it works exactly until the first is wide enough
to reach the second. Larger text makes it reach.

**1,183 of the remaining 1,316 are at 780×560**, the smallest supported window
at the largest common text setting. That corner is not fixed and is recorded as
outstanding rather than described as finished.

---

## What is still blocked on hardware

Nothing in this document is waiting on more coding. Two things need a machine
this one is not:

1. **Applying the protected store's ACLs** (finding 03) requires a dedicated
   device with a real child account. The plan and its validation are here; the
   application is not.
2. **Enforcing the AppLocker policy** (finding 06) requires a Windows edition
   that supports it and a device that may be locked down. The policy, its audit
   report and its activation gate are here.

**Windows lockdown remains disabled on the development machine, and every
mutation path is exercised through fakes, generated artifacts and command
builders.** See [SECURITY.md](SECURITY.md).

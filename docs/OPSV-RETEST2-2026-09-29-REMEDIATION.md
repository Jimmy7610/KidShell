# OPSV retest 2 remediation — 2026-09-29

OPSV retest 2 reviewed merge commit `9664c4d` and confirmed the previous pass's
fixes. It found five remaining integration defects and one throttle weakness.

They have a theme, and it is worth naming before the detail. The previous pass
fixed the **shape** of each problem and left the **seam** where that shape met
the rest of the product:

* protected storage was wired, and had no working write path;
* the fallback was closed for an unusable store, and left open for a usable
  store with nothing in it;
* a backup was added, and a backup is older by definition;
* the parent session got a timeout, driven by an event that is allowed to be
  silent;
* packaged apps became addable, and their identity was dropped one method later;
* the PIN got a throttle, in memory.

Each of those is a correct component reached through a path that undoes it. The
tests in this pass are therefore end-to-end by preference, and
`tools/check-composition.ps1` grew from nine rules to fifteen.

---

## Finding 1 — Protected storage had no working write path

**OPSV reproduction.** The protected directory is considered `Ready` when the
child process cannot write to it. The same child process is then expected to
write policy and screen-time state there, and fails. If the child *can* write
there, the gate rejects the store as wrongly permissioned.

**Our reproduction.** Confirmed by reading the code: `FileSystemProtectedPolicyStore`
implemented one interface with both `Probe()` and `Write()`, and `Probe()`
returned `Ready` only when a write probe failed. Both branches were unreachable
by design.

**Root cause.** Reading and writing were one interface. They are not one
responsibility: the child reads its own policy in order to enforce it, and must
never write it.

**Architecture decision.** Split the interface, and put writing behind the
privileged helper that already exists.

`IProtectedStateReader` reads and probes. It has no write member, asserted by
reflection because it is a design property that would return the moment somebody
added a convenience.

`IProtectedStateWriter` names typed operations — `SaveParentPolicy`,
`SaveScreenTimeState`, `SavePinThrottleState`, `MarkProvisioned`. There is no
`Write(path, bytes)` and no destination parameter of any kind; a test walks the
interface and fails if any parameter name looks like a path. **Rejecting
traversal would be a defence against a contract that should not exist**, and
this one does not have it.

**Integration path.**

```
child process  ->  IProtectedStateWriter  ->  typed ElevatedRequest
               ->  KidShell.SecurityHost  ->  IPrivilegedProtectedStateStore
               ->  %ProgramData%\KidShell\policy\<fixed name>
```

`KidShell.SecurityHost` already had a typed, closed-enum protocol with
validation on the privileged side, so this is four more members on it rather
than a second privileged host. The helper validates the payload **again** on its
own side: the caller's check catches a bug early, the helper's check is the one
that matters, because it does not trust the caller.

The document-to-file-name mapping is one total switch over a closed enum, on the
privileged side. The caller says *what* it is saving; the privileged side
decides *where*.

`ElevatedContract` moved from `KidShell.WindowsIntegration` to `KidShell.Core`.
It is records, JSON and string validation with no platform calls, and the UI
project deliberately does not reference the platform-mutation layer — moving the
contract keeps that boundary rather than widening it.

**Failure semantics.** `WriterUnavailable`, `Rejected`, `Failed`. Production
fails closed on all three. On a machine with no elevated helper the broker
reports itself unavailable, which is the correct answer rather than a degraded
one.

**Regression tests.** `ProtectedWriteBrokerTests` — a Ready store is still
writable through the broker; the reader has no write member; the writer has no
path parameter; each document has one fixed destination; malformed, oversized
and non-object payloads are rejected; broker failure propagates; an unavailable
writer stops the save; development writes without elevation.

**Remaining device dependency.** The ACLs. Nothing in this pass creates or
permissions the protected directory, and `FileSystemProtectedStateReader` still
never creates it — a directory created by KidShell would be owned by whoever ran
KidShell.

**Status.** Fixed.

---

## Finding 2 — A missing protected policy reopened the unsafe fallback

**OPSV reproduction.** Protected store `Ready`, protected policy missing or
null, and the user-writable JSON is accepted as authoritative — including the
PIN, the rules, the apps and the screen-time configuration.

**Our reproduction.** `ProtectedWriteBrokerTests.A_ready_store_with_a_missing_policy_does_not_import_a_hostile_file`
is the case exactly: a hostile file in the child's own profile carrying a PIN
hash, a 1440-minute allowance and `cmd.exe` as an approved app.

**Root cause.** Inferring a first run from an absence. "This device has never
been set up" and "the policy that was here is gone" are the same absence and
completely different facts.

**Trust states.**

| State | Meaning | Production |
| --- | --- | --- |
| `GenuineFirstRun` | store ready, no provisioning marker | may initialise |
| `ExistingProtectedPolicy` | policy present and readable | reads it |
| `MissingButExpected` | provisioned, policy gone | **fails closed** |
| `ReadFailed` | store unreadable | **fails closed** |
| `Corrupt` | policy not valid | **fails closed** |
| `AccessDenied` | unreachable, or child-writable | **fails closed** |
| `VersionUnsupported` | newer schema | **fails closed** |

The provisioning marker lives in the protected store, so a child cannot
manufacture a first run by deleting a file in their own profile. A **damaged
marker counts as provisioned**, because the alternative would make damaging one
small file a route back to first-run initialisation — the shape of the bug being
closed.

A genuine first run does not read the child-writable file either: it starts from
KidShell's own defaults. Adopting a PIN from that file under a first-run label
would be the original defect with different wording.

**Transaction semantics.** Authoritative first, personalisation second. It was
the other way round, so a failed protected write returned `false` with the
child-writable file already rewritten — a save that reported failure and had half
happened. A failure now leaves both sides untouched, and there is a test that
compares the file byte-for-byte before and after a refused save.

**Regression tests.** The hostile-file case; each trust state's `MayProceed`;
first run told apart from missing; damaged marker; first save writes the marker;
corrupt policy; future schema; true first run ignores the child file; failed
write leaves the user file untouched; development still works.

**Status.** Fixed.

---

## Finding 3 — Screen-time write failure still refunded time

**OPSV reproduction.** 60 used, all writes fail, restart → 0. 120 used with the
last durable value at 60, later writes fail, restart → 60. Primary 1200 with
backup 600 and a corrupt primary → 600.

**Our reproduction.** All three are tests in `ScreenTimeMonotonicTests`, named
after the scenarios.

**Root cause.** The previous pass added a backup and treated recovery as an
availability problem. A backup is older than the primary by definition, so
recovering from one hands back the difference. And nothing durable recorded that
a session was *in progress*, so a total write failure was indistinguishable from
a machine that had never been used.

**Invariant.** A persistence failure may make KidShell stricter. It must never
make KidShell more permissive. Across any restart, used time must not decrease
because of a write failure, a crash, a corrupt file, a stale backup, a deleted
file, or an unavailable writer.

**Storage model.** Write-ahead session marker plus a monotonic high-water mark.

* The session is recorded as `Open` **before** a single second is credited. If
  that write fails there is no durable statement that time is being used, and
  enforcement reports itself unavailable rather than counting into memory that
  no restart will read back.
* Checkpoints persist as the counter advances.
* A clean shutdown writes `Clean`. Without it every ordinary close would look
  like a crash and the next start would fail closed — correct but useless,
  because it replaces a refund with a lockout.
* Recovery takes the **highest** figure for today across every readable copy,
  and never a lower one. A backup may raise; it may not lower.

**Restart semantics.**

| Durable state | Conclusion |
| --- | --- |
| `Clean` primary | use it, carry on |
| `Open` anything | figure is a floor, true total unknown → **day spent** |
| recovered from backup | floor, unknown → **day spent** |
| nothing ever written | genuinely zero |

**Failure semantics.** Fail closed, visible through `IsEnforcementUnavailable`,
logged once rather than every tick, cleared by a parent's reset — which is a
deliberate act by somebody who knows the PIN, and is exactly the authority the
state was protecting.

The corrupt-primary case deserves a note on honesty. With primary 1200 corrupt
and backup 600, the product knows "at least 600, and the session did not close".
It blocks. It does **not** inflate the recorded figure to the allowance, because
that would be inventing a measurement — the test asserts the *decision*, not a
number.

**Regression tests.** The three OPSV scenarios; high-water across two copies;
yesterday is not today's floor; deleted primary; both copies gone; unavailable
writer; writer failing mid-session; crash before checkpoint; clean shutdown;
parent reset; genuine first run; log is not spammed; failure is visible.

**Status.** Fixed.

---

## Finding 4 — Parent auto-relock was driven by the wrong signal

**OPSV reproduction.** Timeout 15 minutes, screen time disabled or already
expired, 20 simulated minutes elapsed, Parent Mode still unlocked.

**Root cause.** The session's expiry was evaluated from `ScreenTime.Changed`.
That event fires when the remaining minutes change, so it is silent when screen
time is disabled, when the allowance is unlimited, and when it has already run
out — precisely the states in which the session stayed open.

**New scheduler.** `IPeriodicScheduler`, thirty-second heartbeat, injected.
Deliberately not the screen-time timer: sharing one would re-create the coupling,
and that timer is allowed to be stopped.

**Activity semantics.** Anything the parent does in Parent Mode restarts the
window, measured from the last action rather than from when the session began,
so a long setup pass is not interrupted mid-sentence. A heartbeat tick is **not**
activity — if it were, the session would never expire at all, and there is a test
for that.

Ends on the first of: fifteen idle minutes, returning to Child Mode, leaving
explicitly.

**Regression tests.** Twenty idle minutes with no screen-time activity at all;
the heartbeat rather than a property read is what expires it; activity at minute
ten holds until twenty-five; a heartbeat is not activity; the timeout does not
fire twice; return to Child Mode locks immediately; disposal stops callbacks;
starting twice leaves one timer; a throwing callback does not kill the process.

**Status.** Fixed.

---

## Finding 5 — Store app identity was lost before AppLocker

**OPSV reproduction.** A Store app can be added as `PackagedApp`, the publisher
identity is not persisted, and policy generation produces zero individual rules
with a `packaged-app-without-identity` warning.

**Root cause.** `AddAppViewModel.PrefillFrom` carried the display name, the
AUMID and the kind, and dropped the publisher. The identity cannot be recovered
afterwards — a publisher is not derivable from a display name or an AUMID — which
is why dropping it was fatal rather than inconvenient.

**Identity fields.** `LaunchKind`, `ExecutablePath` (the AUMID),
`PackageFamilyName`, `Publisher`. The family name comes from the AUMID the
parent actually approved rather than being asked of Windows again: a rule built
from a family name the parent never saw would allow something they did not
choose.

**End-to-end path.** Discovery → browse result → `AddAppViewModel` →
`KidAppDefinition` → JSON → reload → `AppControlPolicyBuilder` → one packaged
publisher rule. `PackagedIdentityEndToEndTests` walks exactly that, and also
covers two apps, the same app twice, a rename round trip through `Clone` (a clone
that dropped the identity would silently disarm Secure Mode on the next save) and
an old configuration with none of these fields.

**Secure-mode readiness.** `KidAppDefinition.IsSecureModeReady` is derived, never
persisted, so the two cannot disagree. A packaged app without a publisher can
still run in Standard Mode; what it must not do is claim a readiness that
promises an enforcement rule which cannot be written.

**Status.** Fixed.

---

## PIN throttle persistence

**OPSV reproduction.** One service instance throttles; a new process has
attempts available again.

**Protected state.** Failed attempt count, cooldown instant, last failure
instant — written through the broker from finding 1, because a second storage
mechanism for security state is how two of them drift apart. No PIN, no attempted
values, no hash candidates; a test asserts that what was typed never reaches the
store.

**Restart behaviour.** The throttle is restored on construction. A corrupt
record, an unreadable schema and a missing record all resolve towards *there is a
cooldown*, because the alternative is that damaging one file buys attempts.

**Clock behaviour.** UTC throughout. A backwards clock needs no special case: the
stored instant is absolute, so it stays in the future longer, which is the
conservative direction. A cooldown past the policy maximum is bounded — the value
comes from whoever can write that file, and a cooldown in the year 3000 would
lock a parent out of their own computer forever, which is worse than a few free
attempts. The bounded value is **written back**, because clamping on each load
without persisting would re-anchor an absurd value to "two minutes from now" on
every restart: a permanent throttle wearing a bound.

**Status.** Fixed.

---

## Trust boundary audit

Every security-authoritative write in the product, after this pass.

| Data | Authoritative | Writer | Windows identity | Location | Child writable | Failure behaviour |
| --- | --- | --- | --- | --- | --- | --- |
| Parent PIN material | yes | `ProtectedConfigurationStore` → broker | elevated helper | `%ProgramData%\KidShell\policy\parent-policy.json` | no | save refused, nothing changes |
| Approved apps | yes | as above | elevated helper | as above | no | as above |
| Web mode and allowlist | yes | as above | elevated helper | as above | no | as above |
| Screen-time settings | yes | as above | elevated helper | as above | no | as above |
| Screen-time usage counter | yes | `ProtectedScreenTimeStateStore` → broker | elevated helper | `…\policy\screen-time-state.json` | no | enforcement unavailable; day spent |
| PIN throttle | yes | `ProtectedPinThrottleStore` → broker | elevated helper | `…\policy\pin-throttle.json` | no | in-memory throttle still applies; logged |
| Provisioning marker | yes | broker | elevated helper | `…\policy\provisioned.json` | no | machine looks unprovisioned; fails closed |
| Child name, avatar, theme, age | no | `JsonConfigurationStore` | child | `LocalState\kidshell.config.json` | **yes, by design** | avatar change lost |
| Recovery manifests | yes | elevated operations only | elevated helper | `%ProgramData%\KidShell\security\recovery` | no | transaction refuses |
| AppLocker policy XML | artifact only | nothing deploys it | — | generated for review | n/a | never applied |
| Security mode / lockdown state | n/a | **nothing** | — | — | — | no implementation exists |

The one deliberate "yes" is personalisation. Protecting a chosen avatar would
mean an elevation prompt to change a picture, which is how a product teaches a
family to click through elevation prompts.

**Development builds** write everything to `LocalState`, through
`DirectProtectedStateWriter`, and say so in the log on every fall-back. That path
is reachable only when the runtime environment is a development build.

---

## What is still not proven

No ACL has been applied. No policy has been deployed. The elevated helper has
never been run elevated on this machine, and is not installed as a service.

The following need a dedicated device:

* whether the protected store's ACLs hold, and therefore whether the boundary in
  findings 1 and 2 is real rather than described;
* whether the elevated helper can be reached from a child account in practice,
  including the UAC behaviour of a broker write;
* whether a generated AppLocker policy behaves as the validator predicts;
* whether the administrator recovery rules actually recover a machine.

Physical Windows validation is **not** complete and nothing in this pass
attempted it.

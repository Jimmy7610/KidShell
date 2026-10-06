# The privileged broker — 2026-09-30

A manual integration review of merge commit `d6cbc3e` found that the
protected-write broker added in [OPSV retest 2](OPSV-RETEST2-2026-09-29-REMEDIATION.md)
has the right shape and a transport that cannot work. Fixing the transport
turned out to require answering a question the previous pass had not: *who*,
in Windows terms, is allowed to ask for each of these things.

This document records the defect, the architecture that replaced it, and —
more importantly — what it still does not prove.

---

## 1. The original defect

`ProcessElevatedBrokerClient` started `KidShell.SecurityHost` with:

```
UseShellExecute        = false
RedirectStandardInput  = true
RedirectStandardOutput = true
```

`KidShell.SecurityHost\app.manifest` requested `asInvoker`, and `Program.cs`
refused to enter the request loop unless the process was genuinely elevated.

So on a real child account the helper started unelevated, wrote one line to
stderr and exited. Every protected write failed: the parent's policy, the
screen-time counter, the PIN throttle, the provisioning marker.

The source comments said the helper was launched with the `runas` verb. The
production client never did, and could not.

**Reproduced by** `BrokerTransportDefectTests`, which makes the launch
contract a value (`BrokerLaunchPlan`) that the production client exposes and
`Send` builds its `ProcessStartInfo` from, so the two cannot drift.

### Why `runas` was not the repair

Three separate reasons, and the tests assert each one.

**It does not compose.** A verb is ignored unless `UseShellExecute` is true,
and `UseShellExecute` forbids redirecting the standard streams the protocol
reads and writes. .NET throws `InvalidOperationException` before it resolves
the file name — the test demonstrates the runtime enforcing it rather than
restating the rule.

**It would prompt per request.** Even with the protocol rewritten to need no
redirection, the transport would raise a consent prompt every time. The
screen-time counter is written on a timer and the PIN throttle on every
attempt. A shell a child operates cannot raise a consent prompt during
ordinary enforcement.

**A parent taught to click through prompts is worse off** than one who sees a
prompt rarely and reads it. A design that prompts for a thirty-second
checkpoint has spent the credibility it needs for the prompt that matters.

---

## 2. Threat model

| Adversary | Can | Cannot |
|---|---|---|
| A curious child with the keyboard | run anything the child account may run, delete files it owns, restart the shell, change the clock | become an administrator, write `%ProgramData%\KidShell\policy`, create an instance of the broker pipe |
| A **modified KidShell.App**, running as the child | send any well-formed request to the broker; read the PIN as a parent types it | be anything other than the child's Windows principal |
| An administrator | everything | — |
| Another standard account on the machine | open nothing: the pipe's access list names the child's SID and the Administrators group | reach the broker at all once the device is provisioned |

The second row is the one that reorganised the design. **A modified
KidShell.App is indistinguishable from the real one** until code signing
exists, so no part of the boundary may rest on "the request came from
KidShell".

---

## 3. Architecture

```
KidShell.App  (standard child account)
      |
      |  IProtectedStateWriter / IParentAuthenticator / IScreenTimeParentAuthority
      v
NamedPipeElevatedBrokerClient
      |
      |  \\.\pipe\KidShell.Security.v1    length-prefixed UTF-8 JSON
      v
NamedPipeBrokerListener          (KidShell.SecurityHost, LocalSystem service)
      |  resolves the caller from the token on the connection
      v
ElevatedBrokerServer             size -> parse -> protocol -> shape
      |                          -> AUTHORITY -> transition -> act
      v
PrivilegedProtectedStateStore -> %ProgramData%\KidShell\policy\<fixed name>
```

Authority sits in the middle of that order rather than at the end, because
everything after it is privileged work and everything before it is free. A
caller that fails authorization has not caused the service to read a document,
to hash anything, or to touch the disk.

### Service identity

| | |
|---|---|
| Name | `KidShellSecurityHost` |
| Display name | KidShell Security Host |
| Account | `LocalSystem` |
| Start type | Automatic |
| Image | `%ProgramFiles%\KidShell\KidShell.SecurityHost\KidShell.SecurityHost.exe` |

`LocalSystem` rather than a virtual service account, which would be narrower
and is the usual advice. It is not usable here: the protected store's access
list has to name a principal that exists before the service is installed and
survives it being reinstalled. The narrowing is done by the contract instead —
a closed enum, an authorization matrix, and a set of monotonic rules.

**Automatic start is a security property, not a convenience.** A named pipe
belongs to whoever creates it first. A service that started after an
interactive logon would leave a window in which something running as the child
could stand up `KidShell.Security.v1` itself and answer a client's questions —
including "is this the right PIN?" — as though it were the service.

### Relationship to the watchdog

`KidShell.Watchdog` restarts the shell when it stops. It accepts no commands,
reads no configuration, exposes no endpoint, and monitors one compiled-in
target.

`KidShell.SecurityHost` holds security state and answers typed requests.

Keeping them apart is deliberate. Giving the watchdog an endpoint would turn
"restart the shell" into "do what this message says, as SYSTEM". Giving the
broker a restart loop would put a privileged request handler in the one
component that must never be interesting to talk to.

---

## 4. The pipe

| | |
|---|---|
| Name | `KidShell.Security.v1` — a compile-time constant on both sides |
| Transmission | byte mode, 4-byte big-endian length prefix, UTF-8 JSON |
| Max request | 512 KiB envelope, 256 KiB protected payload |
| Max response | 64 KiB |
| Connect timeout | 2 s |
| I/O timeout | 10 s |
| Instances | 4 |
| Client impersonation level | `Identification` |

The client never chooses the name. A client that could be pointed at another
endpoint could have the parent's policy answered by whoever created it.

The length comes first and is checked against the limit **before** a buffer is
allocated. A caller that announces four gigabytes is refused at the fourth
byte, not the fourth gigabyte, and the receiver runs as LocalSystem.

`Identification` and nothing more: the service may read who is calling, which
is the whole basis of the matrix, and may not act as them.

### Access list

| Principal | Rights |
|---|---|
| `SYSTEM` | full control |
| `BUILTIN\Administrators` | connect, change permissions |
| the child's SID (from the provisioning marker) | connect |
| everyone else | nothing |

Before provisioning there is no SID to scope to, so the fallback plan grants
Authenticated Users *connect* — and nothing else changes, because what a
caller may DO is decided per request.

**Nobody but SYSTEM gets `CreateNewInstance`.** That is the right that lets a
principal serve this pipe name itself.

Two things about this descriptor are worth recording because they were got
wrong first:

* It does not set the owner. Windows refuses an owner a process is not
  entitled to assign, so a creator that is not already SYSTEM fails to create
  the pipe at all — and the listener caught that exception and retried
  forever, so the service would have started, logged, and served nothing. The
  creator owns the object by default, and the creator is the service.
* The listener reads the message **before** it identifies the caller.
  `ImpersonateNamedPipeClient` cannot establish who a caller is until they
  have written something, and with a small pipe buffer that write is itself
  waiting for the read. Reading first costs nothing: the frame is bounded
  before a byte is allocated, and what the message *means* is not looked at
  until the caller is known.

### What the access list is not

It decides who may open the pipe. It cannot decide what they may ask for,
because every caller that gets through sends the same kinds of message. An
access list that said "Authenticated Users may write" and stopped there would
be the design this pass exists to avoid: a LocalSystem service that writes the
parent's policy for anybody who can open a file handle.

---

## 5. Caller identity

Established by the server from the connection, never from the message:

1. `pipe.RunAsClient(...)` impersonates at identification level;
2. `WindowsIdentity.GetCurrent()` gives the SID and the authentication state;
3. `GetNamedPipeClientSessionId` gives the Windows session;
4. the SID is classified.

| Observation | Class |
|---|---|
| `identity.IsSystem` | `System` |
| in the Administrators role **in the presented token** | `Administrator` |
| matches the provisioned child SID (or no child SID is known) | `ChildSession` |
| anything else, or any failure to classify | `Unknown` — refused for every operation |

Elevation is read as membership of the Administrators group in the token
presented. A non-elevated administrator presents a filtered token in which
that membership is deny-only, so this answers *false* for them. That is the
behaviour wanted: "an administrator who has consented", not "an account that
could consent if asked".

**No check looks at a process name.** A process called `KidShell.App.exe` is
not proof of anything. See §11.

---

## 6. Operation authorization

| Operation | Child session | Parent capability | Elevated administrator | SYSTEM | Unknown |
|---|---|---|---|---|---|
| `Probe` | allow | allow | allow | allow | **deny** |
| `SaveScreenTimeState` | allow¹ | allow¹ | allow | allow | deny |
| `SavePinThrottleState` | allow¹ | allow | allow | allow | deny |
| `StageParentPolicy` | allow | allow | allow | allow | deny |
| `VerifyParentPin` | allow² | allow² | allow | allow | deny |
| `GrantScreenTime` | **deny** | allow | allow | allow | deny |
| `ResetScreenTimeToday` | **deny** | allow | allow | allow | deny |
| `SaveParentPolicy` | **deny** | **deny** | allow | allow | deny |
| `CommitStagedParentPolicy` | **deny** | **deny** | allow | allow | deny |
| `MarkProvisioned` | **deny** | **deny** | allow | allow | deny |
| `CreateChildAccount` | **deny** | **deny** | allow³ | allow³ | deny |
| `DemoteChildAccount` | **deny** | **deny** | allow³ | allow³ | deny |
| `ConfigureAutostart` | **deny** | **deny** | allow³ | allow³ | deny |
| `DeployAppLockerPolicy` | **deny** | **deny** | allow³ | allow³ | deny |
| `ConfigureApplicationIdentityService` | **deny** | **deny** | allow³ | allow³ | deny |
| `ConfigureAssignedAccess` | **deny** | **deny** | allow³ | allow³ | deny |
| `DeployBrowserPolicy` | **deny** | **deny** | allow³ | allow³ | deny |
| `InstallWatchdogService` | **deny** | **deny** | allow³ | allow³ | deny |
| `InstallSecurityHostService` | **deny** | **deny** | allow³ | allow³ | deny |
| any future machine operation | **deny** by default | **deny** by default | — | — | deny |

¹ subject to the monotonic transition rules in §8 and §9.
² the service does the verifying and owns the cooldown, which is what makes
  this safe to expose.
³ authorized here, and then refused by the Apply-mode gate that no KidShell
  build can satisfy. Nothing in this product changes Windows yet.

There are no ambiguous cells. `BrokerAuthorizationPolicy.RequiredFor` is total
over the enum and defaults to `Administrator`, and
`BrokerAuthorizationTests.No_operation_is_left_without_a_decision` walks every
member.

---

## 7. Parent-policy authority

### The question

> What prevents a compromised or modified KidShell child process from calling
> `SaveParentPolicy` with an attacker-controlled PIN, apps and web policy?

Before this pass: nothing. The operation was internal, the enum was closed and
the payload was validated. **None of those is an authority check.**

### The answer

`SaveParentPolicy`, `CommitStagedParentPolicy` and `MarkProvisioned` require a
caller whose token is elevated and in the Administrators group. The child's
account is not, and cannot become one by modifying a program it can write.

A parent typing a PIN into a program running as the child does not turn that
program into an administrator, so a parent capability does **not** unlock
these either. There is a test that says so.

### The flow

```
parent edits settings in KidShell.App
      |
      v
ProtectedConfigurationStore.Save
      |-- StageParentPolicy  ----------------> service writes the staging slot
      |                                        and returns SHA-256(document)
      |
      |-- IParentPolicyApprovalChannel.Approve(digest)
      |        |
      |        v
      |   KidShell.SecurityHost --approve-policy <digest>   [ShellExecute, runas]
      |        |                                            -> ONE consent prompt
      |        v
      |   CommitStagedParentPolicy, as an elevated administrator
      |        |
      |        +-- service re-reads the staged document
      |        +-- compares SHA-256 against the approved digest
      |        +-- writes parent-policy.json, clears the slot,
      |            revokes every outstanding capability
      v
personalisation (name, avatar, theme) to the child's own file — last
```

The digest travels between the two steps because without it a modified child
process could stage something innocuous, wait for the parent to read the
consent dialog, and replace it in between.

The commit carries **only** the digest. The policy is read by the service from
its own staging slot, so what is approved is what is stored rather than
whatever the launching process chose to pass along.

**This is the one place `runas` belongs in this product.** It runs when a
parent deliberately saves settings — perhaps a handful of times in the life of
a machine — and needs no redirected streams, so the verb is compatible with
what it does.

Nothing authoritative changes if the parent declines. The staged slot is read
by nothing that enforces anything, and `ProtectedConfigurationStore` writes
the personalisation file only after the authoritative half is durable.

---

## 8. Screen-time authority

### Why the service is not a file writer

If the child's process can send `SaveScreenTimeState` freely, it asks SYSTEM
to write `UsedSeconds = 0` and SYSTEM obliges. Routing the counter through a
privileged service would then have achieved nothing at all: the protected
store would be protecting a number the child chose.

### The rules, enforced on the privileged side

Against the document the privileged side holds, not against anything the
caller supplied.

| Rule | Why |
|---|---|
| schema must match exactly | an unknown shape is not reasoned about |
| `LocalDate` must be a real `yyyy-MM-dd` | it is the key everything else is scoped by |
| `UsedSeconds` ∈ [0, 86400] | a day has no more seconds |
| `UsedSeconds` must not fall within a day | the refund |
| `Sequence` must not fall | rollback |
| `Sequence` must not advance by more than 1000 | jumping it to `int.MaxValue` makes every later honest write look like a rollback — denial of service against the parent |
| `SuspiciousClockEvents` must not fall | erasing the record of a clock change is what somebody who moved the clock wants next |
| the day may only move forward | a backwards day is not something the engine writes |
| a new day must carry no `BonusMinutes` and no `UnlimitedForToday` | otherwise "wait until tomorrow" keeps an unlimited day forever |
| a child session may not raise `BonusMinutes` | a grant is a parent's |
| a child session may not set `UnlimitedForToday` | likewise |

### The grants

`GrantScreenTime` and `ResetScreenTimeToday` are **separate operations**, not
states the caller composes. The caller says how many minutes; the privileged
side reads the counter it holds, applies the change, and writes the outcome.

The difference between "grant fifteen minutes" and "here is the new state" is
the whole security property: `{"usedSeconds": 0}` is a perfectly well-formed
state.

A reset keeps `SuspiciousClockEvents`, because that is evidence about the
machine rather than about today.

---

## 9. PIN throttle authority

### Where the comparison happens

On the privileged side. The service holds the policy document, so it holds the
PIN material, and it does the PBKDF2 comparison itself.

This is not tidiness. A throttle enforced by the process being throttled is a
suggestion, and a hash comparison performed by a program running as the child
is one a modified copy of that program can return `true` from. Neither could
be fixed by writing `ParentPinService` more carefully; the problem was where
the code ran.

The PIN therefore crosses the pipe. It is bounded at 64 characters before it
is hashed — PBKDF2 at 210,000 iterations over a caller-supplied megabyte is a
way to make a LocalSystem service burn a core on request — never logged, never
written to the protected store, and never echoed. `ElevatedRequest.Redacted()`
removes it, and every logging path goes through that.

The stored iteration count is treated as untrusted in both directions: too low
weakens the hash, too high is the same denial of service by another route.

### The capability

On success the service generates 256 bits from the system CSPRNG, remembers
them against the caller's SID **and Windows session**, and later answers "did
I issue this, to you, and is it still valid". Nothing is encoded in the value,
so there is nothing in it to forge or alter.

Lifetime 25 minutes; in memory only; dropped on a service restart (the
conservative direction — a restarted service has forgotten that anyone is a
parent); revoked wholesale when the policy changes, because a parent who
changed the PIN has ended the sessions unlocked with the old one.

### What a child session may write

More failures and a longer cooldown. Not fewer, not shorter, not cleared —
and not a cooldown beyond the policy maximum either, which would lock a parent
out of their own computer.

The service restores its own throttle from the protected store on start, so a
child who can make the service restart does not get the attempts back. That
was the same hole the in-memory throttle had, one layer down.

---

## 10. ProgramData

**No ACL has been applied by this pass.** The plan is unchanged from
[OPSV retest 2](OPSV-RETEST2-2026-09-29-REMEDIATION.md):

```
C:\ProgramData\KidShell\policy
    SYSTEM                  full control
    BUILTIN\Administrators  full control
    the child's account     read
    everyone else           nothing
    inheritance             removed
```

Read for the child because KidShell runs as the child and has to load the
policy it enforces. Read is not a weakness here: the PIN is a salted hash and
everything else is the list of rules the child is already being shown.

The staging slot (`parent-policy.staged.json`) lives in the same directory and
is deliberately **not** a member of the `ProtectedDocument` enum. That enum is
the set of documents KidShell enforces, and nothing enforces the staged file —
which is exactly what makes it safe for the child's session to fill.

---

## 11. Code signing

Everything in §5 establishes **which Windows principal** is asking. Nothing
establishes **which program**.

That is a real gap and it is not closed here. A modified `KidShell.App`,
running as the child, is a caller this design treats exactly like the real
one — correctly, because it genuinely is the same principal. What the design
achieves is that the child's principal cannot do a parent's work regardless of
which program does the asking.

Closing the rest needs signed binary identity: the service would verify the
Authenticode signature and publisher of the connecting process before
classifying it as a KidShell session. KidShell has no signing material, so
that check cannot be written honestly yet, and **no fake production signing
has been added**.

Two consequences that must not be understated:

* A compromised shell can read the PIN as the parent types it. Nothing on the
  unprivileged side of this boundary can prevent that. What changed is that
  the attempt is counted where the attacker cannot reach the counter and the
  cooldown applied where the attacker cannot shorten it, so guessing stops
  being free.
* Until the service is installed and starts automatically, a process running
  as the child could create `KidShell.Security.v1` first and answer as the
  service. The access list denies instance creation to everyone but SYSTEM;
  starting first is what makes that denial arrive in time, and only a real
  device can show that it does.

---

## 12. Service lifecycle

`SecurityHostServiceOperation` implements install, verify and rollback inside
the existing transaction model (preflight, snapshot, recovery manifest, apply,
verify, rollback). **It has never been executed.** Every operation in
`KidShell.WindowsIntegration` requires an Apply-mode `SecurityExecutionContext`,
and this solution has no public, internal or test-visible way to construct one.

Two checks the watchdog's equivalent does not make:

* the image must be under `%ProgramFiles%\KidShell`. A LocalSystem service
  whose binary the child can replace is a privilege escalation with a service
  name, and no access list on the pipe would matter.
* the start type must be Automatic, for the pipe-ownership reason in §3.

Verification fails an installed-but-stopped service, because that is worse
than a missing one: the product would look configured and every protected
write would fail.

Rollback stops the service before removing it. A service left running with its
registration deleted keeps its pipe open, and the next install would find the
name taken by a process nothing can address.

| Recovery case | Design | Status |
|---|---|---|
| service stopped unexpectedly | client reports `ServiceUnavailable`; the product fails closed | tested in-process |
| service missing | identical; `IsAvailable` is a real connection attempt, not a file-existence check | tested |
| binary missing | preflight refuses to install | tested against a fake file system |
| startup failure | verify fails on installed-but-not-running | tested against a fake SCM |
| restart while the shell runs | one message per connection, so the client holds nothing across it | tested over a real pipe |
| child cannot stop the service | a standard account cannot stop a service it has no rights to | **not verified — needs a device** |
| administrator can recover or remove it | `sc` and the Services console, plus the rollback path | **not verified — needs a device** |

---

## 13. Dedicated-device validation still required

Nothing below has been performed, and none of it can be performed on a
development machine with one administrator account.

1. Install the service and confirm it starts before interactive logon.
2. Apply the `%ProgramData%\KidShell\policy` ACL and confirm the child account
   can read and not write it.
3. From the child account, confirm a protected write succeeds **through the
   broker** and fails directly.
4. From the child account, confirm `SaveParentPolicy` is refused and the
   staged-then-approved path raises exactly one consent prompt.
5. Confirm a standard account cannot create an instance of
   `KidShell.Security.v1`, and that the service is already listening at logon.
6. Confirm the child cannot stop, disable or reconfigure the service.
7. Confirm an administrator can stop, repair and remove it.
8. Run `tools\audit-windows-state.ps1` before and after, and account for every
   difference in the recovery manifest.

See [DEDICATED-DEVICE-VALIDATION.md](DEDICATED-DEVICE-VALIDATION.md).

# tools/device-validation

The toolset for validating KidShell's security on a **dedicated** Windows test
machine.

Start with the runbook, not with a script:
[docs/DEDICATED-DEVICE-VALIDATION.md](../../docs/DEDICATED-DEVICE-VALIDATION.md).

## Safe to run anywhere

Everything in this folder is read-only except `apply/`. The numbered scripts
query Windows, write evidence, and change nothing — run them on a development
machine and most stages will correctly report `NOT RUN`, which is the honest
answer when there is nothing installed to check.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Run-ReadOnlyValidation.ps1
```

## Changes the machine

Only `apply/`. See [apply/README.md](apply/README.md). Every script there needs
six simultaneous conditions and still defaults to a dry run.

## Where the decisions live

The scripts gather facts. They judge nothing.

Every judgement — whether the interlock opens, whether an access list matches
the plan, whether a service is configured correctly, whether a stage may be
called a pass, what this edition of Windows can enforce — is made by
`KidShell.DeviceValidation.exe`, which is a thin shell over
`KidShell.Core.Security.Validation`. That code has unit tests; a PowerShell
reimplementation of it would not, and would be the one that actually ran.

```
00-preflight.ps1                        read-only survey, and whether the interlock would open
01-capture-baseline.ps1                 starts a run, captures before-machine-state.txt
02-verify-test-machine.ps1              is this machine cleared for destructive validation?
03-verify-accounts.ps1                  parent and child accounts, and that they differ
04-verify-securityhost-service.ps1      the service against SecurityHostService
05-verify-protected-store.ps1           effective rights against ProtectedStorePlan
06-verify-pipe.ps1                      the broker endpoint, including two regression checks
07-verify-child-denials.ps1             RUN AS THE CHILD: what it cannot do
08-verify-parent-actions.ps1            the staged-then-approved policy flow, as observations
09-verify-screen-time.ps1               counter semantics, snapshot by snapshot
10-verify-pin-throttle.ps1              cooldown survival, snapshot by snapshot
11-verify-service-recovery.ps1          fail-closed behaviour (needs -Apply)
12-verify-reboot-persistence.ps1        checkpoint before a reboot, compare after
13-check-applocker-capability.ps1       SUPPORTED / PARTIALLY_SUPPORTED / NOT_SUPPORTED
14-check-assigned-access-capability.ps1 the same, for the restricted shell
15-capture-final-state.ps1              after-machine-state.txt, and the differences
16-generate-report.ps1                  assembles report.md from the stage evidence
Run-ReadOnlyValidation.ps1              every read-only stage, in order
KidShellValidation.psm1                 shared plumbing: facts, interlock, evidence, stages
device-validation.example.json          the config template. Refuses as written.
apply/                                  the only scripts that change anything
```

## Evidence

Each run writes to `<EvidenceDirectory>\<timestamp>-<machine>\`. Every document
goes through automatic redaction on the way out: a field whose name looks like a
secret is replaced by `sha256:<digest>` of its value. A digest is still evidence
— two runs stay comparable, and a value can be shown not to have changed across
a reboot — and nothing has been copied.

Never stored: passwords, PINs, PIN hashes, salts, protected payloads, capability
tokens.

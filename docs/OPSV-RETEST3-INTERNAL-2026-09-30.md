# Internal OPSV retest 3 — 2026-09-30

Not an external review. This is the audit run after
[the privileged broker pass](PRIVILEGED-BROKER-SERVICE-2026-09-30.md) changed
the transport underneath all six
[OPSV retest 2](OPSV-RETEST2-2026-09-29-REMEDIATION.md) findings.

The reason to run it at all: a fix that was correct against a
process-per-request helper is not automatically correct against a LocalSystem
service that will act on whatever it is sent. Every one of the six was
attacked again, with the thing the new architecture makes possible — a child's
session sending whatever it likes to something privileged that answers.

The tests are in `tests/KidShell.Core.Tests/OpsvRetest3AuditTests.cs`.

---

## Summary

| # | Finding | Status after this pass | Changed by this pass? |
|---|---|---|---|
| 1 | Protected storage had no working write path | **Fixed, and the transport now works** | yes — the fix was correct and unreachable |
| 2 | A Ready store with no policy fell back to the child's file | Fixed, and hardened | yes — the marker is now administrator-only |
| 3 | A restart could refund screen time | Fixed, and hardened | yes — the privileged side now refuses a refund outright |
| 4 | Parent Mode never re-locked without screen-time activity | Fixed, unchanged | no, and the capability was bounded to match |
| 5 | Store identity was dropped before AppLocker | Fixed, and re-checked through the new round trip | no, but the policy now travels further |
| + | The PIN throttle reset on restart | **Fixed, and now enforceable** | yes — the child can no longer clear it |

---

## Finding 1 — the protected write path

**Status: fixed. It was the thing this pass existed to repair.**

The retest-2 architecture was right: a reader with no write member, a writer
with no destination, a privileged side that owns every path. Both properties
still hold, and `ElevatedRequest` is now checked for a destination field too,
since a caller can reach the privileged side over a pipe.

What did not hold was reachability. `ProcessElevatedBrokerClient` started the
helper unelevated, so the helper exited and every protected write failed on
every real child account.

**Attacked again:** `Finding_1_the_transport_can_now_actually_reach_the_privileged_side`
asserts the old plan cannot succeed and the new one can, without prompting.

---

## Finding 2 — the missing protected policy fallback

**Status: fixed, and the new transport opened a way to attack it that is now
closed.**

The trust states and the fail-closed behaviour are unchanged, and
`Finding_2_a_hostile_user_file_still_cannot_replace_a_missing_policy` is the
original reproduction, re-run.

The new attack: the whole fallback turns on the provisioning marker, which
distinguishes "never set up" from "the policy is gone". With a privileged
service in the picture, a child's session could have asked SYSTEM to rewrite
that marker as `provisioned: false` and walked the product back to first-run
initialisation.

`MarkProvisioned` is therefore administrator-only, and
`Finding_2_a_child_cannot_write_the_marker_that_decides_first_run` tries it.

The transaction property — a refused save leaves the user's file byte for
byte — was re-checked, because the authoritative half is now a stage followed
by an approval rather than one write. A declined consent prompt is the normal
way that half fails now, and it leaves nothing changed.

---

## Finding 3 — screen-time monotonic persistence

**Status: fixed, and considerably stronger than it was.**

The journal, the high-water mark and the fail-closed restart are unchanged,
and the retest-2 reproduction passes.

What changed is who can defeat them. In the old design the counter was written
by the child's own process, so every rule in `ScreenTimeJournalRules` was
enforced by the thing it constrained. Routing the write to SYSTEM without
adding server-side rules would have been *worse* than before: the child would
simply have asked SYSTEM for a zero and got it.

`ProtectedStateTransitionRules.ScreenTime` now refuses, on the privileged
side: a lower counter within a day, a lower or absurdly advanced sequence, a
lowered clock-event count, a day moving backwards, a new day carrying
yesterday's grants, and — from a child session — any increase in bonus
minutes or an unlimited day.

`Finding_3_the_service_refuses_the_write_the_old_design_would_have_accepted`
sends four of those and checks the stored counter afterwards.

The three changes that legitimately make things looser became their own
operations, where the privileged side computes the result rather than
accepting one.

---

## Finding 4 — parent auto-relock

**Status: fixed, unchanged, and now matched on the privileged side.**

The heartbeat, the activity semantics and the inactivity reason are untouched,
and the reproduction passes.

The question this pass raised: a session that relocks in the UI while the
service still believes a parent is present would be a relock in appearance
only. So the capability is bounded (25 minutes, shorter than any plausible
"left the shell open"), held in memory only, dropped on a service restart, and
revoked wholesale when the policy changes.

---

## Finding 5 — store identity through to AppLocker

**Status: fixed, and re-checked through a path that did not exist before.**

The end-to-end flow — discovery, add, persist, reload, rule — is unchanged and
passes.

What is new is that the policy now travels to the privileged side as a staged
document and comes back through a commit. An identity lost in that round trip
would disarm Secure Mode exactly as a lost `Clone` did, and nothing downstream
would notice.

`Finding_5_identity_survives_the_staging_round_trip` stages a packaged app,
commits it as an administrator, reads the committed document back, and asserts
the publisher, the package family name and `IsSecureModeReady`.

---

## PIN throttle

**Status: fixed in retest 2, and only now actually enforceable.**

The retest-2 fix persisted the throttle through the protected store, which
stopped a restart from clearing it. It could not stop a *modified* shell from
clearing it, because the process being throttled was the process writing the
throttle — and it was also the process deciding whether the PIN was correct.

Two changes close that:

* `ProtectedStateTransitionRules.PinThrottle` refuses, from a child session,
  any write that lowers the failure count, shortens an active cooldown or
  clears one. `The_pin_throttle_now_also_survives_a_child_that_lies_about_it`
  tries the obvious one and checks the stored value afterwards.
* `VerifyParentPin` moves the comparison to the service, which counts the
  failures, applies the cooldown and restores both from the protected store
  when it starts. A child who can make the service restart does not get the
  attempts back.

What remains open is in
[§11 of the broker document](PRIVILEGED-BROKER-SERVICE-2026-09-30.md): a
compromised shell can still read the PIN as the parent types it. Guessing is
now expensive; watching is not prevented, and needs code signing and a trusted
input path.

---

## Defects found while running this audit

Both were in code written earlier in the same pass, and both would have
shipped.

**The pipe descriptor named LocalSystem as its owner.** Windows refuses an
owner a process is not entitled to assign, so the pipe was never created; the
listener caught the exception and retried in a tight loop. The service would
have started, logged, and served nothing — the same class of defect as a
broker nothing could reach. Found by writing a test that actually opened the
pipe.

**The security host's manifest comment contained a pair of hyphens**, which is
not legal inside an XML comment. The binary would not start at all, and
Windows reports that as a side-by-side configuration error that mentions
nothing about manifests. Found by the component self-test gate.

Both are recorded here rather than quietly fixed, because the second one in
particular is a reminder that this project's gates are load-bearing: a
reviewer reading only the source would have seen a correct manifest comment.

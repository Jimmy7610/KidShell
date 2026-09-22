# KidShell security

**WINDOWS LOCKDOWN STATUS: NOT ENABLED.**

No KidShell build has ever changed a Windows security setting. This document
says what the product does, what it does not, and which of the things people
assume it does are not true.

For the architecture, see
[`architecture/SECURITY-READINESS.md`](architecture/SECURITY-READINESS.md).

---

## The honest summary

KidShell today is **a friendly shell for a supervised child**, not a lock.

A child using KidShell gets a simple screen with the apps their parent chose,
and a screen-time limit that stops KidShell launching things. They can also
minimise KidShell and use the rest of Windows, because nothing prevents it.

That will change — the plan is in [`ROADMAP.md`](ROADMAP.md) — and it will
change on a dedicated test device, not by a claim in a README.

---

## Threat model

### Defended against

A six-year-old, and the ordinary accidents of a shared family laptop: wandering
into Settings, launching something inappropriate, deleting a parent's files,
buying something, answering a prompt they cannot read.

### Not defended against

Stated plainly, because vagueness here sells a feeling instead of a protection:

* a determined teenager with a search engine;
* anyone who can boot another OS or use recovery media;
* anyone who knows the parent's Windows password;
* physical access to the disk;
* malware already running.

### The property that outranks everything

**A parent must never be locked out of their own computer.** Every design
decision bends to it: the recovery-administrator check, `CanRollback` on every
planned action, refusing Secure mode when UAC is off, and refusing to treat a
disabled built-in Administrator as a way back in.

---

## What is actually in force today

| Control | State |
| --- | --- |
| Parent PIN | **Active.** PBKDF2-SHA256, never stored in plain text. |
| KidShell app list | **Active.** Gates what the child's grid will launch. |
| Screen time | **Active at the application level.** Blocks KidShell launches; does not stop the child leaving KidShell. |
| Web mode | **Stored only.** No browser is controlled. |
| Windows child account | Not configured. |
| AppLocker | Not applied. |
| Assigned Access | Not configured — and unavailable on this edition. |
| Autostart | Not configured. |
| Watchdog service | Not installed. |
| Secure sign-out | Simulated. |

---

## Developer builds are not protected

A **Debug** build accepts the published fallback PIN `246810` while no real PIN
is set, exposes a debug shortcut into Parent Mode, and draws a development
watermark so the state is never a surprise.

A **Release** build refuses that PIN outright, has no shortcut, and cannot
finish first-run setup without a real parent PIN. There is deliberately no
environment variable, configuration key, switch or marker file that turns
developer behaviour back on — every one of those would be reachable by a child
who can open Notepad.

---

## Escape-test matrix

Run on a dedicated device before any claim of protection. Every row is marked
with what is true **today**, on a machine with no Windows lockdown applied.

Legend: **Protected** verified blocked · **Mitigated** made harder ·
**Not protected** works · **Needs Secure** requires Assigned Access ·
**Needs device** cannot be tested on a development machine

| # | Escape route | Today | Notes |
| --- | --- | --- | --- |
| 1 | Windows key | Not protected | Opens Start. Needs Secure. |
| 2 | Alt+Tab | Not protected | Needs Secure. |
| 3 | Alt+F4 | Not protected | Closes KidShell. Watchdog would restart it. |
| 4 | Ctrl+Shift+Esc | Not protected | Task Manager. Needs Secure. |
| 5 | Win+R | Not protected | Run dialog. Needs Secure. |
| 6 | Win+X | Not protected | Needs Secure. |
| 7 | Ctrl+Alt+Del | **Cannot be protected** | The OS owns it. No application can intercept it, and any product claiming otherwise is wrong. |
| 8 | File Open dialog | Not protected | A full file browser. Flagged per app in its profile. |
| 9 | Save As dialog | Not protected | As above. |
| 10 | "Open containing folder" | Not protected | Starts Explorer. Flagged in profiles. |
| 11 | ShellExecute from an app | Not protected | Needs app control. |
| 12 | Custom URI handler | Not protected | Needs app control. |
| 13 | Browser redirect | Not protected | Needs browser policy. |
| 14 | Downloads | Not protected | Browser policy generated but not applied. |
| 15 | USB storage | Not protected | Out of scope for 0.x. |
| 16 | KidShell crash | Mitigated | Crash-loop detection shows a calm screen rather than flickering. |
| 17 | Restart the machine | Not protected | Needs autostart plus a child account. |
| 18 | Sleep / hibernate | **Protected** (screen time) | A long gap is not credited, so sleeping does not consume the allowance — and does not grant extra either. |
| 19 | Change the system clock | Mitigated | Usage comes from a monotonic clock, so winding it back does not refund time. Backward jumps are recorded and shown. |
| 20 | Windows Update reboot | Not protected | Needs autostart. |
| 21 | An app's own updater | Not protected | Profiles record updater processes so app control can decide later. |
| 22 | Launcher child process | Not protected | Profiles record them; the generated policy includes them. |

**Fifteen of twenty-two are "not protected" today.** That is the accurate
picture of a product whose security milestones have not run yet, and it is
better for a parent to read it here than to discover it.

---

## Why UI hiding is not counted

Hiding the taskbar, trapping Alt+Tab and covering the screen stop a child who
is not trying. They do not stop Ctrl+Alt+Del, a reboot, or the process being
killed from another session.

Worse, they *feel* like security — and a parent who believes the machine is
locked supervises less than one who knows it is not. KidShell therefore treats
anything in its own UI as convenience, and counts only OS-level mechanisms as
protection.

---

## Reporting a problem

KidShell is a personal project with no security contact address yet. If you
find something, open an issue describing the behaviour — and please do not
include a child's real name or a machine identifier in it.

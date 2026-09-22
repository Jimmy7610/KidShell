# KidShell privacy

**KidShell is local-first. It has no accounts, no servers, no telemetry, and
makes no network requests of its own.**

This document is deliberately specific. A parental-control product asks a
family to trust it with a child's computer, and "we respect your privacy" is
not a claim anybody can check.

---

## What KidShell stores

All of it in one folder on the machine, readable by the person who owns it:

```
%LOCALAPPDATA%\Packages\<KidShell package>\LocalState\
├─ kidshell.config.json        settings
├─ kidshell.config.json.bak    the previous settings
├─ screentime.json             today's counter
├─ recovery\                   recovery manifests, when any exist
└─ logs\kidshell.log           technical events
```

### The child's profile

Name, age, chosen avatar, chosen theme. The name is what the greeting says and
what a future Windows account would be named after; the age is metadata and
changes nothing about how the product behaves.

### The app list

Which applications a parent allowed, their executable paths, display names and
categories. Paths come from the machine's own Start Menu and registry.

### Settings

Screen-time allowances and hours, web mode, the approved-sites list.

### Screen-time counters

Seconds used today, any bonus a parent granted, the local date the count
belongs to, and a count of times the system clock jumped backwards.

**Duration only.** KidShell records *that* an app was open and for how long. It
does not record what was done inside it.

### The parent PIN

A PBKDF2-SHA256 hash and its salt. **The PIN itself is never written to disk in
any form**, and a test asserts it does not appear in the configuration file.

### Logs

Technical events: app started, configuration saved, launch failed, security
scan run. Local, plain text, and readable.

---

## What KidShell does not collect

Not "collects and protects". Does not collect — there is no code that reads any
of it:

* **Keystrokes.** No keyboard hook, no key logging, anywhere.
* **Screen contents.** No screenshots, no screen recording.
* **Documents.** Not names, not contents, not locations. The session log
  records an app id, and a test asserts a file path passed to an app does not
  reach the log.
* **Browsing.** No history, no page contents, no URLs visited. The allowlist
  records sites a *parent* approved, not sites a child went to.
* **Chats or messages.** Of any kind.
* **Passwords.** KidShell never asks for a Windows password and has no field
  that would accept one.
* **Webcam or microphone.** Never accessed.
* **Location.**
* **Anything identifying the child** beyond the name and age a parent typed.

---

## What leaves the machine

**Nothing.**

* No telemetry, including anonymous or aggregated.
* No crash reporting.
* No analytics.
* No update checks — the updater is disabled and has never contacted a server.
* No licence checks, no accounts, no sign-in.

KidShell works on a machine with no network connection, and behaves identically.

---

## Where the boundary is

Some things are *deliberately* out of scope even though a parental-control
product could technically do them:

**KidShell does not monitor a child.** Screen time is a duration. It is not a
report of what they looked at, typed, or said. A tool that recorded that would
be surveillance, and dressing it as safety would not change what it was.

**KidShell does not report to a parent about content.** There is no "what your
child did today" feed, because building one would require collecting exactly
what is listed above as not collected.

**KidShell does not phone home about security state.** The readiness scan runs
locally and its results stay in the log.

---

## Recovery manifests

When a future milestone changes a Windows setting, it first writes a recovery
manifest describing what is about to change and what the previous value was, so
a human can undo it by hand.

Those files deliberately contain **no PIN, hash, salt or password** — a file
whose whole purpose is to be readable during a crisis is the worst possible
place for a secret. A test asserts none of those words appears in a generated
manifest.

---

## Deleting everything

Close KidShell and delete the `LocalState` folder above. That is all of it;
nothing is stored anywhere else, in any registry key KidShell wrote, or on any
server.

Uninstalling the app removes the folder with it.

---

## Children's data

KidShell is used *by* children, so this is worth stating plainly: the only
personal data about a child in the product is the name, age and avatar a parent
entered during setup. It stays on the machine, is never transmitted, and is
deleted with the app.

---

## If this document and the code disagree

The code wins, and the document is wrong and should be fixed. Several of the
claims here are covered by tests:

| Claim | Test |
| --- | --- |
| The PIN is never stored in plain text | `ProductionPinTests` |
| Recovery manifests contain no secrets | `SecurityTransactionTests` |
| Session logs contain no file paths | `ChildSessionTests` |
| Security scans log no PIN material | `SecurityReadinessTests` |

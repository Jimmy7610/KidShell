# KidShell security

**WINDOWS LOCKDOWN STATUS: NOT ENABLED.**

No KidShell build has ever changed a Windows security setting. This document
says what the product does, what it does not, and which of the things people
assume it does are not true.

For the architecture, see
[`architecture/SECURITY-READINESS.md`](architecture/SECURITY-READINESS.md).
For the next step, see
[`DEDICATED-DEVICE-VALIDATION.md`](DEDICATED-DEVICE-VALIDATION.md).

---

## Code complete is not validated

At 1.0.0-rc.1 the code that locks down Windows exists and is tested. It has
never run. Those two facts are both true and neither replaces the other.

| | |
| --- | --- |
| **Written** | Nine security operations, each with preflight, snapshot, apply, verify and rollback |
| **Tested** | 915 tests, every mutating operation exercised against fakes |
| **Executed** | Never, anywhere |

The structural reason is deliberate. Every operation requires an Apply-mode
`SecurityExecutionContext`, and that type has a private constructor with a
single public factory returning audit mode. No KidShell build can construct
one. Enabling Apply is a visible code change that reflection tests will notice,
not a flag somebody can flip.

So this document describes a product whose security is finished and unproven.
Saying only the first half would be marketing; saying only the second would
undersell what is here.

---

## The honest summary

KidShell today, on a machine where secure setup has not been run, is **a
friendly shell for a supervised child**, not a lock.

A child gets a simple screen with the apps their parent chose, and a screen-time
limit that stops KidShell launching things and shows a calm time-is-up message.
They can also minimise KidShell and use the rest of Windows, because nothing
prevents it.

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

**A parent must never be locked out of their own computer.**

Every design decision bends to it, and several are uncomfortable because of it:

* preflight refuses any plan that would leave no enabled administrator;
* an AppLocker policy with no administrator escape rule is refused before it can
  be applied, because a failed rollback would then end the family's use of the
  machine;
* rollback runs on its own cancellation token, so cancelling a transaction
  cannot also cancel the recovery;
* an operation that cannot roll back may not join a transaction at all;
* the recovery manifest is written and verified on disk *before* the first
  change, and a manifest that cannot be written refuses the transaction.

---

## What is actually in force today

| Control | State |
| --- | --- |
| Parent PIN | **Active.** PBKDF2-SHA256, never stored in plain text. |
| KidShell app list | **Active.** Gates what the child's grid will launch. |
| Screen time | **Active at the application level.** Blocks KidShell launches and shows a time-is-up screen; does not stop the child leaving KidShell. |
| Installed-app discovery | **Active.** Read-only; grants nothing. |
| Web mode | **Stored, with a policy preview.** No browser is controlled. |
| Windows child account | Code complete. Not created. |
| AppLocker | Code complete. Not applied. |
| Assigned Access | Code complete. Unavailable on this edition. |
| Autostart | Code complete. Not configured. |
| Watchdog service | Code complete. Not installed. |
| Secure sign-out | Code complete. Simulated in this build. |

---

## Three things that are not the same

Conflating these is how parental-control products end up lying, so KidShell
keeps them apart in the model, the UI and this document.

**Found on this machine.** Discovery lists what is installed. It grants nothing
and changes nothing.

**Shown in KidShell.** A card in the child's grid. Removing it removes the card.
The program is still on the computer and still startable by other means.

**Permitted by Windows.** An application-control rule. Only this one actually
prevents anything, and only once applied *and* verified.

The same distinction applies to AppLocker itself:

**Can enforce** ≠ **can deploy** ≠ **verified enforcing.**

Since KB 5024351, all Windows 10 2004+ and Windows 11 editions can enforce
AppLocker. That says nothing about whether a policy can be *installed* on a
given machine. A stock Windows Home machine would enforce a policy it has no
supported way to receive, and KidShell reports that as "Delvis" rather than
rounding it to either available or unavailable.

Where no supported deployment channel exists, KidShell **blocks activation and
explains why**. It does not write SrpV2 registry keys by hand. That would work,
and it would be undocumented, unsupported, and exactly the kind of trick that
makes a security product untrustworthy.

---

## Developer builds are not protected

A **Debug** build accepts the published fallback PIN `246810` while no real PIN
is set, exposes a debug shortcut into Parent Mode, and draws a development
watermark so the state is never a surprise. Om KidShell says **UTVECKLING** in
capitals.

A **Release** build refuses that PIN outright, has no shortcut, and cannot
finish first-run setup without a real parent PIN. There is deliberately no
environment variable, configuration key, switch or marker file that turns
developer behaviour back on — every one of those would be reachable by a child
who can open Notepad.

---

## Privilege separation

The KidShell UI runs unelevated and cannot change Windows. Security setup
launches `KidShell.SecurityHost`, a short-lived elevated helper that:

* accepts **typed requests from a closed enum**, not commands — there is no
  member that takes a script, a command line or a path to execute;
* validates every field as if hostile, allow-list shaped: a SID must match the
  SID grammar rather than being sanitised, an AUMID carrying a quote or an
  ampersand is refused because it ends up in a value Windows executes;
* carries **no credential field at all**, so a password cannot travel through
  the channel, reach a log or land in the recovery manifest;
* exits when setup finishes. There is no resident elevated process and no
  listening endpoint.

Two tests assert structurally that the request contract has no field carrying
something to evaluate and no field carrying a secret.

The watchdog service follows the same principle from the other direction: it
takes no input at all. Process presence is a signal a child cannot forge into a
command.

---

## Escape-test matrix

Every way out of KidShell anybody has thought of, with an honest answer for
each.

**This table is generated from `src/KidShell.Core/Security/EscapeMatrix.cs`, and
tests enforce its honesty.** A row may only claim *Skyddad* if somebody has
verified it on hardware; Secure may never be weaker than Standard; nothing may
be safer before Windows is configured than after; every row needs a reason.

**Two rows are marked verified. The rest are claims awaiting
[dedicated-device validation](DEDICATED-DEVICE-VALIDATION.md).**

| # | Väg ut | Idag | Standardläge | Säkert läge | Kommentar |
| --- | --- | --- | --- | --- | --- |
| 1 | Windows-tangenten | Inte skyddad | Inte skyddad | Kräver Säkert läge | Öppnar Start. Bara Windows begränsade läge byter ut skalet. |
| 2 | Alt+Tab | Inte skyddad | Inte skyddad | Kräver Säkert läge | Byter fönster. Kräver begränsat läge för att försvinna. |
| 3 | Alt+F4 | Inte skyddad | Försvårad | Kräver Säkert läge | Stänger KidShell. Vakttjänsten startar om det, men fönstret hinner försvinna. |
| 4 | Ctrl+Skift+Esc (Aktivitetshanteraren) | Inte skyddad | Inte skyddad | Kräver Säkert läge | Begränsat läge döljer Aktivitetshanteraren för standardkonton. |
| 5 | Win+R (Kör) | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Kör-rutan startar program. Appkontroll avgör vad som faktiskt får starta. |
| 6 | Win+X | Inte skyddad | Inte skyddad | Kräver Säkert läge | Snabbmenyn med systemverktyg. Kräver begränsat läge. |
| 7 | Ctrl+Alt+Delete | Kan inte spärras | Kan inte spärras | Kan inte spärras | Windows äger den här tangentkombinationen. Ingen app kan fånga den, och en produkt som påstår sig göra det har fel. |
| 8 | Öppna-dialogen i ett program | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | En filbläddrare inuti ett tillåtet program. Markerad per app i appprofilen. |
| 9 | Spara som-dialogen | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Samma sak som Öppna-dialogen. |
| 10 | "Öppna mappen som innehåller filen" | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Startar Utforskaren. Appkontroll avgör om den får starta. |
| 11 | ShellExecute från ett tillåtet program | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Ett program kan be Windows starta ett annat. Bara appkontroll stoppar det. |
| 12 | Egna protokollhanterare (t.ex. ms-settings:) | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | En länk kan öppna Inställningar. Appprofilerna registrerar vilka protokoll varje program kan använda. |
| 13 | Omdirigering i webbläsaren | Inte skyddad | Kräver Standardläge | Kräver Standardläge | Kräver att webbläsarpolicyn går att installera på datorn. Gäller bara Edge. |
| 14 | Nedladdningar | Inte skyddad | Kräver Standardläge | Kräver Standardläge | Edge-policyn kan begränsa nedladdningar. En nedladdad fil kan ändå inte startas om appkontroll gäller. |
| 15 | USB-minne | Inte skyddad | Kräver Standardläge | Kräver Standardläge | Appkontroll gäller även program på ett USB-minne, men filerna går att läsa. |
| 16 | Genvägar (.lnk) till annat | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | En genväg är bara en pekare. Det som avgör är om målet får starta. |
| 17 | URL-hanterare | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Samma mekanism som protokollhanterare. |
| 18 | Underprocesser från ett startprogram | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Att tillåta ett startprogram säger ingenting om vad det sedan startar. Appprofilerna registrerar underprocesserna. |
| 19 | Ett programs egen uppdaterare | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Uppdaterare startar egna processer. Profilerna registrerar dem så att reglerna kan ta hänsyn till dem. |
| 20 | KidShell kraschar | Försvårad | Försvårad | Måste provas på en dedikerad dator | Kraschslinga upptäcks och visar en lugn skärm i stället för att blinka. Vad barnet ser i begränsat läge måste provas på en riktig dator. |
| 21 | Vakttjänsten kraschar eller stoppas | Gäller inte | Måste provas på en dedikerad dator | Måste provas på en dedikerad dator | Tjänsten körs som LocalSystem och kan inte stoppas av ett standardkonto, men beteendet måste provas. |
| 22 | Starta om datorn | Inte skyddad | Måste provas på en dedikerad dator | Måste provas på en dedikerad dator | Kräver att autostart och barnkontot fungerar efter omstart. Måste provas. |
| 23 | Viloläge och återupptagning | **Skyddad** | **Skyddad** | **Skyddad** | Skärmtiden räknar inte sovtid. Att sova förbrukar varken dagens tid eller ger extra. |
| 24 | Ställa tillbaka klockan | Försvårad | Försvårad | Försvårad | Tiden räknas från en klocka som inte går att ställa, så ingen tid återbetalas. Bakåthopp registreras och visas, men KidShell gör ingen kapprustning av det. |
| 25 | Windows Update startar om datorn | Inte skyddad | Måste provas på en dedikerad dator | Måste provas på en dedikerad dator | Samma som omstart, men vid en tidpunkt ingen valt. Måste provas. |
| 26 | Avinstallera KidShell | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Ett standardkonto kan inte avinstallera ett program som installerats för alla användare. |
| 27 | Redigera KidShells inställningsfil | Inte skyddad | Kräver Standardläge | Kräver Säkert läge | Filen ligger i barnets egen profil. PIN-koden är hashad, men inställningarna går att ändra utan appkontroll. |
| 28 | Starta i felsäkert läge | Inte skyddad | Inte skyddad | Måste provas på en dedikerad dator | Felsäkert läge startar inte tredjepartstjänster. Vad som gäller där måste provas på en riktig dator. |
| 29 | Starta från USB eller återställningsmedia | Kan inte spärras | Kan inte spärras | Kan inte spärras | Den som kan starta ett annat operativsystem äger datorn. Det ligger utanför vad en app kan göra något åt. |

### The numbers

* **Today, on this machine:** 1 of 29 routes is blocked. Windows is not locked.
* **Standard Mode:** 2 routes cannot be blocked by any application, and 3 must
  be tested on real hardware before anything is claimed.
* **Secure Mode:** the same 2, and 5 awaiting device testing.

**Twenty-six of twenty-nine routes are open today.** That is the accurate
picture of a machine where secure setup has not been run, and it is better for a
parent to read it here than to discover it.

---

## Why UI hiding is not counted

Hiding the taskbar, trapping Alt+Tab and covering the screen stop a child who is
not trying. They do not stop Ctrl+Alt+Del, a reboot, or the process being killed
from another session.

Worse, they *feel* like security — and a parent who believes the machine is
locked supervises less than one who knows it is not. KidShell therefore treats
anything in its own UI as presentation, and counts only OS-level mechanisms as
protection.

Full-screen Child Mode is presentation. Assigned Access is protection.

---

## Reporting a problem

KidShell is a personal project with no security contact address yet. If you find
something, open an issue describing the behaviour — and please do not include a
child's real name or a machine identifier in it.

If you find a way to lock a parent out of their own machine, that is the most
serious class of bug this project has, and it will be treated that way.

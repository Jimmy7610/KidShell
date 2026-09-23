# KidShell / Barnläge

[Svenska](#svenska) · [English](#english)

---

# Svenska

KidShell är ett barnvänligt Windows-skal som gör en vanlig Windows-dator enklare att använda för barn och lättare att styra för en förälder. Barnet möts av ett tydligt **Barnläge** med stora appkort, medan föräldern har ett separat **Föräldraläge** för profil, appar, PIN, skärmtid, webb och säkerhet.

> **Status: 1.0.0-rc.1 — CODE COMPLETE, INTE VALIDERAD PÅ RIKTIG HÅRDVARA**
>
> **WINDOWS LOCKDOWN STATUS: NOT ENABLED**
>
> Skillnaden mellan de två raderna är hela poängen.
>
> **Code complete** betyder att koden för att låsa Windows nu finns och är
> testad: barnkonton, AppLocker-distribution, Assigned Access, autostart,
> vakttjänst, webbpolicy, säker utloggning och återställning — var och en som
> en transaktion med preflight, ögonblicksbild, tillämpning, verifiering och
> återställning.
>
> **Not enabled** betyder att ingenting av det har körts. Ingen KidShell-version
> som byggts kan skapa ett Apply-läge: typen har privat konstruktor och en enda
> publik fabrik som returnerar granskningsläge, och reflektionstester bevakar
> det. Den här datorn är oförändrad, och en byte-identisk före/efter-granskning
> av Windows-tillståndet visar det vid varje milstolpe.
>
> Nästa steg är validering på en dedikerad dator. Se
> [`docs/DEDICATED-DEVICE-VALIDATION.md`](docs/DEDICATED-DEVICE-VALIDATION.md).

## Skärmbilder

| Första start | |
| --- | --- |
| ![Välkommen](docs/screenshots/setup-welcome.png) | ![Barnets namn](docs/screenshots/setup-name.png) |
| ![Avatar](docs/screenshots/setup-avatar.png) | ![Tema](docs/screenshots/setup-theme.png) |

| Barnläge | Föräldraläge |
| --- | --- |
| ![Barnläge](docs/screenshots/child-mode.png) | ![Föräldraöversikt](docs/screenshots/parent-overview.png) |
| ![Föräldra-PIN](docs/screenshots/parent-pin.png) | ![Tillåtna appar](docs/screenshots/parent-apps.png) |

Den godkända visuella riktningen finns i
[`docs/design/child-home-reference.png`](docs/design/child-home-reference.png) och
[`docs/design/parent-mode-reference.png`](docs/design/parent-mode-reference.png).

## Vad KidShell kan idag

### Barnläge

- Ett riktigt paketerat WinUI 3-program byggt med .NET 10 och Windows App SDK 2.5.1.
- Första-start-flöde där föräldern väljer barnets namn, avatar, ålder och tema.
- 14 vektoravatarer och 5 teman: **Skogen, Rymden, Havet, Dinosaurier och Färgglatt**.
- Responsivt appgrid med stora barnvänliga kort.
- Klocka, batteri och nätverksstatus.
- Paint och Windows Kalkylator kan startas via KidShells launcher-abstraktion.
- Vänliga felmeddelanden när en app saknas eller inte är korrekt konfigurerad.

### Föräldraläge

Föräldraläget innehåller idag:

- **Översikt**
- **Appar**
- **Skärmtid**
- **Webb**
- **Säkerhet**
- **Profil**

Föräldern kan bland annat:

- slå appar av och på
- ändra barnets profil
- ändra PIN
- konfigurera skärmtidsregler
- hantera webbinställningar
- se Windows- och säkerhetskapabiliteter

### Föräldra-PIN

KidShell skiljer nu tydligt på utvecklingsläge och produktionsläge.

- En riktig föräldra-PIN lagras aldrig i klartext.
- PIN lagras med PBKDF2-baserad hashning.
- Svaga PIN-koder som `000000`, `123456`, `987654` och den publicerade utvecklings-PIN-koden får inte väljas som riktig PIN.
- En Release/production-build accepterar **inte** utvecklings-PIN som fallback.
- En Debug/development-build visar tydligt att utvecklingsläge är aktivt.

Utvecklings-PIN i Debug-build:

```text
246810
```

Den ska endast användas under utveckling.

### Installerade appar

KidShell har read-only appupptäckt för bland annat:

- Start-menyn
- registrerade Win32-program
- installerade paket / MSIX / UWP
- AUMID där det finns
- kända exekverbara sökvägar

Resultaten normaliseras och dubletter slås ihop. Att lägga till en app i KidShells gränssnitt är **inte** samma sak som att ge den framtida Windows-säkerhetsbehörigheter.

### App-profiler

KidShell har en modell för att beskriva hur appar beter sig, bland annat:

- huvudprocess
- child processes
- launcher
- updater-processer
- protokoll och URL:er
- filplatser
- beroenden
- kända säkerhetsrisker

Profiler finns eller är förberedda för bland annat Calculator, Paint, Minecraft, VLC, Scratch och webbläsare.

## Skärmtid

Skärmtidsmotorn är implementerad på applikationsnivå.

Den kan hantera:

- vardags- och helggränser
- tillåtna tider på dygnet
- använd tid och återstående tid
- varningar vid 15, 5 och 1 minut
- tillfälliga förlängningar
- omstart av KidShell
- sleep/resume
- lokala dagsgränser och DST
- passiv upptäckt av misstänkt bakåtflyttad systemklocka

Viktigt: eftersom Windows-lockdown ännu inte är aktiverad kan KidShell begränsa vad som händer **inne i KidShell**, men kan ännu inte garantera att ett barn inte lämnar appen och använder resten av Windows.

## Webb

KidShell har en web policy-modell och kan generera framtida Edge-policy som **dry-run/artifact**.

Planerade lägen:

- ingen webbläsare
- endast godkända webbplatser
- friare webb

URL:er normaliseras och riskabla protokoll som `file:`, `javascript:` och `shell:` avvisas.

Ingen webbläsarpolicy appliceras på Windows i nuvarande build.

## Säkerhetsarkitektur

KidShell bygger säkerhetsdelen enligt principen:

```text
Prepare
  ↓
Preflight
  ↓
Snapshot
  ↓
Spara recovery-manifest
  ↓
Apply
  ↓
Verify
  ↓
Commit
```

Om något går fel efter att en ändring har börjat appliceras ska transaktionen försöka göra rollback i omvänd ordning.

Cancellation-säkerheten är också byggd så att:

- avbrott före första Apply kan avbryta utan mutation
- avbrott efter en Apply måste gå via rollback
- rollback använder en separat tidsbudget och stoppas inte bara för att den ursprungliga operationen blev cancelled

I nuvarande produktkod finns fortfarande ingen användbar väg till riktig `Apply`.

`SecurityFeatureCompiledIn` är fortfarande `false`.

## Recovery

KidShell har recovery-manifest som är tänkta att sparas **innan** framtida Windows-förändringar görs.

Recovery-informationen kan innehålla:

- transaction ID
- tidpunkt
- Windows-kapabiliteter
- planerade operationer
- tidigare värden
- rollback-information
- slutstatus

PIN, lösenord, salt och andra hemligheter ska inte skrivas till recovery-manifestet.

## Barnkonto och Windows-säkerhet

Arkitekturen kan idag upptäcka och planera för ett framtida barnkonto, men den skapar eller ändrar inget konto.

Målet är:

```text
Föräldrakonto
└─ Administrator

Barnkonto
└─ Standard User
```

Barnkontot ska aldrig behöva administratörsrättigheter.

## AppLocker och Assigned Access

KidShell skiljer på:

- om Windows kan **enforce** AppLocker
- om den aktuella datorn har en dokumenterad kanal för att **deploya** policyn

Det är inte samma sak.

På en vanlig Windows Home-installation kan enforcement-motorn finnas samtidigt som PowerShell-modul, policykonsol, CSP eller annan lämplig deployment-kanal saknas.

KidShell använder därför inte en förenklad `SupportsAppLocker = true/false`.

AppLocker-policy kan genereras som validerad XML-artifact, men appliceras inte.

Assigned Access är inte tillgängligt på Windows Home och är därför avsett för framtida **Secure Mode** på kompatibla Windows-utgåvor.

## Standard Mode och Secure Mode

### Standardläge — kodfärdigt, inte validerat

Fungerar så långt Windows-utgåvan tillåter, inklusive Home:

- separat standardkonto för barnet
- KidShell autostart
- tillåten appmodell
- skärmtid
- webbkontroll
- watchdog
- recovery
- dokumenterad appkontroll där deployment faktiskt stöds

### Säkert läge — kodfärdigt, inte validerat

På Windows Pro, Enterprise, Education och IoT Enterprise:

- allt i Standardläge
- Assigned Access som *restricted user experience* — den flerapps-variant
  Microsoft dokumenterar, inte enapps-kiosk, som skulle göra varje app
  föräldern godkänt oåtkomlig
- starkare isolering på OS-nivå

Windows 11 Home har inte Assigned Access, och KidShell säger det rakt ut i
stället för att erbjuda en knapp som skulle misslyckas.

KidShell märker aldrig en dator som **Skyddad** bara för att funktionerna finns.
Verkligt skydd kräver att reglerna tillämpats **och** att KidShell läst tillbaka
dem och bekräftat att de gäller.

## Watchdog och sessioner

`KidShell.Watchdog` är en riktig Windows-tjänst. Den gör en enda sak: startar
om KidShell om KidShell slutar köra.

- ingen konfigurationsfil, ingen pipe, ingen socket, inga kommandon — en tjänst
  som körs som LocalSystem och läser instruktioner från något ett barnkonto kan
  skriva till är en rättighetseskalering med ett vänligt namn
- startar skalet med `CreateProcessAsUser` på sessionens token, så KidShell körs
  som barnet och aldrig som LocalSystem
- kraschslinga upptäcks; efter några snabba omstarter slutar den försöka och
  låter den lugna skärmen stå kvar
- faller **aldrig** tillbaka på att visa skrivbordet — då vore det enklaste
  sättet ut ur KidShell att krascha det

Tjänsten är byggd och testad men **inte installerad** på den här datorn.
Installationen sker genom säkerhetstransaktionen på en dedikerad dator.

## Uppdateringar

Arkitektur finns för framtida säkra uppdateringar:

```text
Check
→ Download
→ Verify
→ Stage
→ Install
→ Verify
→ Rollback vid fel
```

Modellen kräver bland annat HTTPS, signaturkontroll och SHA-256-verifiering.

Automatiska produktionsuppdateringar är fortfarande avstängda eftersom KidShell ännu inte har en riktig kodsigneringskedja.

## Vad KidShell inte gör på den här datorn

Skillnaden mot tidigare versioner: koden finns nu. Den har bara inte körts.

**Byggt och testat, men aldrig kört någonstans:**

| | |
| --- | --- |
| Skapa Windows-barnkonto | `CreateChildAccountOperation` |
| Ta bort administratörsrollen | `DemoteChildAccountOperation` |
| AppLocker-distribution | `AppLockerDeploymentOperation` |
| Tjänsten Programidentitet | `ApplicationIdentityServiceOperation` |
| Assigned Access | `AssignedAccessOperation` |
| Autostart för barnkontot | `ChildAutostartOperation` |
| Webbläsarpolicy för Edge | `BrowserPolicyOperation` |
| Vakttjänst | `WatchdogServiceOperation` |
| Säker utloggning | `ChildSessionLogoutOperation` |

**Inte byggt, och medvetet inte:**

- byte av Windows-skal (shell replacement) — Assigned Access är det
  dokumenterade sättet
- ändringar i grupprincip eller UAC
- automatisk inloggning
- odokumenterade registerknep för att tvinga fram AppLocker på Home

**Finns inte ännu av skäl utanför koden:**

- kodsigneringscertifikat, och därmed automatiska uppdateringar
- distributionsinfrastruktur

Explorer, Aktivitetshanteraren och vanliga Windows-genvägar är alltså
fortfarande tillgängliga. Hela listan finns i
[`docs/SECURITY.md`](docs/SECURITY.md).

## Windows-krav

| | |
| --- | --- |
| OS | Windows 10 1809 (10.0.17763) eller senare; utvecklas främst på Windows 11 |
| SDK | .NET SDK 10.0.300 eller senare |
| Runtime | Windows App Runtime 2.5.1 |
| Lokal Debug-körning | Windows Developer Mode för registrering av osignerat lokalt paket |

Packaged app discovery använder nyare Windows-API:er där de finns och guardas på äldre Windows-builds.

## Bygga

Öppna **PowerShell** i repots rotmapp och kör:

```powershell
cd C:\Projects\KidShell
dotnet build KidShell.sln -p:Platform=x64 -c Debug
```

För Release:

```powershell
dotnet build KidShell.sln -p:Platform=x64 -c Release
```

## Köra

Från repots rot:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1
```

När paketet redan är registrerat kan appen startas med:

```powershell
start shell:AppsFolder\KidShell.Barnlage.Dev_b19zrs1eesfdc!App
```

## Tester

Kör:

```powershell
dotnet test KidShell.sln -c Release
```

Nuvarande verifierade nivå:

**885 automatiska tester passerar** i två projekt — `KidShell.Core.Tests`
och `KidShell.WindowsIntegration.Tests`.

Inget test ändrar den här datorn. Varje Windows-operation körs mot en falsk
plattform: en kontokatalog i minnet, ett register som är en ordbok, en
tjänstehanterare som är en lista.

GitHub Actions kör dessutom restore, build, båda testsviterna, självtesterna för
hjälparen och vakttjänsten, en scan efter maskinmuterande anrop, en kontroll att
P/Invoke stannar i plattformslagret, en versionskontroll och en länkkontroll av
dokumentationen.

## Arkitektur

```text
KidShell.App
WinUI 3 / Windows-specifikt UI
        │
        ▼
KidShell.Core
konfiguration, appar, PIN, screen time,
sessioner, webb, watchdog, säkerhetsplanering
        ▲
        │
KidShell.Core.Tests
xUnit / fake operations / ingen riktig Windows-mutation
```

Viktiga principer:

- `KidShell.Core` innehåller så mycket testbar logik som möjligt.
- UI får inte sprida direkta Windows-mutationer.
- framtida systemändringar måste gå genom den transaktionella säkerhetsgränsen.
- capability är inte samma sak som enforcement.
- recovery går före lockdown.
- lokal-first: ingen telemetry och inget molnkonto krävs.

## Data

| Data | Plats |
| --- | --- |
| Konfiguration | `%LOCALAPPDATA%\Packages\KidShell.Barnlage.Dev_…\LocalState\kidshell.config.json` |
| Backup | samma plats med `.bak` |
| Logg | `…\LocalState\logs\kidshell.log` |
| Recovery | lokal appdata enligt recovery-arkitekturen |

KidShell samlar inte in:

- tangenttryckningar
- chattar
- lösenord
- dokumentinnehåll
- webbsidors innehåll
- skärmbilder för övervakning

Se [`docs/PRIVACY.md`](docs/PRIVACY.md).

## Roadmap

| Version | Omfattning | Status |
| --- | --- | --- |
| 0.1 | Application shell | ✅ Klar |
| 0.1.1 | First-run onboarding | ✅ Klar |
| 0.1.5 | Security readiness / dry-run | ✅ Klar |
| 0.2 | Produkt-UX, production PIN, app discovery | ✅ Klar |
| 0.3 | Transaktionell Windows-integration | ✅ Klar |
| 0.4 | Barnkonto + appkontroll | ✅ Kodfärdig |
| 0.5 | Skärmtid, webb, watchdog | ✅ Klar |
| 0.6 | Installer, updater, deployment | ✅ Kodfärdig |
| 0.7 | Härdning och escape-matris | ✅ Klar |
| **1.0.0-rc.1** | **Release candidate** | **✅ Du är här** |
| 1.0 | Produktionsrelease | Blockerad: kräver dedikerad testdator, Pro-hårdvara och ett riktigt signeringscertifikat |

Den detaljerade och auktoritativa roadmapen finns i
**[`docs/ROADMAP.md`](docs/ROADMAP.md)**.

## Dokumentation

| Dokument | Innehåll |
| --- | --- |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Versioner, principer och scope |
| [`docs/SECURITY.md`](docs/SECURITY.md) | Threat model och security status |
| [`docs/PRIVACY.md`](docs/PRIVACY.md) | Vad som sparas och inte samlas in |
| [`docs/RECOVERY.md`](docs/RECOVERY.md) | Recovery och rollback |
| [`docs/TESTING.md`](docs/TESTING.md) | Teststrategi |
| [`docs/DEDICATED-DEVICE-VALIDATION.md`](docs/DEDICATED-DEVICE-VALIDATION.md) | Validering på dedikerad dator — nästa steg |
| [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) | Packaging, signing och deployment |
| [`docs/architecture/`](docs/architecture/) | Arkitekturanteckningar per milestone |

## Licens

Copyright © 2026 Jimmy Eliasson. All rights reserved.

KidShell är proprietär programvara. Ingen rätt ges att kopiera, modifiera, distribuera, sublicensiera, sälja, publicera eller skapa derivat av programvaran utan skriftligt tillstånd från upphovsrättsinnehavaren.

Se [LICENSE](LICENSE).

---

# English

KidShell is a child-friendly Windows shell designed to make an ordinary Windows PC simpler for a child to use and easier for a parent to manage. The child sees a clear **Child Mode** with large app cards, while the parent gets a separate **Parent Mode** for profile, apps, PIN, screen time, web and security.

> **Status: 1.0.0-rc.1 — CODE COMPLETE, NOT VALIDATED ON REAL HARDWARE**
>
> **WINDOWS LOCKDOWN STATUS: NOT ENABLED**
>
> KidShell has still **not** enabled real Windows lockdown. No Windows accounts have been created, no AppLocker policy has been applied, Assigned Access is not enabled, and no services or system policies have been installed. Current security functionality consists of detection, planning, policy generation, simulation and recovery architecture.

## Screenshots

| First run | |
| --- | --- |
| ![Welcome](docs/screenshots/setup-welcome.png) | ![Child name](docs/screenshots/setup-name.png) |
| ![Avatar](docs/screenshots/setup-avatar.png) | ![Theme](docs/screenshots/setup-theme.png) |

| Child Mode | Parent Mode |
| --- | --- |
| ![Child Mode](docs/screenshots/child-mode.png) | ![Parent overview](docs/screenshots/parent-overview.png) |
| ![Parent PIN](docs/screenshots/parent-pin.png) | ![Allowed apps](docs/screenshots/parent-apps.png) |

The approved visual direction lives in
[`docs/design/child-home-reference.png`](docs/design/child-home-reference.png) and
[`docs/design/parent-mode-reference.png`](docs/design/parent-mode-reference.png).

## What KidShell can do today

### Child Mode

- A real packaged WinUI 3 application built on .NET 10 and Windows App SDK 2.5.1.
- First-run setup for child name, avatar, age and theme.
- 14 vector avatars and 5 themes: **Forest, Space, Ocean, Dinosaurs and Bright**.
- Responsive app grid with large child-friendly cards.
- Clock, battery and network status.
- Paint and Windows Calculator can launch through KidShell's launcher abstraction.
- Friendly errors when an app is missing or misconfigured.

### Parent Mode

Parent Mode currently contains:

- **Overview**
- **Apps**
- **Screen time**
- **Web**
- **Security**
- **Profile**

A parent can, among other things:

- enable or disable apps
- edit the child profile
- change the parent PIN
- configure screen-time rules
- manage web settings
- inspect Windows and security capabilities

### Parent PIN

KidShell now has a strict separation between development and production behavior.

- A real parent PIN is never stored in plaintext.
- PIN storage uses PBKDF2-based hashing.
- Weak PINs such as `000000`, `123456`, `987654`, and the published development PIN cannot be selected as a real PIN.
- A Release/production build does **not** accept the development PIN as a fallback.
- A Debug/development build clearly indicates that development mode is active.

Development PIN in Debug builds:

```text
246810
```

This is for development only.

### Installed application discovery

KidShell has read-only discovery for:

- Start Menu entries
- registered Win32 applications
- installed packages / MSIX / UWP
- AUMID where available
- known executable paths

Results are normalized and duplicates are merged. Adding an app to KidShell's visible app list is **not** the same thing as granting future Windows security permission.

### Application profiles

KidShell has a model for describing how applications behave, including:

- main executable
- child processes
- launcher
- updater processes
- protocols and URLs
- file locations
- dependencies
- known security considerations

Profiles exist or are prepared for apps such as Calculator, Paint, Minecraft, VLC, Scratch and browsers.

## Screen time

The screen-time engine is implemented at application level.

It supports:

- weekday and weekend allowances
- allowed hours
- used and remaining time
- warnings at 15, 5 and 1 minute
- temporary parent extensions
- KidShell restarts
- sleep/resume
- local-day boundaries and DST
- passive detection of suspicious backward clock movement

Important: because Windows lockdown is not yet enabled, KidShell can control what happens **inside KidShell**, but cannot yet guarantee that a child cannot leave the application and use the rest of Windows.

## Web

KidShell has a web-policy model and can generate future Edge policy as a **dry-run/artifact**.

Planned modes:

- no browser
- approved sites only
- broader web access

URLs are normalized and risky schemes such as `file:`, `javascript:` and `shell:` are rejected.

No browser policy is applied to Windows in the current build.

## Security architecture

KidShell's security foundation follows:

```text
Prepare
  ↓
Preflight
  ↓
Snapshot
  ↓
Persist recovery manifest
  ↓
Apply
  ↓
Verify
  ↓
Commit
```

If something fails after a change starts being applied, the transaction attempts rollback in reverse order.

Cancellation handling is also designed so that:

- cancellation before the first Apply can stop without mutation
- cancellation after an Apply must pass through rollback
- rollback uses its own time budget and is not simply cancelled because the original operation was cancelled

There is still no usable route to real `Apply` in current product code.

`SecurityFeatureCompiledIn` remains `false`.

## Recovery

KidShell has recovery manifests designed to be persisted **before** future Windows changes are made.

Recovery data can include:

- transaction ID
- timestamp
- Windows capability summary
- planned operations
- previous values
- rollback information
- final state

PINs, passwords, salts and other secrets must not be written to recovery manifests.

## Child account and Windows security

The architecture can currently discover and plan for a future child account, but does not create or modify one.

Target model:

```text
Parent account
└─ Administrator

Child account
└─ Standard User
```

The child account should never need administrator rights.

## AppLocker and Assigned Access

KidShell distinguishes between:

- whether Windows can **enforce** AppLocker
- whether the current machine has a documented way to **deploy** the policy

Those are not the same thing.

On a typical Windows Home machine, the enforcement engine may exist while the PowerShell module, policy console, CSP or another suitable deployment channel is absent.

KidShell therefore does not use a simplified `SupportsAppLocker = true/false` model.

AppLocker policy can be generated as validated XML artifacts, but is not applied.

Assigned Access is not available on Windows Home and is intended for future **Secure Mode** on compatible Windows editions.

## Standard Mode and Secure Mode

### Standard Mode — code complete, not validated

Works as far as the Windows edition allows, including Home:

- dedicated standard child account
- KidShell autostart
- allowed-app model
- screen time
- web controls
- watchdog
- recovery
- documented application control where deployment is actually supported

### Secure Mode — code complete, not validated

On Windows Pro, Enterprise, Education and IoT Enterprise:

- everything in Standard Mode
- Assigned Access as a *restricted user experience* — the multi-app shape
  Microsoft documents, not single-app kiosk, which would make every app the
  parent approved unreachable
- stronger OS-level containment

Windows 11 Home does not have Assigned Access, and KidShell says so plainly
rather than offering a button that would fail.

KidShell never labels a machine **Protected** merely because a capability
exists. Real protection requires the rules to have been applied **and** for
KidShell to have read them back and confirmed they are in force.

## Watchdog and sessions

`KidShell.Watchdog` is a real Windows service. It does one thing: restart
KidShell if KidShell stops running.

- no configuration file, no pipe, no socket, no commands — a LocalSystem
  service that reads instructions from anywhere a child account can write is a
  privilege escalation with a friendly name
- starts the shell with `CreateProcessAsUser` on the session token, so KidShell
  runs as the child and never as LocalSystem
- crash-loop detection; after a few rapid restarts it stops trying and leaves
  the calm failure screen in place
- **never** falls back to showing the desktop, which would make crashing
  KidShell the easiest way out of it

The service is built and tested but **not installed** on this machine.
Installation happens through the security transaction on a dedicated device.

## Updates

Architecture exists for future secure updates:

```text
Check
→ Download
→ Verify
→ Stage
→ Install
→ Verify
→ Rollback on failure
```

The model requires HTTPS, signature verification and SHA-256 verification.

Automatic production updates remain disabled because KidShell does not yet have a production code-signing chain.

## What KidShell does not do on this machine

What changed from earlier versions: the code now exists. It simply has not
been run.

**Built and tested, never executed anywhere:**

| | |
| --- | --- |
| Create the Windows child account | `CreateChildAccountOperation` |
| Remove the administrator role | `DemoteChildAccountOperation` |
| AppLocker deployment | `AppLockerDeploymentOperation` |
| Application Identity service | `ApplicationIdentityServiceOperation` |
| Assigned Access | `AssignedAccessOperation` |
| Child-account autostart | `ChildAutostartOperation` |
| Microsoft Edge policy | `BrowserPolicyOperation` |
| Watchdog service | `WatchdogServiceOperation` |
| Secure sign-out | `ChildSessionLogoutOperation` |

**Not built, deliberately:**

- shell replacement — Assigned Access is the documented mechanism
- Group Policy or UAC changes
- automatic logon
- undocumented registry tricks to force AppLocker onto Home

**Absent for reasons outside the code:**

- a code-signing certificate, and therefore automatic updates
- distribution infrastructure

Explorer, Task Manager and normal Windows shortcuts are therefore still
available. The full picture is in [`docs/SECURITY.md`](docs/SECURITY.md).

## Windows requirements

| | |
| --- | --- |
| OS | Windows 10 1809 (10.0.17763) or later; primarily developed on Windows 11 |
| SDK | .NET SDK 10.0.300 or later |
| Runtime | Windows App Runtime 2.5.1 |
| Local Debug run | Windows Developer Mode for registration of the unsigned local package |

Packaged-app discovery uses newer Windows APIs where available and is guarded on older Windows builds.

## Build

Open **PowerShell** in the repository root and run:

```powershell
cd C:\Projects\KidShell
dotnet build KidShell.sln -p:Platform=x64 -c Debug
```

For Release:

```powershell
dotnet build KidShell.sln -p:Platform=x64 -c Release
```

## Run

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-kidshell.ps1
```

When the package is already registered:

```powershell
start shell:AppsFolder\KidShell.Barnlage.Dev_b19zrs1eesfdc!App
```

## Tests

Run:

```powershell
dotnet test KidShell.sln -c Release
```

Current verified level:

**885 automated tests passing** across two projects — `KidShell.Core.Tests`
and `KidShell.WindowsIntegration.Tests`.

No test changes this machine. Every Windows operation runs against a fake
platform: an in-memory account directory, a registry that is a dictionary, a
service control manager that is a list.

GitHub Actions also performs restore, build, both test suites, the helper and
watchdog self-tests, a source scan for machine-changing calls, a check that
P/Invoke stays inside the platform layer, a version-coherence check and a
documentation link check.

## Architecture

```text
KidShell.App
WinUI 3 / Windows-specific UI
        │
        ▼
KidShell.Core
configuration, apps, PIN, screen time,
sessions, web, watchdog, security planning
        ▲
        │
KidShell.Core.Tests
xUnit / fake operations / no real Windows mutation
```

Core principles:

- `KidShell.Core` holds as much testable logic as possible.
- UI code must not scatter direct Windows mutation calls.
- future system changes must pass through the transactional security boundary.
- capability is not the same as enforcement.
- recovery comes before lockdown.
- local-first: no telemetry and no cloud account required.

## Data

| Data | Location |
| --- | --- |
| Configuration | `%LOCALAPPDATA%\Packages\KidShell.Barnlage.Dev_…\LocalState\kidshell.config.json` |
| Backup | same path with `.bak` |
| Log | `…\LocalState\logs\kidshell.log` |
| Recovery | local app data according to the recovery architecture |

KidShell does not collect:

- keystrokes
- chats
- passwords
- document contents
- browsing page contents
- monitoring screenshots

See [`docs/PRIVACY.md`](docs/PRIVACY.md).

## Roadmap

| Version | Scope | Status |
| --- | --- | --- |
| 0.1 | Application shell | ✅ Done |
| 0.1.1 | First-run onboarding | ✅ Done |
| 0.1.5 | Security readiness / dry-run | ✅ Done |
| **0.2** | **Product UX, production PIN, app discovery** | **In progress** |
| 0.3 | Transactional Windows integration | Prepared in Core |
| 0.4 | Child account + app control | Prepared / dry-run |
| 0.5 | Screen time, web, watchdog | Partially implemented |
| 0.6 | Installer, updater, deployment | Planned |
| 0.7 | Hardening and escape testing | Planned |
| 0.8 | Release candidate | Requires a dedicated test device |
| 1.0 | Production release | Blocked until real device validation is complete |

The detailed and authoritative roadmap lives in
**[`docs/ROADMAP.md`](docs/ROADMAP.md)**.

## Documentation

| Document | Contents |
| --- | --- |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Versions, principles and scope |
| [`docs/SECURITY.md`](docs/SECURITY.md) | Threat model and security status |
| [`docs/PRIVACY.md`](docs/PRIVACY.md) | What is stored and what is never collected |
| [`docs/RECOVERY.md`](docs/RECOVERY.md) | Recovery and rollback |
| [`docs/TESTING.md`](docs/TESTING.md) | Test strategy |
| [`docs/DEDICATED-DEVICE-VALIDATION.md`](docs/DEDICATED-DEVICE-VALIDATION.md) | Dedicated-device validation — the next step |
| [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) | Packaging, signing and deployment |
| [`docs/architecture/`](docs/architecture/) | Architecture notes by milestone |

## Licence

Copyright © 2026 Jimmy Eliasson. All rights reserved.

KidShell is proprietary software. No permission is granted to copy, modify, distribute, sublicense, sell, publish, or create derivative works from the software without prior written permission from the copyright holder.

See [LICENSE](LICENSE).

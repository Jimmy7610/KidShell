# KidShell MVP 0.1 — architecture

This document describes what was built for MVP 0.1, why it is shaped this way,
and where the later Windows-security work will attach.

**Status: MVP 0.1 applies no Windows lockdown of any kind.** Every design note
below assumes that, and several of the seams exist specifically so that a later
milestone can add lockdown without rewriting the app.

---

## 1. Projects

```text
KidShell.sln
├─ src/KidShell.Core          net10.0            no UI, no Windows TFM
├─ src/KidShell.App           net10.0-windows…   WinUI 3, packaged (MSIX)
└─ tests/KidShell.Core.Tests  net10.0            xunit
```

### KidShell.Core

Targets plain `net10.0` on purpose. It has **no** WinUI reference, no package
identity requirement and no dependency on a window existing. That constraint is
what makes the configuration model, the launcher decision logic and the PIN
service testable in a plain unit-test process.

Namespaces:

| Namespace | Contents |
| --- | --- |
| `KidShell.Core.Configuration` | The configuration document, its defaults, the JSON store, `IAppStateService` |
| `KidShell.Core.Launching` | `IAppLauncher`, `LaunchResult`, the executable resolver, `ProcessRunner` |
| `KidShell.Core.Security` | `IParentPinService`, PBKDF2 hashing, the development PIN constant |
| `KidShell.Core.Diagnostics` | `IKidShellLogger` and a rolling file logger |
| `KidShell.Core.Mvvm` | A two-type MVVM base: `ObservableObject` and `RelayCommand` |

`KidShell.Core.Mvvm` is hand-rolled rather than pulled from a framework. The
surface actually used is small enough that a dependency would cost more than it
saves.

### KidShell.App

A packaged WinUI 3 desktop app. `Microsoft.Extensions.DependencyInjection` wires
the service graph once in `App.xaml.cs`; DI is used because there genuinely is a
graph (configuration → state → PIN → launcher → dialogs), not as decoration.

```text
App.xaml(.cs)           composition root
MainWindow.xaml(.cs)    the single window: scene + mode + PIN overlay
Themes/                 the design system (see §6)
Views/                  Child Mode, Parent Mode, the six parent pages, dialogs
ViewModels/             one view model per screen, plus row/tile view models
Controls/               HoldButton, AppIconPresenter, AvatarPresenter
Services/               dialogs, file picker, system status, parent flows, paths
Localization/           Strings table + the {loc:Str} markup extension
Converters/             the four value converters the views need
```

---

## 2. Configuration system

### The document

One model, `KidShellConfiguration`, is the whole persisted state:

```jsonc
{
  "schemaVersion": 1,
  "child":      { "name", "age", "avatarId", "themeId" },
  "apps":       [ { "id", "displayName", "programName", "description",
                    "category", "icon", "accentStyle", "isEnabled",
                    "executablePath", "arguments", "sortOrder" } ],
  "screenTime": { "isEnabled", "weekdayMinutes", "weekendMinutes" },
  "web":        { "mode", "allowedDomains" },
  "parentPin":  { "hash", "salt", "iterations" }
}
```

`schemaVersion` is written as `1` and is the hook for future migrations.
Derived members (`EnabledApps`, `EffectiveProgramName`, `IsConfigured`) carry
`[JsonIgnore]` so the file stays a description of intent rather than a dump of
computed state.

`displayName` is what the child sees on the card ("Rita"); `programName` is what
the parent sees in the app list ("Paint"). The two audiences genuinely want
different words for the same entry.

### Storage location

`AppPaths` resolves `ApplicationData.Current.LocalFolder` for the packaged app,
falling back to `%LOCALAPPDATA%\KidShell` when there is no package identity.
Nothing is ever written next to the executable or into Program Files.

### Save behaviour

`JsonConfigurationStore.Save` is deliberately defensive:

1. serialize to a string first — a serialization failure then cannot truncate a
   good file;
2. deserialize that string back as validation;
3. write `kidshell.config.json.tmp`;
4. `File.Replace` the live file, which both swaps atomically-ish and leaves the
   previous good document as `kidshell.config.json.bak`;
5. on `IOException` / `UnauthorizedAccessException`, delete the temp file, log,
   and return `false` so the UI can say the save failed.

### Load behaviour

| Situation | Result |
| --- | --- |
| No file | `CreatedDefaults` — defaults are written out |
| Valid file | `Loaded` |
| Structurally valid but incomplete | `Loaded`, after `Normalize` fills gaps and clamps values |
| Unreadable | `RecoveredFromCorruption` — the bad file is copied to `*.corrupt-<timestamp>`, defaults are used, and the app shows the parent a one-time notice |

KidShell never throws its way out of a bad configuration file.

### Live state and drafts

`IAppStateService` owns the single live document.

```text
Child Mode  ──reads──►  state.Current
Parent Mode ──edits──►  state.CreateDraft()  (a deep clone)
                            │
                     Spara ändringar
                            ▼
                     state.Commit(draft)  ──► persist ──► Current ──► ConfigurationChanged
```

Because Parent Mode works on a clone, a half-finished edit can never reach the
child's screen. `ConfigurationChanged` is what makes `ChildHomeViewModel`
rebuild its tiles, which is why toggling an app in Parent Mode is visible in
Child Mode the moment it is saved.

"Are there unsaved changes?" is answered by `ConfigurationSnapshot.AreEquivalent`,
which compares the serialized draft with the serialized live document. Editing a
value and editing it back is therefore correctly *not* a change — a class of bug
that per-page dirty flags tend to get wrong.

---

## 3. Launcher abstraction

```text
ChildHomeViewModel
      │ Launch(KidAppDefinition)
      ▼
IAppLauncher ── AppLauncher
      ├─ IExecutableResolver ── WindowsExecutableResolver
      └─ IProcessRunner       ── ProcessRunner  (the only Process.Start in the app)
```

`AppLauncher` never throws. Every path produces a `LaunchResult`:

| Status | Meaning | What the child sees |
| --- | --- | --- |
| `Success` | Started | "*X* öppnas …" (no dialog) |
| `NotConfigured` | No program pointed at this card yet | "*X* är inte konfigurerat ännu." + *Tillbaka* |
| `NotFound` | Configured, but not on this machine | "*X* finns inte på den här datorn ännu." |
| `Failed` | Exists but would not start | "*X* kunde inte startas just nu." |
| `Blocked` | Switched off in Parent Mode | "*X* är avstängd just nu." |

`LaunchResult` carries two separate strings: `ChildMessage` (friendly, shown)
and `TechnicalDetail` (paths, exception types — logged only, never displayed).

`WindowsExecutableResolver` decides *whether* something can start without
starting it:

* empty → `Empty`
* `calculator:`, `ms-paint:`, `https://…` → `ShellTarget` (shell activation)
* rooted path → `File` or `NotFound`
* bare name → probed against System32, the Windows directory and `PATH`,
  trying `.exe/.com/.bat/.cmd`

File existence is injected as a delegate, so the resolution rules are unit
tested without depending on what happens to be installed on the build machine.

---

## 4. Parent PIN

`IParentPinService` is the gate. The view model hands it whatever was typed and
gets back `Correct` / `Incorrect` / `Malformed` — it never learns the expected
PIN, and `Incorrect` and `Malformed` produce the same message on screen.

`ParentPinService` stores a PBKDF2-SHA256 hash (210 000 iterations, 16-byte
salt) inside the configuration document. Until a parent sets a PIN, and only
while `DeveloperOptions.DeveloperMode` is true, the compiled-in development PIN
in `DevelopmentPin.cs` is accepted — and the PIN screen and the Säkerhet page
both say so.

The interface exists so that the later milestone can back it with Windows
Credential Manager or Windows Hello without touching a view.

---

## 5. ViewModel structure and navigation

```text
ShellViewModel                     which face is showing; owns the PIN gate
├─ ChildHomeViewModel              tiles, greeting, avatar, launch command
│   └─ ChildAppTileViewModel       one card
├─ PinOverlayViewModel             entry state, never the expected PIN
└─ ParentShellViewModel            the draft, page selection, save/back/exit
    ├─ ParentOverviewViewModel
    ├─ ParentAppsViewModel  └─ ParentAppRowViewModel
    ├─ ParentScreenTimeViewModel
    ├─ ParentWebViewModel
    ├─ ParentSecurityViewModel └─ SecurityStatusViewModel
    └─ ParentProfileViewModel  └─ AvatarChoiceViewModel
```

### Mode switching

`MainWindow` is a three-layer `Grid`: the illustrated `SceneBackground`, the
active mode (`ChildHomeView` or `ParentShellView`), and `PinOverlayView` on top.
Switching modes is a visibility change rather than a frame navigation, so Child
Mode is never rebuilt and returning to it is instant. While the PIN overlay is
up, both modes have `IsHitTestVisible = false`, so nothing behind it is
reachable by pointer or keyboard.

### Parent navigation

The sidebar is six `RadioButton`s sharing a group, styled by
`NavigationItemStyle`. RadioButton semantics give single selection, arrow-key
movement between items and the correct announcement to assistive technology for
free, and the checked visual state is the "clear active state" the design calls
for. Selection drives `ParentShellViewModel.SelectedPage`; the six pages live in
one `Grid` and toggle visibility.

### Views and item templates

Pages receive their view model through an explicit `Initialize(vm)` call rather
than through `DataContext`, which keeps compile-time typing.

One WinUI detail worth recording: `ItemsRepeater` does **not** set `DataContext`
on realized elements (x:Bind in the template works through generated binding
code instead). Any click handler inside an `ItemsRepeater` item template
therefore reads the item from `Tag="{x:Bind}"`, with `DataContext` only as a
fallback.

### Localization

All user-facing strings live in one keyed table, `Localization/Strings.cs`, and
XAML reaches them through a markup extension:

```xml
<TextBlock Text="{loc:Str Key=Child.Tagline}" />
```

MVP 0.1 ships Swedish only. Adding a language means adding a table (or swapping
the lookup for a `ResourceLoader`); it does not mean hunting literals through
views.

---

## 6. Design system

`Themes/` is merged into `Application.Resources` in dependency order, and no
page defines a colour, radius or shadow of its own.

| File | Contents |
| --- | --- |
| `Tokens.xaml` | Brand, text, surface, status and scene colours; the eight card accent gradients; radii; spacing; `ThemeShadow` instances; focus brushes |
| `Typography.xaml` | Font families and the type ramp, from the 52 px wordmark to the 13 px caption |
| `Surfaces.xaml` | Panels, tinted info panels, list rows, status chips, toggle-switch brush overrides, form inputs, ContentDialog chrome |
| `Buttons.xaml` | Primary, secondary, quiet, soft, child-chrome and PIN-key button styles, all from one templated base |
| `Cards.xaml` | The child app card: a real `Button` with lift-on-hover and press-down states |
| `Navigation.xaml` | The parent sidebar item |
| `AppIcons.xaml` | Ten app icons drawn as vector shapes on a 100×100 canvas |
| `Avatars.xaml` | Six avatars, same approach |

`ThemeLookup` maps `KidShell.Core`'s `AccentStyle` enum onto the brushes, so
Core never learns about brushes and XAML never learns about enums.

### Artwork

`SceneBackground` is the illustrated environment: a themed sky gradient, clouds,
a sun with fixed proportions, distant mountains, three hill bands, a lake,
trees, bushes and the white foreground wave the footer sits on. It is all vector
XAML on a stretched 1920×520 canvas, so it scales to any panel and adds no
image licences to the repository. It is `IsHitTestVisible="False"` and hidden
from assistive technology.

The child's chosen theme (`meadow` / `sunset` / `ocean`) swaps the sky, so a
profile change in Parent Mode is visibly real on the child's screen.

### Responsiveness

The card grid is an `ItemsRepeater` with a `UniformGridLayout`
(`MinItemWidth="232"`, `MinItemHeight="186"`, 22 px gaps) inside a
`MaxWidth="1180"` container. That yields a 4×2 grid from roughly 980 px of
content width upward — so 1366×768, 1920×1080 and 2560×1440 all get the layout
from the design — and degrades to three and then two columns rather than
clipping. In Parent Mode an `AdaptiveTrigger` hides the right-hand summary
column below 1320 px.

Nothing is sized in absolute screen coordinates, the manifest declares
PerMonitorV2 DPI awareness, and text uses no fixed line heights, so 125 % and
150 % scaling grow the UI instead of cropping it.

### Accessibility

* Cards, nav items and buttons are real controls, so keyboard activation,
  focus and narrator work without extra code.
* System focus visuals are enabled with explicit high-contrast brushes and a
  negative margin, so focus is clearly visible on coloured cards.
* State is never carried by colour alone: the app rows show *Tillåten* /
  *Avstängd* beside the switch, the security rows pair a glyph with a word, and
  the selected avatar gets a check badge as well as a ring.
* `AutomationProperties.Name` is set on the cards, the toggles, the remove
  buttons, the PIN keypad and the status strip.
* The three-second parent gesture also works from the keyboard (hold Space or
  Enter), so it is not pointer-only.

---

## 7. Where the Windows-security work attaches

Everything below is **out of scope for 0.1** and has a seam waiting for it.

| Future capability | Attaches to |
| --- | --- |
| Borderless full-screen child mode | `MainWindow.ConfigureWindow` / `ConfigureTitleBar`, gated on `IDeveloperOptions.DeveloperMode` |
| Real `DeveloperMode` switch | `IDeveloperOptions` — currently a constant, later parent-controlled |
| Forced PIN setup, no fallback | `DevelopmentPin` deleted; `IParentPinService.IsCustomPinConfigured` already drives the UI |
| Hardware-backed PIN | A second `IParentPinService` implementation |
| Screen-time enforcement | A watchdog reading `ScreenTimeSettings`; the page already distinguishes "configured" from "enforced" |
| Web filtering | `WebSettings.Mode` + `AllowedDomains`; a browser or Edge policy writer consumes them |
| Assigned Access / kiosk | A new provisioning service; the Säkerhet page's rows are already the status surface for it |
| AppLocker / WDAC | `KidAppDefinition.ExecutablePath` is the allowlist source |
| Secure sign-out | `ParentShellViewModel.ExitRequested` — today it closes the app and says so in the confirmation dialog |
| Child Windows account | New provisioning service; `ChildProfile` holds the human-facing half already |

---

## 8. Testing

`KidShell.Core.Tests` covers what Core promises, with no UI and without starting
a single real process:

* configuration defaults, including the eight child cards and the two
  parent-only rows that start switched off;
* serialization round-trip of every section, and that `schemaVersion` is written;
* that derived properties are not persisted;
* backup creation, temp-file cleanup, corrupt-file recovery and quarantine,
  and normalization of nonsense values;
* draft isolation, commit, the change notification, enable/disable, add and
  remove, and that each of those survives a simulated restart;
* launcher mapping for success, not-configured, not-found, failed, blocked and
  shell activation, plus that technical detail never leaks into the child
  message;
* resolver rules for bare names, missing extensions, absolute paths, quotes and
  protocol activation;
* PIN verification, malformed input, the developer-mode gate, PIN replacement,
  persistence across restart, and that no PIN is ever readable on disk.

The UI layer is verified by manual QA against the two reference images; that
list is in the milestone report rather than here.

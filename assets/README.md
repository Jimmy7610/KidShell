# KidShell assets

Everything visual in KidShell is produced by this repository. **No third-party
image, icon set, font file or illustration is bundled**, so there are no
external asset licences to honour.

## What is here

| Path | What it is | How it was produced |
| --- | --- | --- |
| `icons/kidshell-mark-512.png` | The KidShell brand mark | Drawn by `generate-brand-assets.ps1` |
| `generate-brand-assets.ps1` | Generator for every raster asset | Hand-written for this project |
| `avatars/` | Notes only — see below | — |
| `backgrounds/` | Notes only — see below | — |

## Generated package assets

`generate-brand-assets.ps1` draws the MSIX tiles, the splash screen and the
multi-resolution `.ico` with `System.Drawing`, straight from the brand palette
in `src/KidShell.App/Themes/Tokens.xaml`. Regenerate them from the repository
root with:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File assets\generate-brand-assets.ps1
```

The output lands in `src/KidShell.App/Assets/` (`StoreLogo.png`,
`Square44x44Logo.png`, `Square150x150Logo.png`, `SmallTile.png`,
`LargeTile.png`, `Wide310x150Logo.png`, `SplashScreen.png`, `KidShell.ico`) and
a 512 px master copy in `icons/`.

The generator uses the **Segoe UI** face that ships with Windows to draw the "K"
wordmark. It reads that font from the operating system at generation time; no
font file is redistributed here.

## Avatars

The six child avatars (fox, owl, bear, panda, rocket, blossom) are **not**
images. They are vector shapes defined in
`src/KidShell.App/Themes/Avatars.xaml` and drawn at runtime, which keeps them
crisp at every DPI and keeps the repository free of artwork licences. Add a new
avatar by adding an `Avatar_<id>` `DataTemplate` there and listing its id in
`ThemeLookup.AvatarIds`.

## App icons

The app card icons (palette, gamepad, lightbulb, calculator, music note, book,
clapperboard, block, globe, fallback star) are likewise vector shapes, in
`src/KidShell.App/Themes/AppIcons.xaml`, on a 100×100 canvas. Add one by adding
an `Icon_<key>` `DataTemplate` and a constant in `KidShell.Core`'s `IconKeys`.

## Backgrounds

The illustrated child/parent scene — sky, clouds, sun, mountains, hills, lake,
trees, bushes and the foreground wave — is vector XAML in
`src/KidShell.App/Views/SceneBackground.xaml`. There is no background bitmap.

## System icons

Small chrome glyphs (clock, battery, network, gear, lock, shield, trash and so
on) come from the **Segoe Fluent Icons** font that ships with Windows, referenced
by glyph code. Nothing is copied into the repository.

## If a third-party asset is ever added

Record it in this file with its source, its licence and the licence text, and
keep the licence file beside the asset. Nothing currently requires that.

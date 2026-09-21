# Avatars

The KidShell avatars are vector XAML, not image files.

They live in `src/KidShell.App/Themes/Avatars.xaml` as `Avatar_<id>`
DataTemplates and are drawn at runtime by `AvatarPresenter`, so they stay sharp
at 100%, 125% and 150% scaling and add no image licences to the repository.

Current set: `fox`, `owl`, `bear`, `panda`, `rocket`, `blossom`.

To add one: add an `Avatar_<id>` DataTemplate there, then list the id in
`KidShell.App/Themes/ThemeLookup.AvatarIds` so Parent Mode offers it.

See `assets/README.md` for the full asset provenance.

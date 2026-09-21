# Backgrounds

KidShell has no background bitmap.

The illustrated scene behind both Child Mode and Parent Mode - sky gradient,
clouds, sun, mountains, hill bands, lake, trees, bushes and the white
foreground wave - is vector XAML in
`src/KidShell.App/Views/SceneBackground.xaml`, drawn on a stretched 1920x520
canvas so it scales to any panel.

The sky swaps with the child's theme (`meadow`, `sunset`, `ocean`), which is
what makes the theme picker in Parent Mode visibly real.

See `assets/README.md` for the full asset provenance.

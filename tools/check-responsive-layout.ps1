<#
.SYNOPSIS
    Catches layout mistakes that break KidShell on displays other than the one
    it was written on.

.DESCRIPTION
    Every responsiveness bug this project has had was the same shape: a
    constant that looked right on one monitor and hid content everywhere else.
    A ScrollViewer capped at 300 that scrolled two radio buttons out of sight.
    A dialog 560 wide that clipped its own buttons at 1366. A sidebar and an
    aside column fixed at 218 and 352, which at 1024 effective pixels left the
    content narrower than a settings row.

    None of those were visible in review. All of them are mechanically
    detectable, so this looks for them.

    WHAT IT DOES NOT DO
    -------------------
    It does not object to fixed sizes as such. An icon is 26 epx because that
    is how big an icon should be, and an avatar is 100 because the artwork is
    drawn at that size - those are physical dimensions, not layout. The check
    targets containers: the elements whose job is to arrange other things.

.PARAMETER Path
    Directory to scan. Defaults to the app project.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-responsive-layout.ps1
#>

[CmdletBinding()]
param(
    [string] $Path = 'src\KidShell.App'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Elements whose purpose is to arrange other elements. A fixed dimension on one
# of these is a layout decision, and almost always the wrong one.
$containers = @(
    'Grid', 'StackPanel', 'ScrollViewer', 'Border', 'ItemsRepeater',
    'ItemsControl', 'RelativePanel', 'WrapPanel', 'ContentDialog', 'UserControl'
)

# Above this, a fixed dimension is certainly about page layout rather than
# about a graphic. A 148-epx avatar frame is fine; a 400-epx Border is a page
# deciding how wide a display is.
$layoutThreshold = 260

$files = @(
    Get-ChildItem -Path $Path -Recurse -Filter '*.xaml' -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
)

if ($files.Count -eq 0) {
    Write-Host "::error::No XAML found under $Path. The check is broken, not clean."
    exit 1
}

$findings = @()
$checked = 0

foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw
    $lines = $text -split "`n"

    # Walk opening tags so each dimension is attributed to its element.
    $pattern = '<([A-Za-z][\w.:]*)((?:[^<>"]|"[^"]*")*?)/?>'

    foreach ($match in [regex]::Matches($text, $pattern)) {
        $element = ($match.Groups[1].Value -split ':')[-1]
        $attributes = $match.Groups[2].Value
        $line = ($text.Substring(0, $match.Index) -split "`n").Count

        $isContainer = $containers -contains $element
        $checked++

        # The previous line often carries a deliberate explanation; a fixed
        # dimension that somebody wrote a reason for is not an accident.
        $justified = $false
        if ($line -ge 2) {
            $context = ($lines[[Math]::Max(0, $line - 12)..($line - 1)] -join ' ')
            $justified = $context -match 'deliberate|intentional|on purpose|fixed on purpose|physical size'
        }

        foreach ($attribute in @('Width', 'Height')) {
            $m = [regex]::Match($attributes, "\b$attribute=`"(\d+(?:\.\d+)?)`"")
            if (-not $m.Success) { continue }

            $value = [double] $m.Groups[1].Value

            # A page or dialog root with any fixed dimension is the worst case:
            # it cannot adapt at all.
            if ($element -in @('UserControl', 'ContentDialog')) {
                $findings += "$($file.Name):$line  <$element> has a fixed $attribute=$value - a page must not fix its own size"
                continue
            }

            if ($isContainer -and $value -ge $layoutThreshold -and -not $justified) {
                $findings += "$($file.Name):$line  <$element> has a fixed $attribute=$value - use MinWidth/MaxWidth, * sizing, or size it from the window"
            }
        }

        # MaxHeight on a scrollable region is the specific bug that has bitten
        # this codebase twice: content silently disappears with no visible
        # scrollbar to suggest anything is missing.
        if ($element -eq 'ScrollViewer') {
            $m = [regex]::Match($attributes, '\bMaxHeight="(\d+(?:\.\d+)?)"')
            if ($m.Success -and -not $justified) {
                $findings += "$($file.Name):$line  <ScrollViewer> has a fixed MaxHeight=$($m.Groups[1].Value) - size it from the window, or let the page scroll"
            }
        }
    }

    # Horizontal scrolling is almost always a layout failure rather than an
    # interaction; where it is genuinely wanted, say so in a comment.
    foreach ($match in [regex]::Matches($text, 'HorizontalScrollMode="Enabled"')) {
        $line = ($text.Substring(0, $match.Index) -split "`n").Count
        $context = ($lines[[Math]::Max(0, $line - 12)..($line - 1)] -join ' ')

        if ($context -notmatch 'deliberate|intentional|on purpose|code area|wide content') {
            $findings += "$($file.Name):$line  horizontal scrolling enabled without a stated reason"
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host '::error::Layout that will not adapt to other displays:'
    $findings | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host 'If a dimension is genuinely physical - an icon, an avatar, artwork -'
    Write-Host 'say so in a comment above it and this check will accept it.'
    exit 1
}

Write-Host "Checked $checked element(s) in $($files.Count) file(s); no fixed page layout found."
exit 0

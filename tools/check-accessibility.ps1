<#
.SYNOPSIS
    Finds interactive controls a screen reader would announce as unnamed.

.DESCRIPTION
    An icon-only button with no accessible name is announced as just "button".
    A parent using Narrator then has to guess which row it belongs to, and on a
    list of allowed websites the wrong guess deletes the wrong site.

    This is a lint, not a substitute for using the product with a screen reader.
    It catches the one failure that is both common and mechanically detectable:
    a control with no name and no visible text.

    A control counts as named if it has AutomationProperties.Name, a Content,
    Header or PlaceholderText attribute, or any TextBlock inside it - UIA
    derives a name from visible text, so a button wrapping an icon and a label
    is fine.

.PARAMETER Path
    Directory to scan. Defaults to the app project.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-accessibility.ps1
#>

[CmdletBinding()]
param(
    [string] $Path = 'src\KidShell.App'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$controls = @(
    'Button', 'ToggleSwitch', 'Slider', 'TextBox', 'PasswordBox', 'RadioButton',
    'CheckBox', 'ComboBox', 'HoldButton', 'ToggleButton'
)

$files = @(
    Get-ChildItem -Path $Path -Recurse -Filter '*.xaml' -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
)

if ($files.Count -eq 0) {
    Write-Host "::error::No XAML found under $Path. The check is broken, not clean."
    exit 1
}

$pattern = '<(controls:)?(' + ($controls -join '|') + ')\b'
$findings = @()
$checked = 0

foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw

    foreach ($match in [regex]::Matches($text, $pattern)) {
        $prefix = $match.Groups[1].Value
        $kind = $match.Groups[2].Value

        # Walk to the end of the opening tag, respecting quoted values so an
        # attribute containing ">" does not end it early.
        $i = $match.Index + $match.Length
        $inQuote = $false

        while ($i -lt $text.Length) {
            $c = $text[$i]
            if ($c -eq '"') { $inQuote = -not $inQuote }
            elseif ($c -eq '>' -and -not $inQuote) { break }
            $i++
        }

        $attributes = $text.Substring($match.Index, $i - $match.Index)
        $selfClosing = $text[$i - 1] -eq '/'

        # The element's full extent, so child content counts towards a name.
        $body = ''
        if (-not $selfClosing) {
            $closing = "</$prefix$kind>"
            $close = $text.IndexOf($closing, $i)
            if ($close -ge 0) { $body = $text.Substring($i, $close - $i) }
        }

        $checked++

        $named = $attributes.Contains('AutomationProperties.Name') -or
                 ($attributes -match '\b(Content|Header|PlaceholderText)="') -or
                 $body.Contains('<TextBlock') -or
                 $body.Contains('AutomationProperties.Name')

        if (-not $named) {
            $line = ($text.Substring(0, $match.Index) -split "`n").Count
            $findings += "$($file.Name):$line  <$kind>"
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host '::error::Interactive controls with no accessible name:'
    $findings | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host 'Add AutomationProperties.Name, or give the control visible text.'
    exit 1
}

Write-Host "Checked $checked interactive control(s) in $($files.Count) file(s); every one has an accessible name."
exit 0

# Stamps a release tag (vX.Y.Z) into every file that carries the version, so
# cutting a release no longer needs a "Bump version strings" commit:
#   PackagingProject\Package.appxmanifest  Identity Version="2.X.Y.Z"
#   RoundedTB\AboutWindow.xaml(.cs)         "Community Edition vX.Y.Z"
# The MSIX version keeps the existing 2.<tag> scheme (v0.4.2 -> 2.0.4.2), which
# stays above the Store build 2.0.4.0 so in-place upgrades keep working.
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)$') {
    throw "Tag '$Tag' is not of the form vX.Y.Z"
}
$semver = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$msixVersion = "2.$semver"

function Update-File([string] $RelativePath, [string] $Pattern, [string] $Replacement) {
    $path = Join-Path $Root $RelativePath
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($path)
    if ($text -notmatch $Pattern) {
        throw "Version pattern not found in $RelativePath"
    }
    $updated = [regex]::Replace($text, $Pattern, $Replacement)
    # Keep each file's BOM state so the stamp is the only diff.
    [IO.File]::WriteAllText($path, $updated, (New-Object Text.UTF8Encoding($hasBom)))
    Write-Host "$RelativePath -> $(([regex]::Match($updated, $Pattern)).Value)"
}

Update-File 'PackagingProject\Package.appxmanifest' '(?<=<Identity[^>]*\sVersion=")[\d.]+(?=")' $msixVersion
Update-File 'RoundedTB\AboutWindow.xaml' '(?<=Community Edition v)[\d.]+' $semver
Update-File 'RoundedTB\AboutWindow.xaml.cs' '(?<=Community Edition v)[\d.]+' $semver

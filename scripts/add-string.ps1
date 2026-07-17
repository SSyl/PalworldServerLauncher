#!/usr/bin/env pwsh
# Add one localized string key to ALL TEN Strings resx files at once, so a new UI string is a single
# command instead of ten hand edits. The satellites default to the English text as a placeholder (so the
# LocalizationSmokeTests key-parity check stays green immediately); pass real translations when you have them
# and fill the placeholders later.
#
#   pwsh scripts/add-string.ps1 -Key My_New_Key -En "English text"
#   pwsh scripts/add-string.ps1 -Key My_New_Key -En "English" -ZhHans "简体" -ZhHant "繁體" -Ja "日本語" `
#       -De "Deutsch" -Es "Español" -Fr "Français" -Ko "한국어" -PtBr "Português" -Ru "Русский"

param(
    [Parameter(Mandatory)] [string] $Key,
    [Parameter(Mandatory)] [string] $En,
    [string] $ZhHans,
    [string] $ZhHant,
    [string] $Ja,
    [string] $De,
    [string] $Es,
    [string] $Fr,
    [string] $Ko,
    [string] $PtBr,
    [string] $Ru
)

$ErrorActionPreference = 'Stop'
$localizationDir = Join-Path $PSScriptRoot '..' 'src' 'PalServerLauncher.Localization'

# Satellites fall back to the English text when a translation isn't supplied.
$targets = @(
    @{ File = 'Strings.resx';         Value = $En },
    @{ File = 'Strings.zh-Hans.resx'; Value = if ($ZhHans) { $ZhHans } else { $En } },
    @{ File = 'Strings.zh-Hant.resx'; Value = if ($ZhHant) { $ZhHant } else { $En } },
    @{ File = 'Strings.ja.resx';      Value = if ($Ja)     { $Ja }     else { $En } },
    @{ File = 'Strings.de.resx';      Value = if ($De)     { $De }     else { $En } },
    @{ File = 'Strings.es.resx';      Value = if ($Es)     { $Es }     else { $En } },
    @{ File = 'Strings.fr.resx';      Value = if ($Fr)     { $Fr }     else { $En } },
    @{ File = 'Strings.ko.resx';      Value = if ($Ko)     { $Ko }     else { $En } },
    @{ File = 'Strings.pt-BR.resx';   Value = if ($PtBr)   { $PtBr }   else { $En } },
    @{ File = 'Strings.ru.resx';      Value = if ($Ru)     { $Ru }     else { $En } }
)

function ConvertTo-XmlText([string] $s) {
    $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}

$added = 0
foreach ($t in $targets) {
    $path = Join-Path $localizationDir $t.File
    $content = Get-Content -Raw -Encoding UTF8 $path
    if ($content -match [regex]::Escape("name=`"$Key`"")) {
        Write-Warning "$($t.File): key '$Key' already present, skipping."
        continue
    }
    $value = ConvertTo-XmlText $t.Value
    $entry = "  <data name=`"$Key`" xml:space=`"preserve`">`n    <value>$value</value>`n  </data>`n"
    # Insert just before the closing </root> so the rest of the file is untouched (minimal diff).
    $updated = $content -replace '(?s)</root>\s*$', "$entry</root>`n"
    Set-Content -Path $path -Value $updated -Encoding utf8NoBOM -NoNewline
    Write-Host "$($t.File): added '$Key'."
    $added++
}

if ($added -gt 0) {
    Write-Host "`nDone. Run 'dotnet test --filter LocalizationSmokeTests' to confirm key parity."
}

#!/usr/bin/env pwsh

# Keep a blank line between this comment and the <# #> block. Any comment line sitting directly against it,
# the shebang included, makes Get-Help ignore the comment-based help and return the bare syntax line, which
# breaks the help command too.

<#
.SYNOPSIS
    Read, write, search, and audit the ten Strings resx files.

.DESCRIPTION
    Every command parses the resx as XML and works on <value> content only, so key names never pollute a
    search and the boilerplate header comment (which contains example <data name="Name1"> markup) never
    inflates a count. Writes are spliced in at character offsets, so the diff covers only the changed lines
    and the file's encoding, line endings, and formatting are preserved.

    Commands:
      add               Add one key to all ten files. Satellites default to the English text.
      set               Change one value in one file.
      get               Print one key's value in all ten languages.
      find              Search value text.
      untranslated      List satellite values that are still the English text.
      mark-invariant    Mark a key as the same in every language on purpose, so untranslated skips it.
      unmark-invariant  Remove that marker.
      mark-reviewed     Confirm one satellite's value, even though it equals English. Needs -Lang.
      unmark-reviewed   Remove that marker.
      validate          Key parity, duplicates, empty values, {N} placeholder parity. Non-zero on findings.
      audit             validate plus per-language coverage and a catalog-check summary. Always exits zero.
      catalog-check     Compare Cat_*_Label values against Palworld's own L10N export.

    Two markers, both a resx <comment>, told apart by the file they sit in. Trailing text is allowed on
    either, so "invariant, brand name" and "reviewed, same word in German" both count.

      Strings.resx     <comment>invariant</comment>   never translate this, in any language
      Strings.de.resx  <comment>reviewed</comment>    a human confirmed this German value

    untranslated hides both unless -IncludeInvariant or -IncludeReviewed, and reports the hidden counts
    separately. Changing an English value clears every reviewed marker on that key, since the wording
    those markers confirmed no longer exists.

    Exit codes: 0 clean, 1 findings or failure, 2 usage error, 3 a write failed verification and was rolled back.

    catalog-check reads Palworld's own game assets, extracted from the shipped paks with FModel. Those assets
    are Pocketpair's copyrighted work and MUST NOT be committed or redistributed, not in whole and not as a
    trimmed subset, so they stay outside this repo and every developer extracts their own. Point at them with
    -ExportsPath or the PALWORLD_EXPORTS environment variable, or leave an 'Exports' folder next to the repo.
    Without them this command reports itself skipped and every other command is unaffected.

.EXAMPLE
    pwsh scripts/localization.ps1 add -Key My_New_Key -En "English text"

.EXAMPLE
    pwsh scripts/localization.ps1 add -Key My_New_Key -En "English" -De "Deutsch" -Fr "Francais"

.EXAMPLE
    pwsh scripts/localization.ps1 set -Key Start_Button -Lang de -Value "Starten"

.EXAMPLE
    pwsh scripts/localization.ps1 get -Key Start_Button

.EXAMPLE
    pwsh scripts/localization.ps1 find -Text "launcher" -Lang default

.EXAMPLE
    pwsh scripts/localization.ps1 untranslated -Lang ru

.EXAMPLE
    pwsh scripts/localization.ps1 mark-invariant -Key Common_AppName -Reason "brand name"

.EXAMPLE
    pwsh scripts/localization.ps1 validate
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('add', 'set', 'get', 'find', 'untranslated', 'validate', 'audit', 'catalog-check',
        'mark-invariant', 'unmark-invariant', 'mark-reviewed', 'unmark-reviewed', 'help')]
    [string] $Command = 'help',

    [string] $Key,
    [string] $Lang,
    [string] $Value,
    [string] $Text,
    [string] $Reason,
    [switch] $Regex,
    [switch] $CaseSensitive,
    [switch] $IncludeInvariant,
    [switch] $IncludeReviewed,

    [ValidateSet('table', 'json')]
    [string] $Format = 'table',

    [string] $Path,
    [string] $ExportsPath,
    [switch] $RequireClean,
    [switch] $ShowReworded,

    [string] $En,
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
Set-StrictMode -Version 3.0

# $PSBoundParameters inside a function is that function's own, so snapshot the script's here. The commands
# need to tell "-Value ''" (a deliberate blank) apart from -Value never being passed.
$script:Bound = $PSBoundParameters

try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

# Display order: English first, then alphabetical by culture code.
$script:LangFiles = [ordered]@{
    'default' = 'Strings.resx'
    'de'      = 'Strings.de.resx'
    'es'      = 'Strings.es.resx'
    'fr'      = 'Strings.fr.resx'
    'ja'      = 'Strings.ja.resx'
    'ko'      = 'Strings.ko.resx'
    'pt-BR'   = 'Strings.pt-BR.resx'
    'ru'      = 'Strings.ru.resx'
    'zh-Hans' = 'Strings.zh-Hans.resx'
    'zh-Hant' = 'Strings.zh-Hant.resx'
}

$script:LangAliases = @{
    'en'      = 'default'
    'english' = 'default'
    'ptbr'    = 'pt-BR'
    'pt'      = 'pt-BR'
    'zhhans'  = 'zh-Hans'
    'zhcn'    = 'zh-Hans'
    'zhhant'  = 'zh-Hant'
    'zhtw'    = 'zh-Hant'
}

$script:ExitFindings = 1
$script:ExitUsage = 2
$script:ExitRolledBack = 3
$script:ExitCode = 0

function Resolve-Lang {
    param([Parameter(Mandatory)][string] $Name)

    foreach ($known in $script:LangFiles.Keys) {
        if ($known -eq $Name) { return $known }
    }
    $flat = ($Name -replace '[-_]', '').ToLowerInvariant()
    if ($script:LangAliases.ContainsKey($flat)) { return $script:LangAliases[$flat] }
    throw "Unknown language '$Name'. Use one of: $(($script:LangFiles.Keys) -join ', ') (or 'en' for default)."
}

function Get-LocalizationDir {
    if ($Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "-Path '$Path' is not a folder." }
        return (Resolve-Path -LiteralPath $Path).ProviderPath
    }
    $candidate = Join-Path $PSScriptRoot '..' 'src' 'PalServerLauncher.Localization'
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "Cannot find the localization folder at '$candidate'. Pass -Path."
    }
    return (Resolve-Path -LiteralPath $candidate).ProviderPath
}

function Get-LangPath {
    param([Parameter(Mandatory)][string] $LangKey)
    return Join-Path (Get-LocalizationDir) $script:LangFiles[$LangKey]
}

# Neither XmlDocument nor the resource compiler normalizes line endings, so a multi-line value comes back
# with whatever the file holds. Compare on LF and let each write use the target file's own newline, so a
# CRLF file and an LF one never look like a difference in the value itself.
#
# Typed [object] rather than [string] because a [string] parameter coerces $null to '', which would make an
# absent <comment> indistinguishable from an empty one.
function Get-NormalizedValue {
    param([AllowNull()][object] $Value)
    if ($null -eq $Value) { return $null }
    return ([string]$Value).Replace("`r`n", "`n").Replace("`r", "`n")
}

function ConvertTo-XmlText {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)

    $bad = [regex]::Match($Value, '[\x00-\x08\x0B\x0C\x0E-\x1F]')
    if ($bad.Success) {
        throw ("Value contains control character U+{0:X4} at offset {1}, which XML 1.0 cannot represent." -f [int][char]$bad.Value[0], $bad.Index)
    }
    # Quotes and backslashes stay literal on purpose. Pal\Binaries\Win64 and "quoted" text appear in real values.
    return $Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}

function ConvertFrom-XmlText {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)

    if ($Value.IndexOf([char]'&') -lt 0) { return $Value }
    $evaluator = {
        param($m)
        $entity = $m.Groups[1].Value
        switch -CaseSensitive ($entity) {
            'lt' { return '<' }
            'gt' { return '>' }
            'amp' { return '&' }
            'quot' { return '"' }
            'apos' { return "'" }
        }
        if ($entity.StartsWith('#x')) { return [char]::ConvertFromUtf32([Convert]::ToInt32($entity.Substring(2), 16)) }
        if ($entity.StartsWith('#')) { return [char]::ConvertFromUtf32([int]$entity.Substring(1)) }
        throw "Unknown XML entity '&$entity;'."
    }
    return [regex]::Replace($Value, '&(#x[0-9A-Fa-f]+|#[0-9]+|[A-Za-z][A-Za-z0-9]*);', $evaluator)
}

function Get-DominantNewline {
    param([Parameter(Mandatory)][string] $Content)
    $crlf = ([regex]::Matches($Content, "`r`n")).Count
    $lf = ([regex]::Matches($Content, "(?<!`r)`n")).Count
    if ($crlf -ge $lf) { return "`r`n" }
    return "`n"
}

function Read-ResxText {
    param([Parameter(Mandatory)][string] $LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { throw "No such file: $LiteralPath" }
    $bytes = [System.IO.File]::ReadAllBytes($LiteralPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "$LiteralPath starts with a UTF-8 BOM. These files are BOM-less, refusing to rewrite one."
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Write-ResxText {
    param(
        [Parameter(Mandatory)][string] $LiteralPath,
        [Parameter(Mandatory)][string] $Content
    )
    [System.IO.File]::WriteAllText($LiteralPath, $Content, [System.Text.UTF8Encoding]::new($false))
}

# Locates every real <data> element and the character spans of its <value> and <comment> text, so a write
# can splice one of them without reserializing the document. An [xml] round-trip would reflow the whole file.
#
# The scanning is left to the regex engine on purpose. An equivalent character-by-character walk in
# PowerShell ran 2 seconds per resx against 20 ms here, and validate loads ten of them.
# Trailing text is allowed, so "invariant, brand name" and "reviewed, same word in German" both count.
function Test-MarkerComment {
    param([string] $Comment, [Parameter(Mandatory)][string] $Word)
    if ([string]::IsNullOrWhiteSpace($Comment)) { return $false }
    return $Comment.TrimStart() -match "^$Word\b"
}

function New-Span {
    param(
        [Parameter(Mandatory)][string] $Name,
        [int] $ValueStart = -1,
        [int] $ValueEnd = -1,
        [AllowNull()][object] $RawValue,
        [int] $CommentStart = -1,
        [int] $CommentEnd = -1,
        [AllowNull()][object] $RawComment,
        [int] $AfterValue = -1,
        [string] $Indent = '    '
    )
    return [pscustomobject]@{
        Name         = $Name
        ValueStart   = $ValueStart
        ValueEnd     = $ValueEnd
        RawValue     = $RawValue
        CommentStart = $CommentStart
        CommentEnd   = $CommentEnd
        RawComment   = $RawComment
        AfterValue   = $AfterValue
        Indent       = $Indent
    }
}

function Get-ValueSpans {
    param([Parameter(Mandatory)][string] $Content)

    $ordinal = [System.StringComparison]::Ordinal
    $spans = [System.Collections.Generic.List[object]]::new()

    # The resx boilerplate header comments out example <data name="Name1"> markup, which is not a real key.
    # Counting <data name= by text gives 800 for the files that carry that header and 796 for the ones that
    # do not, which is where the recurring "how many keys are there" confusion comes from.
    $commentSpans = [System.Collections.Generic.List[object]]::new()
    foreach ($comment in [regex]::Matches($Content, '<!--.*?-->', 'Singleline')) {
        $commentSpans.Add([pscustomobject]@{ Start = $comment.Index; End = $comment.Index + $comment.Length })
    }

    # [^>]* would truncate on an attribute value containing '>'. No resx attribute does, and the cross-check
    # in Get-ResxDocument catches it if one ever does.
    foreach ($tag in [regex]::Matches($Content, '<data\b[^>]*>')) {
        $tagStart = $tag.Index
        $skip = $false
        foreach ($span in $commentSpans) {
            if ($tagStart -ge $span.Start -and $tagStart -lt $span.End) { $skip = $true; break }
        }
        if ($skip) { continue }

        $nameMatch = [regex]::Match($tag.Value, '\bname\s*=\s*(["''])(?<n>.*?)\1')
        if (-not $nameMatch.Success) { throw "A <data> element at offset $tagStart has no name attribute." }
        $name = ConvertFrom-XmlText $nameMatch.Groups['n'].Value

        if ($tag.Value.EndsWith('/>')) {
            $spans.Add((New-Span -Name $name))
            continue
        }

        $tagEnd = $tagStart + $tag.Length
        $dataEnd = $Content.IndexOf('</data>', $tagEnd, $ordinal)
        if ($dataEnd -lt 0) { throw "The <data name=`"$name`"> element is never closed." }

        $valueOpen = $Content.IndexOf('<value>', $tagEnd, $ordinal)
        if ($valueOpen -lt 0 -or $valueOpen -gt $dataEnd) {
            $spans.Add((New-Span -Name $name))
            continue
        }
        $valueStart = $valueOpen + 7
        $valueEnd = $Content.IndexOf('</value>', $valueStart, $ordinal)
        if ($valueEnd -lt 0 -or $valueEnd -gt $dataEnd) { throw "The <value> of '$name' is never closed." }

        # A value cannot hold a literal '<', so a <comment> found inside the element is always the real one.
        $commentStart = -1
        $commentEnd = -1
        $rawComment = $null
        $commentOpen = $Content.IndexOf('<comment>', $tagEnd, $ordinal)
        if ($commentOpen -ge 0 -and $commentOpen -lt $dataEnd) {
            $commentStart = $commentOpen + 9
            $commentEnd = $Content.IndexOf('</comment>', $commentStart, $ordinal)
            if ($commentEnd -lt 0 -or $commentEnd -gt $dataEnd) { throw "The <comment> of '$name' is never closed." }
            $rawComment = $Content.Substring($commentStart, $commentEnd - $commentStart)
        }

        $lineStart = $Content.LastIndexOf("`n", $valueOpen) + 1
        $spans.Add((New-Span -Name $name -ValueStart $valueStart -ValueEnd $valueEnd `
                    -RawValue $Content.Substring($valueStart, $valueEnd - $valueStart) `
                    -CommentStart $commentStart -CommentEnd $commentEnd -RawComment $rawComment `
                    -AfterValue ($valueEnd + 8) -Indent $Content.Substring($lineStart, $valueOpen - $lineStart)))
    }

    return $spans
}

function Get-ResxDocument {
    param([Parameter(Mandatory)][string] $LiteralPath)

    $content = Read-ResxText -LiteralPath $LiteralPath

    $xml = [System.Xml.XmlDocument]::new()
    $xml.PreserveWhitespace = $true
    $xml.LoadXml($content)

    $parsed = @(foreach ($node in $xml.SelectNodes('/root/data')) {
            $valueNode = $node.SelectSingleNode('value')
            $commentNode = $node.SelectSingleNode('comment')
            [pscustomobject]@{
                Name    = $node.GetAttribute('name')
                Value   = if ($null -eq $valueNode) { $null } else { $valueNode.InnerText }
                Comment = if ($null -eq $commentNode) { $null } else { $commentNode.InnerText }
            }
        })

    # The span scanner is hand-rolled, so check it against the XML parser on every load. A misalignment here
    # would splice a value into the wrong element.
    $spans = @(Get-ValueSpans -Content $content)
    if ($spans.Count -ne $parsed.Count) {
        throw "$LiteralPath : the span scanner found $($spans.Count) data elements, the XML parser found $($parsed.Count)."
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $parsed.Count; $i++) {
        if ($spans[$i].Name -ne $parsed[$i].Name) {
            throw "$LiteralPath : element $i is '$($spans[$i].Name)' to the scanner and '$($parsed[$i].Name)' to the XML parser."
        }
        $value = Get-NormalizedValue $parsed[$i].Value
        if ($null -ne $spans[$i].RawValue) {
            $decoded = Get-NormalizedValue (ConvertFrom-XmlText $spans[$i].RawValue)
            if ($decoded -cne $value) {
                throw "$LiteralPath : the scanner and the XML parser disagree on the value of '$($parsed[$i].Name)'."
            }
        }
        $comment = Get-NormalizedValue $parsed[$i].Comment
        $scannedComment = if ($null -eq $spans[$i].RawComment) { $null } else { Get-NormalizedValue (ConvertFrom-XmlText $spans[$i].RawComment) }
        if ($scannedComment -cne $comment) {
            throw "$LiteralPath : the scanner and the XML parser disagree on the comment of '$($parsed[$i].Name)'."
        }
        $entries.Add([pscustomobject]@{
                Name         = $parsed[$i].Name
                Value        = $value
                Comment      = $comment
                Invariant    = Test-MarkerComment $comment 'invariant'
                Reviewed     = Test-MarkerComment $comment 'reviewed'
                ValueStart   = $spans[$i].ValueStart
                ValueEnd     = $spans[$i].ValueEnd
                RawValue     = $spans[$i].RawValue
                CommentStart = $spans[$i].CommentStart
                CommentEnd   = $spans[$i].CommentEnd
                AfterValue   = $spans[$i].AfterValue
                Indent       = $spans[$i].Indent
            })
    }

    # Ordinal on purpose. A PowerShell hashtable matches keys case-insensitively, which would hide a
    # Foo / foo pair and let 'set' write to a key the caller did not name.
    $index = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    $duplicates = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $entries) {
        if ($index.ContainsKey($entry.Name)) {
            if (-not $duplicates.Contains($entry.Name)) { $duplicates.Add($entry.Name) }
            continue
        }
        $index[$entry.Name] = $entry
    }

    return [pscustomobject]@{
        Path       = $LiteralPath
        Content    = $content
        Newline    = Get-DominantNewline -Content $content
        Entries    = $entries
        Names      = @($entries | ForEach-Object { $_.Name })
        Index      = $index
        Duplicates = $duplicates
    }
}

function Get-LangDocument {
    param([Parameter(Mandatory)][string] $LangKey)
    return Get-ResxDocument -LiteralPath (Get-LangPath $LangKey)
}

# Same rule as LocalizationSmokeTests.PlaceholderIndices, so the two agree on what counts as a mismatch.
function Get-PlaceholderIndices {
    param([string] $Value)
    if ([string]::IsNullOrEmpty($Value)) { return @() }
    $cleaned = $Value.Replace('{{', '').Replace('}}', '')
    $found = [System.Collections.Generic.SortedSet[int]]::new()
    foreach ($m in [regex]::Matches($cleaned, '\{(\d+)')) { [void]$found.Add([int]$m.Groups[1].Value) }
    return , [int[]]$found
}

function Assert-CleanTree {
    param([Parameter(Mandatory)][string] $Directory)

    $status = & git -C $Directory status --porcelain -- $Directory 2>&1
    if ($LASTEXITCODE -ne 0) { throw "-RequireClean was passed but git could not read the tree: $status" }
    if ($status) {
        throw "-RequireClean was passed and the localization folder has uncommitted changes:`n$($status -join "`n")"
    }
}

function Test-KeyName {
    param([Parameter(Mandatory)][string] $Name)
    if ($Name -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$') {
        throw "Key '$Name' is not a valid resource identifier (letters, digits, underscore, dot, not starting with a digit)."
    }
}

# Restores from a temp copy and rethrows if $Verify rejects the reloaded file, so a bad write leaves nothing
# behind. The tree is deliberately allowed to be dirty, this is the safety net instead.
function Set-ResxContentVerified {
    param(
        [Parameter(Mandatory)][string] $LiteralPath,
        [Parameter(Mandatory)][string] $NewContent,
        [Parameter(Mandatory)][scriptblock] $Verify
    )

    $backup = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(),
        "strings-ps1-$([System.IO.Path]::GetFileName($LiteralPath))-$([Guid]::NewGuid().ToString('n')).bak")
    [System.IO.File]::Copy($LiteralPath, $backup, $true)

    try {
        Write-ResxText -LiteralPath $LiteralPath -Content $NewContent

        $expectedBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($NewContent)
        $actualBytes = [System.IO.File]::ReadAllBytes($LiteralPath)
        if ($actualBytes.Length -ne $expectedBytes.Length) {
            throw "the file on disk is $($actualBytes.Length) bytes, expected $($expectedBytes.Length). Encoding or line endings were altered."
        }
        for ($i = 0; $i -lt $expectedBytes.Length; $i++) {
            if ($actualBytes[$i] -ne $expectedBytes[$i]) { throw "the bytes on disk differ from what was written, first at offset $i." }
        }

        $reloaded = Get-ResxDocument -LiteralPath $LiteralPath
        & $Verify $reloaded
    } catch {
        [System.IO.File]::Copy($backup, $LiteralPath, $true)
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        throw [System.Management.Automation.RuntimeException]::new(
            "$([System.IO.Path]::GetFileName($LiteralPath)): verification failed, the file was restored unchanged. $($_.Exception.Message)")
    }

    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
}

function Assert-UnchangedExcept {
    param(
        [Parameter(Mandatory)][object] $Before,
        [Parameter(Mandatory)][object] $After,
        [string[]] $Added = @(),
        [string[]] $Changed = @(),
        [string[]] $CommentChanged = @()
    )

    $expectedNames = @($Before.Names) + @($Added)
    if ($After.Names.Count -ne $expectedNames.Count) {
        throw "key count went from $($Before.Names.Count) to $($After.Names.Count), expected $($expectedNames.Count)."
    }
    for ($i = 0; $i -lt $expectedNames.Count; $i++) {
        if ($After.Names[$i] -cne $expectedNames[$i]) {
            throw "key order changed at position $i : '$($expectedNames[$i])' became '$($After.Names[$i])'."
        }
    }
    # A dropped <comment> would silently unmark an invariant key, so a value write has to keep every one of
    # them, including on the key it is changing.
    foreach ($name in $Before.Names) {
        if ($CommentChanged -contains $name) { continue }
        if ($After.Index[$name].Comment -cne $Before.Index[$name].Comment) {
            throw "the comment on '$name' changed but should not have."
        }
    }
    foreach ($name in $Before.Names) {
        if ($Changed -contains $name) { continue }
        if ((Get-NormalizedValue $After.Index[$name].Value) -cne (Get-NormalizedValue $Before.Index[$name].Value)) {
            throw "the value of '$name' changed but should not have."
        }
    }
}

function Format-Inline {
    param([string] $Value, [int] $MaxLength = 0)
    if ($null -eq $Value) { return '' }
    $flat = ($Value -replace "`r`n", '\n') -replace "`n", '\n'
    if ($MaxLength -gt 0 -and $flat.Length -gt $MaxLength) { return $flat.Substring(0, $MaxLength - 1) + [char]0x2026 }
    return $flat
}

function Format-Indented {
    param([string] $Value, [int] $Indent)
    if ($null -eq $Value) { return '' }
    return (Get-NormalizedValue $Value) -replace "`n", ("`n" + (' ' * $Indent))
}

function Write-Json {
    param([Parameter(Mandatory)][AllowNull()][object] $Data)
    Write-Output (ConvertTo-Json -InputObject $Data -Depth 8)
}

function Invoke-Add {
    if (-not $Key) { throw 'add needs -Key.' }
    if (-not $script:Bound.ContainsKey('En')) { throw 'add needs -En.' }
    Test-KeyName -Name $Key
    if ($RequireClean) { Assert-CleanTree -Directory (Get-LocalizationDir) }

    $supplied = [ordered]@{
        'default' = $En
        'de'      = $De
        'es'      = $Es
        'fr'      = $Fr
        'ja'      = $Ja
        'ko'      = $Ko
        'pt-BR'   = $PtBr
        'ru'      = $Ru
        'zh-Hans' = $ZhHans
        'zh-Hant' = $ZhHant
    }

    $added = 0
    $skipped = 0
    foreach ($langKey in $supplied.Keys) {
        $file = $script:LangFiles[$langKey]
        $target = Get-LangPath $langKey
        $before = Get-ResxDocument -LiteralPath $target

        if ($before.Index.ContainsKey($Key)) {
            Write-Warning "$file : key '$Key' already present, skipping."
            $skipped++
            continue
        }

        $wanted = if ([string]::IsNullOrEmpty($supplied[$langKey])) { $En } else { $supplied[$langKey] }
        $newline = $before.Newline
        $encoded = ConvertTo-XmlText ($wanted -replace "`r`n", "`n" -replace "`n", $newline)
        $entry = "  <data name=`"$Key`" xml:space=`"preserve`">$newline    <value>$encoded</value>$newline  </data>$newline"

        $rootClose = $before.Content.LastIndexOf('</root>', [System.StringComparison]::Ordinal)
        if ($rootClose -lt 0) { throw "$file has no closing </root>." }
        $newContent = $before.Content.Substring(0, $rootClose) + $entry + $before.Content.Substring($rootClose)

        Set-ResxContentVerified -LiteralPath $target -NewContent $newContent -Verify {
            param($after)
            Assert-UnchangedExcept -Before $before -After $after -Added @($Key)
            if (-not $after.Index.ContainsKey($Key)) { throw "'$Key' is not in the reloaded file." }
            if ($after.Index[$Key].Value -cne (Get-NormalizedValue $wanted)) {
                throw "'$Key' did not round-trip. Wanted [$(Format-Inline $wanted)], got [$(Format-Inline $after.Index[$Key].Value)]."
            }
            if ($after.Duplicates.Count -ne 0) { throw "the write introduced duplicate keys: $($after.Duplicates -join ', ')." }
        }

        Write-Output "$file : added '$Key'."
        $added++
    }

    if ($added -gt 0) {
        Write-Output ''
        Write-Output "Added to $added file(s). Run 'dotnet test --filter LocalizationSmokeTests' to confirm key parity."
    }
    if ($added -eq 0 -and $skipped -gt 0) { $script:ExitCode = $script:ExitFindings }
    return
}

function Invoke-Set {
    if (-not $Key) { throw 'set needs -Key.' }
    if (-not $Lang) { throw "set needs -Lang (use 'default' for English)." }
    if (-not $script:Bound.ContainsKey('Value')) { throw 'set needs -Value.' }

    $langKey = Resolve-Lang $Lang
    $file = $script:LangFiles[$langKey]
    if ($RequireClean) { Assert-CleanTree -Directory (Get-LocalizationDir) }

    $target = Get-LangPath $langKey
    $before = Get-ResxDocument -LiteralPath $target

    if (-not $before.Index.ContainsKey($Key)) {
        throw "$file has no key '$Key'. Use 'add' to create it in all ten files."
    }
    if ($before.Duplicates -contains $Key) {
        throw "$file holds '$Key' more than once. Fix that by hand before setting it."
    }
    $entry = $before.Index[$Key]
    if ($entry.ValueStart -lt 0) { throw "'$Key' in $file has no <value> element." }

    $wanted = Get-NormalizedValue $Value
    if ($entry.Value -ceq $wanted) {
        Write-Output "$file : '$Key' already holds that value, nothing written."
        return
    }

    $encoded = ConvertTo-XmlText ($wanted -replace "`n", $before.Newline)
    $newContent = $before.Content.Substring(0, $entry.ValueStart) + $encoded + $before.Content.Substring($entry.ValueEnd)

    Set-ResxContentVerified -LiteralPath $target -NewContent $newContent -Verify {
        param($after)
        Assert-UnchangedExcept -Before $before -After $after -Changed @($Key)
        if ($after.Index[$Key].Value -cne $wanted) {
            throw "'$Key' did not round-trip. Wanted [$(Format-Inline $wanted)], got [$(Format-Inline $after.Index[$Key].Value)]."
        }
        if ($after.Duplicates.Count -ne 0) { throw "the write introduced duplicate keys: $($after.Duplicates -join ', ')." }
    }

    Write-Output "$file : set '$Key'."
    Write-Output "  was: $(Format-Inline $entry.Value 160)"
    Write-Output "  now: $(Format-Inline $wanted 160)"

    if ($langKey -eq 'default') {
        $cleared = @(Clear-ReviewedForKey -KeyName $Key)
        if ($cleared.Count -gt 0) {
            Write-Output "  cleared the reviewed marker on $($cleared -join ' '), the English wording they confirmed has changed."
        }
    }

    if ($langKey -ne 'default') {
        $english = Get-LangDocument 'default'
        if ($english.Index.ContainsKey($Key)) {
            $expected = Get-PlaceholderIndices $english.Index[$Key].Value
            $actual = Get-PlaceholderIndices $wanted
            if (($expected -join ',') -ne ($actual -join ',')) {
                Write-Warning ("'$Key' placeholders now differ from English: en={{{0}}} {1}={{{2}}}. This throws FormatException at runtime." -f ($expected -join ','), $langKey, ($actual -join ','))
            }
        }
    }
    return
}

# Resx has no state attribute, so <comment> carries both markers and the FILE it sits in tells them apart.
# In Strings.resx "invariant" means never translate this in any language, XLIFF's translate="no". In a
# satellite "reviewed" means a human confirmed this target, XLIFF's state="final", which is the only place
# the fact can live: "Hardcore" is the official Palworld term in en/es/de/fr but "Intenso" in pt-BR, so no
# source-side flag can express it.
function Set-CommentMarker {
    param(
        [Parameter(Mandatory)][string] $LangKey,
        [Parameter(Mandatory)][string] $KeyName,
        [Parameter(Mandatory)][string] $Word,
        [AllowNull()][object] $CommentText,
        [switch] $Quiet
    )

    if ($RequireClean) { Assert-CleanTree -Directory (Get-LocalizationDir) }
    $target = Get-LangPath $LangKey
    $before = Get-ResxDocument -LiteralPath $target
    $file = $script:LangFiles[$LangKey]

    if (-not $before.Index.ContainsKey($KeyName)) { throw "$file has no key '$KeyName'." }
    if ($before.Duplicates -contains $KeyName) { throw "$file holds '$KeyName' more than once. Fix that by hand first." }
    $entry = $before.Index[$KeyName]

    # An empty <comment></comment> carries nothing, so it is safe to rewrite. A real note is not.
    if (-not [string]::IsNullOrWhiteSpace($entry.Comment) -and -not (Test-MarkerComment $entry.Comment $Word)) {
        throw "'$KeyName' in $file already carries a comment that is not a '$Word' marker: [$(Format-Inline $entry.Comment 120)]. Refusing to overwrite it."
    }
    if ($entry.Comment -ceq $CommentText) {
        if (-not $Quiet) {
            Write-Output "$file : '$KeyName' already reads $(if ($null -eq $CommentText) { 'unmarked' } else { "[$CommentText]" }), nothing written."
        }
        return
    }
    if ($entry.ValueStart -lt 0) { throw "'$KeyName' in $file has no <value> element." }

    if ($null -eq $CommentText) {
        $newContent = $before.Content.Substring(0, $entry.AfterValue) + $before.Content.Substring($entry.CommentEnd + 10)
    } elseif ($entry.CommentStart -ge 0) {
        $newContent = $before.Content.Substring(0, $entry.CommentStart) + (ConvertTo-XmlText $CommentText) + $before.Content.Substring($entry.CommentEnd)
    } else {
        $element = "$($before.Newline)$($entry.Indent)<comment>$(ConvertTo-XmlText $CommentText)</comment>"
        $newContent = $before.Content.Substring(0, $entry.AfterValue) + $element + $before.Content.Substring($entry.AfterValue)
    }

    Set-ResxContentVerified -LiteralPath $target -NewContent $newContent -Verify {
        param($after)
        Assert-UnchangedExcept -Before $before -After $after -CommentChanged @($KeyName)
        if ($after.Index[$KeyName].Comment -cne $CommentText) {
            throw "'$KeyName' comment did not round-trip. Wanted [$CommentText], got [$($after.Index[$KeyName].Comment)]."
        }
        if ($after.Duplicates.Count -ne 0) { throw "the write introduced duplicate keys: $($after.Duplicates -join ', ')." }
    }

    if (-not $Quiet) {
        if ($null -eq $CommentText) { Write-Output "$file : '$KeyName' is no longer marked $Word." }
        else { Write-Output "$file : '$KeyName' marked [$CommentText]." }
    }
}

# Reads the comment-based help above rather than repeating it, so the two cannot drift apart.
function Invoke-Help {
    $help = Get-Help $PSCommandPath
    Write-Output ''
    Write-Output $help.Synopsis.Trim()
    Write-Output ''
    foreach ($block in $help.Description) { Write-Output $block.Text }
    if ($help.Examples) {
        Write-Output 'Examples:'
        foreach ($example in $help.Examples.Example) { Write-Output "  $($example.Code.Trim())" }
    }
    Write-Output ''
    Write-Output "Full parameter reference: Get-Help '$PSCommandPath' -Full"
    Write-Output ''
    return
}

function Invoke-MarkInvariant {
    if (-not $Key) { throw 'mark-invariant needs -Key.' }
    $text = if ($Reason) { "invariant, $Reason" } else { 'invariant' }
    Set-CommentMarker -LangKey 'default' -KeyName $Key -Word 'invariant' -CommentText $text
    return
}

function Invoke-UnmarkInvariant {
    if (-not $Key) { throw 'unmark-invariant needs -Key.' }
    Set-CommentMarker -LangKey 'default' -KeyName $Key -Word 'invariant' -CommentText $null
    return
}

function Resolve-ReviewLang {
    param([Parameter(Mandatory)][string] $Command)
    if (-not $Lang) { throw "$Command needs -Lang." }
    $langKey = Resolve-Lang $Lang
    if ($langKey -eq 'default') {
        throw "$Command does not apply to Strings.resx. The source file uses 'invariant' for a value no language translates, so use mark-invariant instead."
    }
    return $langKey
}

function Invoke-MarkReviewed {
    if (-not $Key) { throw 'mark-reviewed needs -Key.' }
    $langKey = Resolve-ReviewLang 'mark-reviewed'
    $text = if ($Reason) { "reviewed, $Reason" } else { 'reviewed' }
    Set-CommentMarker -LangKey $langKey -KeyName $Key -Word 'reviewed' -CommentText $text
    return
}

function Invoke-UnmarkReviewed {
    if (-not $Key) { throw 'unmark-reviewed needs -Key.' }
    $langKey = Resolve-ReviewLang 'unmark-reviewed'
    Set-CommentMarker -LangKey $langKey -KeyName $Key -Word 'reviewed' -CommentText $null
    return
}

# XLIFF resets a target's state when its source changes, and the same holds here: once the English wording
# moves, every "a human confirmed this" mark on that key is describing text that no longer exists.
function Clear-ReviewedForKey {
    param([Parameter(Mandatory)][string] $KeyName)

    $cleared = [System.Collections.Generic.List[string]]::new()
    foreach ($langKey in $script:LangFiles.Keys) {
        if ($langKey -eq 'default') { continue }
        $doc = Get-LangDocument $langKey
        if (-not $doc.Index.ContainsKey($KeyName)) { continue }
        if (-not $doc.Index[$KeyName].Reviewed) { continue }
        Set-CommentMarker -LangKey $langKey -KeyName $KeyName -Word 'reviewed' -CommentText $null -Quiet
        $cleared.Add($langKey)
    }
    return $cleared
}

function Invoke-Get {
    if (-not $Key) { throw 'get needs -Key.' }

    $rows = foreach ($langKey in $script:LangFiles.Keys) {
        $doc = Get-LangDocument $langKey
        [pscustomobject]@{
            Lang    = $langKey
            File    = $script:LangFiles[$langKey]
            Present = $doc.Index.ContainsKey($Key)
            Value   = if ($doc.Index.ContainsKey($Key)) { Get-NormalizedValue $doc.Index[$Key].Value } else { $null }
        }
    }
    $rows = @($rows)

    if ($Format -eq 'json') {
        Write-Json ([pscustomobject]@{ Key = $Key; Languages = $rows })
        return
    }

    if (-not ($rows | Where-Object { $_.Present })) {
        Write-Output "No file holds a key named '$Key'."
        $script:ExitCode = $script:ExitFindings
        return
    }

    Write-Output "$Key"
    $width = ($script:LangFiles.Keys | Measure-Object -Property Length -Maximum).Maximum
    foreach ($row in $rows) {
        $label = '  ' + $row.Lang.PadRight($width) + '  '
        if (-not $row.Present) { Write-Output "$label(missing)"; continue }
        Write-Output ($label + (Format-Indented -Value $row.Value -Indent $label.Length))
    }
    return
}

function Invoke-Find {
    if (-not $Text) { throw 'find needs -Text.' }

    $langKeys = @(if ($Lang) { Resolve-Lang $Lang } else { $script:LangFiles.Keys })
    $options = if ($CaseSensitive) { [System.Text.RegularExpressions.RegexOptions]::None } else { [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }
    $pattern = if ($Regex) { $Text } else { [regex]::Escape($Text) }
    $matcher = [regex]::new($pattern, $options)

    $hits = [System.Collections.Generic.List[object]]::new()
    foreach ($langKey in $langKeys) {
        $doc = Get-LangDocument $langKey
        foreach ($entry in $doc.Entries) {
            if ($null -eq $entry.Value) { continue }
            if ($matcher.IsMatch($entry.Value)) {
                $hits.Add([pscustomobject]@{
                        Lang  = $langKey
                        Key   = $entry.Name
                        Value = Get-NormalizedValue $entry.Value
                    })
            }
        }
    }

    if ($Format -eq 'json') {
        Write-Json ([pscustomobject]@{ Text = $Text; Regex = [bool]$Regex; Matches = @($hits) })
        return
    }

    if ($hits.Count -eq 0) { Write-Output "No value matches '$Text'."; return }

    $langWidth = ($hits | ForEach-Object { $_.Lang.Length } | Measure-Object -Maximum).Maximum
    $keyWidth = [Math]::Min(46, ($hits | ForEach-Object { $_.Key.Length } | Measure-Object -Maximum).Maximum)
    foreach ($hit in $hits) {
        Write-Output ("{0}  {1}  {2}" -f $hit.Lang.PadRight($langWidth), $hit.Key.PadRight($keyWidth), (Format-Inline $hit.Value 110))
    }
    Write-Output ''
    $groups = @($hits | Group-Object Lang)
    $perLang = $groups | ForEach-Object { "$($_.Name)=$($_.Count)" }
    Write-Output "$($hits.Count) value(s) match across $($groups.Count) language(s): $($perLang -join ' ')"
    return
}

# Every key that at least one satellite leaves at the English text, with the languages split into those that
# match English and those that translate it. All nine satellites are always inspected, whatever -Lang asks
# for, because "translated in 8 others" is the whole point and it cannot be known from one file.
function Get-UntranslatedKeys {
    param([switch] $WithInvariant, [switch] $WithReviewed)

    $english = Get-LangDocument 'default'
    $satellites = @($script:LangFiles.Keys | Where-Object { $_ -ne 'default' })
    $docs = [ordered]@{}
    foreach ($langKey in $satellites) { $docs[$langKey] = Get-LangDocument $langKey }

    $keys = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $english.Entries) {
        if ($entry.Invariant -and -not $WithInvariant) { continue }

        $matching = [System.Collections.Generic.List[string]]::new()
        $reviewed = [System.Collections.Generic.List[string]]::new()
        $translated = [System.Collections.Generic.List[string]]::new()
        foreach ($langKey in $satellites) {
            $doc = $docs[$langKey]
            if (-not $doc.Index.ContainsKey($entry.Name)) { continue }
            if ($doc.Index[$entry.Name].Value -cne $entry.Value) { $translated.Add($langKey); continue }
            if ($doc.Index[$entry.Name].Reviewed) { $reviewed.Add($langKey) }
            if ($doc.Index[$entry.Name].Reviewed -and -not $WithReviewed) { continue }
            $matching.Add($langKey)
        }
        if ($matching.Count -eq 0) { continue }

        $keys.Add([pscustomobject]@{
                Key        = $entry.Name
                Value      = $entry.Value
                Matching   = @($matching)
                Reviewed   = @($reviewed)
                Translated = @($translated)
                Everywhere = $translated.Count -eq 0
                Invariant  = $entry.Invariant
            })
    }
    return $keys
}

# Key-language pairs a reviewed marker is hiding, counted independently so a marker cannot quietly swallow
# a real gap.
function Get-ReviewedHiddenCount {
    $english = Get-LangDocument 'default'
    $count = 0
    foreach ($langKey in $script:LangFiles.Keys) {
        if ($langKey -eq 'default') { continue }
        $doc = Get-LangDocument $langKey
        foreach ($entry in $doc.Entries) {
            if (-not $entry.Reviewed) { continue }
            if (-not $english.Index.ContainsKey($entry.Name)) { continue }
            if ($english.Index[$entry.Name].Value -ceq $entry.Value) { $count++ }
        }
    }
    return $count
}

function Invoke-Untranslated {
    $only = if ($Lang) { Resolve-Lang $Lang } else { $null }
    if ($only -eq 'default') {
        throw "'default' is the English source, so it is never untranslated. Pass a satellite language or no -Lang at all."
    }

    $english = Get-LangDocument 'default'
    $total = $english.Entries.Count
    $satellites = @($script:LangFiles.Keys | Where-Object { $_ -ne 'default' })
    $all = @(Get-UntranslatedKeys -WithInvariant:$IncludeInvariant -WithReviewed:$IncludeReviewed)
    $invariantHidden = @($english.Entries | Where-Object { $_.Invariant }).Count
    $reviewedHidden = Get-ReviewedHiddenCount

    $gaps = @($all | Where-Object { -not $_.Everywhere })
    $everywhere = @($all | Where-Object { $_.Everywhere })
    if ($only) { $gaps = @($gaps | Where-Object { $_.Matching -contains $only }) }

    $summary = foreach ($langKey in $(if ($only) { @($only) } else { $satellites })) {
        $gapCount = @($gaps | Where-Object { $_.Matching -contains $langKey }).Count
        $everywhereCount = @($everywhere | Where-Object { $_.Matching -contains $langKey }).Count
        [pscustomobject]@{
            Lang              = $langKey
            Gaps              = $gapCount
            IdenticalNoGap    = $everywhereCount
            MatchingEnglish   = $gapCount + $everywhereCount
            Total             = $total
            Coverage          = [Math]::Round((($total - $gapCount - $everywhereCount) / $total) * 100, 1)
        }
    }

    if ($Format -eq 'json') {
        Write-Json ([pscustomobject]@{
                Summary             = @($summary)
                TranslatedElsewhere = $gaps
                IdenticalEverywhere = $everywhere
                InvariantHidden     = $(if ($IncludeInvariant) { 0 } else { $invariantHidden })
                ReviewedHidden      = $(if ($IncludeReviewed) { 0 } else { $reviewedHidden })
            })
        return
    }

    if ($gaps.Count -gt 0) {
        Write-Output 'Still English here, translated in other languages. This is the actionable list.'
        $keyWidth = [Math]::Min(50, ($gaps | ForEach-Object { $_.Key.Length } | Measure-Object -Maximum).Maximum)
        foreach ($item in $gaps) {
            $langs = if ($only) { @($only) } else { $item.Matching }
            Write-Output ("  {0}  {1}" -f $item.Key.PadRight($keyWidth), "$($langs -join ' ') still English, translated in $($item.Translated -join ' ')")
        }
    } else {
        Write-Output 'Still English here, translated in other languages: none.'
    }

    if ($everywhere.Count -gt 0) {
        Write-Output ''
        Write-Output "Identical in all ten files ($($everywhere.Count)). Likely intentional, consider mark-invariant."
        $keyWidth = [Math]::Min(50, ($everywhere | ForEach-Object { $_.Key.Length } | Measure-Object -Maximum).Maximum)
        foreach ($item in $everywhere) {
            $flag = if ($item.Invariant) { ' [invariant]' } else { '' }
            Write-Output ("  {0}  {1}{2}" -f $item.Key.PadRight($keyWidth), (Format-Inline $item.Value 70), $flag)
        }
    }

    Write-Output ''
    foreach ($item in $summary) {
        Write-Output ("{0,-8} {1,4} to translate, {2,3} identical everywhere, {3}% translated" -f $item.Lang, $item.Gaps, $item.IdenticalNoGap, $item.Coverage)
    }
    $notes = [System.Collections.Generic.List[string]]::new()
    if ($invariantHidden -gt 0 -and -not $IncludeInvariant) {
        $notes.Add("$invariantHidden key(s) marked invariant in Strings.resx are hidden (-IncludeInvariant shows them).")
    }
    if ($reviewedHidden -gt 0 -and -not $IncludeReviewed) {
        $notes.Add("$reviewedHidden key-language pair(s) marked reviewed in a satellite are hidden (-IncludeReviewed shows them).")
    }
    if ($notes.Count -gt 0) {
        Write-Output ''
        foreach ($note in $notes) { Write-Output $note }
    }
    return
}

function Get-ValidationFindings {
    $findings = [System.Collections.Generic.List[object]]::new()
    $docs = [ordered]@{}

    foreach ($langKey in $script:LangFiles.Keys) {
        try {
            $docs[$langKey] = Get-LangDocument $langKey
        } catch {
            $findings.Add([pscustomobject]@{
                    Severity = 'error'; Lang = $langKey; Key = ''
                    Issue    = 'unreadable'; Detail = $_.Exception.Message
                })
        }
    }

    if (-not $docs.Contains('default')) {
        $findings.Add([pscustomobject]@{
                Severity = 'error'; Lang = 'default'; Key = ''
                Issue    = 'no-english'; Detail = 'Strings.resx could not be read, so nothing can be compared against it.'
            })
        return $findings
    }

    $english = $docs['default']
    $englishNames = [System.Collections.Generic.HashSet[string]]::new([string[]]$english.Names, [System.StringComparer]::Ordinal)

    foreach ($langKey in $docs.Keys) {
        $doc = $docs[$langKey]

        foreach ($name in $doc.Duplicates) {
            $count = @($doc.Names | Where-Object { $_ -ceq $name }).Count
            $findings.Add([pscustomobject]@{
                    Severity = 'error'; Lang = $langKey; Key = $name
                    Issue    = 'duplicate-key'; Detail = "appears $count times in the file"
                })
        }

        foreach ($entry in $doc.Entries) {
            if ($null -eq $entry.Value) {
                $findings.Add([pscustomobject]@{
                        Severity = 'error'; Lang = $langKey; Key = $entry.Name
                        Issue    = 'no-value'; Detail = 'the <data> element has no <value> child'
                    })
            } elseif ([string]::IsNullOrWhiteSpace($entry.Value)) {
                $findings.Add([pscustomobject]@{
                        Severity = 'error'; Lang = $langKey; Key = $entry.Name
                        Issue    = 'empty-value'; Detail = "the value is empty or whitespace only ($($entry.Value.Length) chars)"
                    })
            }
        }

        if ($langKey -eq 'default') { continue }

        foreach ($name in $english.Names) {
            if (-not $doc.Index.ContainsKey($name)) {
                $findings.Add([pscustomobject]@{
                        Severity = 'error'; Lang = $langKey; Key = $name
                        Issue    = 'missing-key'; Detail = 'present in Strings.resx, absent here'
                    })
            }
        }
        foreach ($name in $doc.Names) {
            if (-not $englishNames.Contains($name)) {
                $findings.Add([pscustomobject]@{
                        Severity = 'error'; Lang = $langKey; Key = $name
                        Issue    = 'extra-key'; Detail = 'present here, absent from Strings.resx'
                    })
            }
        }

        foreach ($name in $english.Names) {
            if (-not $doc.Index.ContainsKey($name)) { continue }
            $expected = Get-PlaceholderIndices $english.Index[$name].Value
            if ($expected.Count -eq 0) { continue }
            $actual = Get-PlaceholderIndices $doc.Index[$name].Value
            if (($expected -join ',') -ne ($actual -join ',')) {
                $findings.Add([pscustomobject]@{
                        Severity = 'error'; Lang = $langKey; Key = $name
                        Issue    = 'placeholder-mismatch'
                        Detail   = ("en={{{0}}} {1}={{{2}}}, string.Format throws on this" -f ($expected -join ','), $langKey, ($actual -join ','))
                    })
            }
        }
    }

    return $findings
}

function Invoke-Validate {
    $findings = @(Get-ValidationFindings)

    if ($Format -eq 'json') {
        Write-Json ([pscustomobject]@{ Findings = $findings; Count = $findings.Count })
        if ($findings.Count -gt 0) { $script:ExitCode = $script:ExitFindings }
        return
    }

    if ($findings.Count -eq 0) {
        $english = Get-LangDocument 'default'
        Write-Output "validate: clean. $($english.Entries.Count) keys, $($script:LangFiles.Count) files, no duplicates, no empty values, placeholders match."
        Write-Output "Note: a satellite holding the English text is not a validate finding. Use 'untranslated' for that."
        return
    }

    $langWidth = ($findings | ForEach-Object { $_.Lang.Length } | Measure-Object -Maximum).Maximum
    $issueWidth = ($findings | ForEach-Object { $_.Issue.Length } | Measure-Object -Maximum).Maximum
    foreach ($finding in $findings) {
        Write-Output ("{0}  {1}  {2}  {3}" -f $finding.Lang.PadRight($langWidth), $finding.Issue.PadRight($issueWidth), $finding.Key, $finding.Detail)
    }
    Write-Output ''
    Write-Output "validate: $($findings.Count) finding(s)."
    $script:ExitCode = $script:ExitFindings
    return
}

# The exports are Palworld's own game assets and are Pocketpair's copyrighted work, so they can never be
# committed here and each developer extracts their own. Resolution order is -ExportsPath, then
# $env:PALWORLD_EXPORTS, then a sibling 'Exports' folder next to the repo. Returns null when none exists,
# which makes catalog-check report itself skipped rather than fail, since it is the only command needing them.
function Get-ExportRoot {
    if ($ExportsPath) {
        if (-not (Test-Path -LiteralPath $ExportsPath -PathType Container)) { throw "-ExportsPath '$ExportsPath' is not a folder." }
        return (Resolve-Path -LiteralPath $ExportsPath).ProviderPath
    }
    if ($env:PALWORLD_EXPORTS) {
        if (-not (Test-Path -LiteralPath $env:PALWORLD_EXPORTS -PathType Container)) {
            throw "PALWORLD_EXPORTS points at '$($env:PALWORLD_EXPORTS)', which is not a folder."
        }
        return (Resolve-Path -LiteralPath $env:PALWORLD_EXPORTS).ProviderPath
    }
    $candidate = Join-Path $PSScriptRoot '..' '..' 'Exports'
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { return $null }
    return (Resolve-Path -LiteralPath $candidate).ProviderPath
}

function Get-ExportPath {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $LangKey)

    # Japanese is the game's source language, so it has no L10N folder. Its text is the base table.
    if ($LangKey -eq 'ja') { return Join-Path $Root 'Pal/Content/Pal/DataTable/Text/DT_UI_Common_Text.json' }
    $culture = if ($LangKey -eq 'default') { 'en' } else { $LangKey }
    return Join-Path $Root "Pal/Content/L10N/$culture/Pal/DataTable/Text/DT_UI_Common_Text_Common.json"
}

function Get-WorldSettingRows {
    param([Parameter(Mandatory)][string] $LiteralPath)

    $json = Get-Content -Raw -LiteralPath $LiteralPath | ConvertFrom-Json
    $table = if ($json -is [array]) { $json[0] } else { $json }
    $rows = @{}
    foreach ($property in $table.Rows.PSObject.Properties) {
        if ($property.Name -like '*_TextData') { continue }
        if ($property.Name -notlike 'WORLDSSETTING_*') { continue }
        $textData = $property.Value.PSObject.Properties['TextData']
        if (-not $textData) { continue }
        $source = $textData.Value.PSObject.Properties['SourceString']
        if (-not $source) { continue }
        $rows[$property.Name] = $source.Value
    }
    return $rows
}

function Get-CatalogCheckResult {
    $root = Get-ExportRoot
    if (-not $root) {
        return [pscustomobject]@{
            Available = $false
            Note      = "No Exports folder next to the repo. Pass -ExportsPath to point at the FModel extraction."
            Mapped    = 0; Labels = 0; Mapping = @{}; Cultures = @(); Mismatches = @(); Reworded = @(); Skipped = @()
        }
    }

    $englishExport = Get-ExportPath -Root $root -LangKey 'default'
    if (-not (Test-Path -LiteralPath $englishExport -PathType Leaf)) {
        return [pscustomobject]@{
            Available = $false
            Note      = "The English export is missing at '$englishExport', so no mapping can be derived."
            Mapped    = 0; Labels = 0; Mapping = @{}; Cultures = @(); Mismatches = @(); Reworded = @(); Skipped = @()
        }
    }

    # Load every culture's rows up front. The English ones derive the mapping, the rest both verify it and
    # provide the cross-culture check further down.
    $cultureRows = [ordered]@{}
    $cultureIndex = [ordered]@{}
    $skipped = [System.Collections.Generic.List[object]]::new()
    foreach ($langKey in $script:LangFiles.Keys) {
        $exportPath = Get-ExportPath -Root $root -LangKey $langKey
        if (-not (Test-Path -LiteralPath $exportPath -PathType Leaf)) {
            $skipped.Add([pscustomobject]@{ Lang = $langKey; Reason = "no export at $exportPath" })
            continue
        }
        $cultureRows[$langKey] = Get-WorldSettingRows -LiteralPath $exportPath
        $cultureIndex[$langKey] = Get-LangDocument $langKey
    }

    # Invert a culture's rows into text -> row name. A source string used by two rows cannot be attributed,
    # so drop it rather than guess.
    function Get-InvertedRows {
        param([Parameter(Mandatory)][hashtable] $Rows, [switch] $IgnoreCase)

        $comparer = if ($IgnoreCase) { [System.StringComparer]::OrdinalIgnoreCase } else { [System.StringComparer]::Ordinal }
        $inverted = [System.Collections.Generic.Dictionary[string, string]]::new($comparer)
        $ambiguous = [System.Collections.Generic.HashSet[string]]::new($comparer)
        foreach ($rowName in $Rows.Keys) {
            $sourceText = $Rows[$rowName]
            if ([string]::IsNullOrEmpty($sourceText)) { continue }
            if ($inverted.ContainsKey($sourceText)) { [void]$ambiguous.Add($sourceText); continue }
            $inverted[$sourceText] = $rowName
        }
        foreach ($text in $ambiguous) { [void]$inverted.Remove($text) }
        return $inverted
    }

    # Derive the label -> row mapping by inverting the English export rather than hand-building a table.
    # Ordinal, so a label that only matches once you ignore case counts as reworded rather than mapped.
    $englishRows = Get-WorldSettingRows -LiteralPath $englishExport
    $byText = Get-InvertedRows -Rows $englishRows
    $byTextLoose = Get-InvertedRows -Rows $englishRows -IgnoreCase

    $english = Get-LangDocument 'default'
    $labels = @($english.Entries | Where-Object { $_.Name -like 'Cat_*_Label' })
    $mapping = [ordered]@{}
    $unmapped = [System.Collections.Generic.List[object]]::new()
    foreach ($label in $labels) {
        $value = Get-NormalizedValue $label.Value
        if ($byText.ContainsKey($value)) { $mapping[$label.Name] = $byText[$value]; continue }
        $unmapped.Add([pscustomobject]@{ Key = $label.Name; Value = $value })
    }

    # An English label that was reworded drops out of the mapping silently, which is the one failure this
    # check exists to catch. Recover it from the satellites: if two or more cultures still hold the game's
    # own wording for the same row, the English side is what moved.
    $reworded = [System.Collections.Generic.List[object]]::new()
    $inverted = [ordered]@{}
    foreach ($langKey in $cultureRows.Keys) {
        if ($langKey -eq 'default') { continue }
        $inverted[$langKey] = Get-InvertedRows -Rows $cultureRows[$langKey]
    }
    foreach ($item in $unmapped) {
        $votes = @{}
        foreach ($langKey in $inverted.Keys) {
            $doc = $cultureIndex[$langKey]
            if (-not $doc.Index.ContainsKey($item.Key)) { continue }
            $satelliteValue = Get-NormalizedValue $doc.Index[$item.Key].Value
            if (-not $inverted[$langKey].ContainsKey($satelliteValue)) { continue }
            $rowName = $inverted[$langKey][$satelliteValue]
            if (-not $votes.ContainsKey($rowName)) { $votes[$rowName] = [System.Collections.Generic.List[string]]::new() }
            $votes[$rowName].Add($langKey)
        }
        foreach ($rowName in $votes.Keys) {
            if ($votes[$rowName].Count -lt 2) { continue }
            $reworded.Add([pscustomobject]@{
                    Key      = $item.Key
                    Row      = $rowName
                    Game     = $englishRows[$rowName]
                    Ours     = $item.Value
                    Agreeing = @($votes[$rowName])
                    CaseOnly = $byTextLoose.ContainsKey($item.Value) -and $byTextLoose[$item.Value] -eq $rowName
                })
        }
    }

    $cultures = [System.Collections.Generic.List[object]]::new()
    $mismatches = [System.Collections.Generic.List[object]]::new()

    foreach ($langKey in $cultureRows.Keys) {
        $rows = $cultureRows[$langKey]
        $doc = $cultureIndex[$langKey]
        $matched = 0
        foreach ($catKey in $mapping.Keys) {
            $rowName = $mapping[$catKey]
            if (-not $rows.ContainsKey($rowName)) {
                $mismatches.Add([pscustomobject]@{
                        Lang = $langKey; Key = $catKey; Row = $rowName
                        Expected = '(row missing from this export)'; Actual = ''
                    })
                continue
            }
            $expected = Get-NormalizedValue $rows[$rowName]
            $actual = if ($doc.Index.ContainsKey($catKey)) { Get-NormalizedValue $doc.Index[$catKey].Value } else { $null }
            if ($actual -ceq $expected) { $matched++; continue }
            $mismatches.Add([pscustomobject]@{
                    Lang = $langKey; Key = $catKey; Row = $rowName
                    Expected = $expected; Actual = $actual
                })
        }
        $cultures.Add([pscustomobject]@{ Lang = $langKey; Matched = $matched; Mapped = $mapping.Count })
    }

    return [pscustomobject]@{
        Available  = $true
        Note       = ''
        Mapped     = $mapping.Count
        Labels     = $labels.Count
        Mapping    = $mapping
        Cultures   = @($cultures)
        Mismatches = @($mismatches)
        Reworded   = @($reworded)
        Skipped    = @($skipped)
    }
}

function Invoke-CatalogCheck {
    $result = Get-CatalogCheckResult

    if ($Format -eq 'json') {
        Write-Json $result
        if (-not $result.Available -or $result.Mismatches.Count -gt 0) { $script:ExitCode = $script:ExitFindings }
        return
    }

    if (-not $result.Available) {
        Write-Output "catalog-check: skipped. $($result.Note)"
        $script:ExitCode = $script:ExitFindings
        return
    }

    Write-Output "Auto-mapped $($result.Mapped) of $($result.Labels) Cat_*_Label keys to WORLDSSETTING rows by exact English text."
    Write-Output 'The rest are our own admin labels or deliberately reworded, and are out of scope.'
    Write-Output ''
    foreach ($culture in $result.Cultures) {
        $flag = if ($culture.Matched -eq $culture.Mapped) { 'ok' } else { 'DRIFT' }
        Write-Output ("  {0,-8} {1,3} of {2,-3} {3}" -f $culture.Lang, $culture.Matched, $culture.Mapped, $flag)
    }
    foreach ($skip in $result.Skipped) { Write-Warning "catalog-check skipped $($skip.Lang): $($skip.Reason)" }

    # Off by default. Shortening the game's caveats and title-casing our labels is deliberate, so listing
    # every one of them on each run would bury the drift this command is actually looking for.
    if ($result.Reworded.Count -gt 0 -and -not $ShowReworded) {
        Write-Output ''
        Write-Output "$($result.Reworded.Count) more English label(s) read differently from Palworld while their translations match it. That rewording is deliberate. Pass -ShowReworded to list them."
    } elseif ($result.Reworded.Count -gt 0) {
        Write-Output ''
        Write-Output "$($result.Reworded.Count) English label(s) differ from Palworld while their translations still match it:"
        foreach ($item in $result.Reworded) {
            $note = if ($item.CaseOnly) { ' (casing only)' } else { '' }
            Write-Output "  $($item.Key)$note, matched via $($item.Agreeing -join ' ')"
            Write-Output "    game: $(Format-Inline $item.Game 120)"
            Write-Output "    ours: $(Format-Inline $item.Ours 120)"
        }
    }

    if ($result.Mismatches.Count -gt 0) {
        Write-Output ''
        foreach ($mismatch in $result.Mismatches) {
            Write-Output "  $($mismatch.Lang) $($mismatch.Key) ($($mismatch.Row))"
            Write-Output "    game:    $(Format-Inline $mismatch.Expected 120)"
            Write-Output "    ours:    $(Format-Inline $mismatch.Actual 120)"
        }
        Write-Output ''
        Write-Output "catalog-check: $($result.Mismatches.Count) label(s) drifted from Palworld's own wording."
        $script:ExitCode = $script:ExitFindings
        return
    }

    Write-Output ''
    Write-Output "catalog-check: clean, $($result.Cultures.Count) x $($result.Mapped) exact matches."
    return
}

function Invoke-Audit {
    $findings = @(Get-ValidationFindings)
    $english = Get-LangDocument 'default'
    $total = $english.Entries.Count
    $satellites = @($script:LangFiles.Keys | Where-Object { $_ -ne 'default' })
    $untranslated = @(Get-UntranslatedKeys)
    $invariantHidden = @($english.Entries | Where-Object { $_.Invariant }).Count

    $coverage = foreach ($langKey in $satellites) {
        $gaps = @($untranslated | Where-Object { -not $_.Everywhere -and $_.Matching -contains $langKey }).Count
        $everywhere = @($untranslated | Where-Object { $_.Everywhere -and $_.Matching -contains $langKey }).Count
        [pscustomobject]@{
            Lang         = $langKey
            Untranslated = $gaps
            Everywhere   = $everywhere
            Total        = $total
            Coverage     = [Math]::Round((($total - $gaps - $everywhere) / $total) * 100, 1)
        }
    }
    $catalog = Get-CatalogCheckResult

    if ($Format -eq 'json') {
        Write-Json ([pscustomobject]@{
                Keys            = $total
                Files           = $script:LangFiles.Count
                Findings        = @($findings)
                Coverage        = @($coverage)
                InvariantMarked = $invariantHidden
                CatalogCheck    = $catalog
            })
        return
    }

    Write-Output "$total keys across $($script:LangFiles.Count) files."
    Write-Output ''
    if ($findings.Count -eq 0) {
        Write-Output 'validate         clean'
    } else {
        Write-Output "validate         $($findings.Count) finding(s):"
        foreach ($finding in $findings) { Write-Output "  $($finding.Lang) $($finding.Issue) $($finding.Key) - $($finding.Detail)" }
    }
    Write-Output ''
    Write-Output "coverage         ($invariantHidden key(s) marked invariant are excluded)"
    foreach ($item in $coverage) {
        Write-Output ("  {0,-8} {1,5}% translated, {2} to translate, {3} identical everywhere" -f $item.Lang, $item.Coverage, $item.Untranslated, $item.Everywhere)
    }
    Write-Output ''
    if (-not $catalog.Available) {
        Write-Output "catalog-check    skipped, $($catalog.Note)"
    } elseif ($catalog.Mismatches.Count -eq 0) {
        Write-Output "catalog-check    clean, $($catalog.Cultures.Count) x $($catalog.Mapped) exact matches against Palworld's L10N"
    } else {
        Write-Output "catalog-check    $($catalog.Mismatches.Count) label(s) drifted:"
        foreach ($mismatch in $catalog.Mismatches) { Write-Output "  $($mismatch.Lang) $($mismatch.Key) ($($mismatch.Row))" }
    }
    foreach ($skip in $catalog.Skipped) { Write-Output "                 skipped $($skip.Lang), $($skip.Reason)" }
    return
}

try {
    switch ($Command) {
        'add' { Invoke-Add }
        'set' { Invoke-Set }
        'get' { Invoke-Get }
        'find' { Invoke-Find }
        'untranslated' { Invoke-Untranslated }
        'validate' { Invoke-Validate }
        'audit' { Invoke-Audit }
        'catalog-check' { Invoke-CatalogCheck }
        'mark-invariant' { Invoke-MarkInvariant }
        'unmark-invariant' { Invoke-UnmarkInvariant }
        'mark-reviewed' { Invoke-MarkReviewed }
        'unmark-reviewed' { Invoke-UnmarkReviewed }
        'help' { Invoke-Help }
    }
    exit $script:ExitCode
} catch {
    [Console]::Error.WriteLine("localization.ps1: $($_.Exception.Message)")
    if ($VerbosePreference -ne 'SilentlyContinue') { [Console]::Error.WriteLine($_.ScriptStackTrace) }
    if ($_.Exception.Message -like '*was restored unchanged*') { exit $script:ExitRolledBack }
    exit $script:ExitUsage
}

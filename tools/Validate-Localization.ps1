$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot "src"

# Locate the translation catalog regardless of its project subdirectory.
$catalogFiles = @(
    Get-ChildItem $sourceRoot -Filter "Translations.cs" -File -Recurse |
        Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
        }
)

if ($catalogFiles.Count -ne 1) {
    throw "Expected exactly one Translations.cs under src, but found $($catalogFiles.Count)."
}

$catalogPath = $catalogFiles[0].FullName
$catalogText = Get-Content $catalogPath -Raw -Encoding UTF8
$entryPattern = '(?m)^\s*\["(?<key>(?:\\.|[^"\\])*)"\]\s*=\s*new\[\]\s*\{(?<values>.*?)\},\s*$'
$valuePattern = '"(?<value>(?:\\.|[^"\\])*)"'
$catalog = @{}
$validationErrors = [System.Collections.Generic.List[string]]::new()

foreach ($entry in [regex]::Matches($catalogText, $entryPattern)) {
    $key = $entry.Groups['key'].Value
    if ($catalog.ContainsKey($key)) {
        $validationErrors.Add("Duplicate key: " + $key)
        continue
    }

    $values = @(
        [regex]::Matches($entry.Groups['values'].Value, $valuePattern) |
            ForEach-Object { $_.Groups['value'].Value }
    )
    if ($values.Count -ne 10) {
        $validationErrors.Add($key + " has " + $values.Count + " translations; expected 10.")
        continue
    }

    if ($values | Where-Object { [string]::IsNullOrWhiteSpace($_) }) {
        $validationErrors.Add($key + " contains an empty translation.")
    }

    $expected = @(
        [regex]::Matches($values[0], '(?<!\{)\{(?<index>\d+)(?:[^}]*)\}(?!\})') |
            ForEach-Object { $_.Groups['index'].Value } |
            Sort-Object -Unique
    )
    for ($i = 1; $i -lt $values.Count; $i++) {
        $actual = @(
            [regex]::Matches($values[$i], '(?<!\{)\{(?<index>\d+)(?:[^}]*)\}(?!\})') |
                ForEach-Object { $_.Groups['index'].Value } |
                Sort-Object -Unique
        )
        if (($expected -join ",") -ne ($actual -join ",")) {
            $validationErrors.Add($key + " has mismatched placeholders in language column " + $i + ".")
        }
    }

    $catalog[$key] = $true
}

$sourceFiles = Get-ChildItem $sourceRoot -Filter "*.cs" -File -Recurse |
    Where-Object {
        $_.Name -ne "Translations.cs" -and
        $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
    }
$referenced = @{}
$keyPattern = '"(?<key>(?:common|error|format|http|items|language|long|main|model|overlay|profiles|replay|report|settings|startup|status|tracker|updater)\.[a-z0-9_.]+)"'
$localizationCallPattern = 'L\.(?:T|F|Big|Count)\s*\((?<args>[^;]*?)\)'
$inlinePattern = 'L\.(?:T|F|Big)\s*\(\s*"(?:\\.|[^"\\])*"\s*,\s*"(?:\\.|[^"\\])*"\s*,'
$directDialogPattern = 'MessageBox\.Show\s*\(\s*"(?:\\.|[^"\\]){2,}"'
$directNoticePattern = 'SetNotice\s*\(\s*"(?:\\.|[^"\\]){2,}"'
$directTextPattern = '\b(?:Text|Caption|ToolTipText|PlaceholderText|AccessibleName|AccessibleDescription)\s*=\s*"(?<text>[^"]{2,})"'
foreach ($file in $sourceFiles) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    foreach ($call in [regex]::Matches($text, $localizationCallPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
        foreach ($match in [regex]::Matches($call.Groups['args'].Value, $keyPattern)) {
            $referenced[$match.Groups['key'].Value] = $true
        }
    }
    if ($file.Name -eq "Lang.cs") {
        foreach ($match in [regex]::Matches($text, '"(?<key>(?:format|items|language)\.[a-z0-9_.]+)"')) {
            $referenced[$match.Groups['key'].Value] = $true
        }
    }
    if ($file.Name -eq "ReportWriter.cs") {
        foreach ($match in [regex]::Matches($text, '"(?<key>report\.obtained_[a-z0-9_.]+)"')) {
            $referenced[$match.Groups['key'].Value] = $true
        }
    }
    if ([regex]::IsMatch($text, $inlinePattern)) {
        $validationErrors.Add($file.Name + " contains an inline translation pair; use catalog keys.")
    }
    if ([regex]::IsMatch($text, $directDialogPattern)) {
        $validationErrors.Add($file.Name + " contains a hardcoded MessageBox string.")
    }
    if ([regex]::IsMatch($text, $directNoticePattern)) {
        $validationErrors.Add($file.Name + " contains a hardcoded notification string.")
    }
    foreach ($match in [regex]::Matches($text, $directTextPattern)) {
        $value = $match.Groups['text'].Value
        if ($value -notmatch '^[0-9]+$' -and $value -ne "i" -and $value -notmatch '^[^\w\s]$') {
            $validationErrors.Add($file.Name + " contains a hardcoded UI text: " + $value)
        }
    }
}

foreach ($key in $referenced.Keys) {
    if (-not $catalog.ContainsKey($key)) {
        $validationErrors.Add("Referenced key is missing from the catalog: " + $key)
    }
}

if ($validationErrors.Count -gt 0) {
    $validationErrors | ForEach-Object { Write-Output ("ERROR: " + $_) }
    exit 1
}

$unused = @($catalog.Keys | Where-Object { -not $referenced.ContainsKey($_) } | Sort-Object)
Write-Output "Localization validation passed: $($catalog.Count) keys, 10 language columns."
Write-Output "Unreferenced catalog entries: $($unused.Count)"
if ($unused.Count -gt 0) {
    $unused | ForEach-Object { Write-Output "  $_" }
}

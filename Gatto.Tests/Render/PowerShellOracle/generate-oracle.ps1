# Rebuilds oracle.json from corpus.txt with Windows PowerShell's own parser.
# Run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File generate-oracle.ps1
# Entries in corpus.txt are separated by a line that holds only four dashes.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$raw = [IO.File]::ReadAllText((Join-Path $here 'corpus.txt')) -replace "`r`n", "`n"
$entries = $raw -split "`n----`n" | ForEach-Object { $_.TrimEnd("`n") } | Where-Object { $_.Trim().Length -gt 0 }
$version = "$($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor)"

function Add-Token($list, $token) {
    $list.Add([ordered]@{ k = [string]$token.Kind; f = [string]$token.TokenFlags; s = $token.Extent.StartOffset; e = $token.Extent.EndOffset })
    if ($token.PSObject.Properties['NestedTokens'] -and $token.NestedTokens) {
        foreach ($nested in $token.NestedTokens) { Add-Token $list $nested }
    }
}

$lines = New-Object System.Collections.Generic.List[string]
foreach ($source in $entries) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($token in $tokens) { Add-Token $list $token }
    $entry = [ordered]@{ source = $source; parser = $version; tokens = $list }
    $lines.Add((ConvertTo-Json -InputObject $entry -Depth 6 -Compress))
}

$json = "[`n" + ($lines -join ",`n") + "`n]`n"
[IO.File]::WriteAllText((Join-Path $here 'oracle.json'), $json, (New-Object System.Text.UTF8Encoding $false))
"wrote $($lines.Count) entries with PowerShell $version"

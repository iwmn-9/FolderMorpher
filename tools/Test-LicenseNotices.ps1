param(
    [string]$DepsPath = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\FolderMorpher.deps.json'),
    [string]$NoticesPath = (Join-Path $PSScriptRoot '..\THIRD-PARTY-NOTICES.txt')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Use the built/published dependency manifest, not all restored build tools.
$dependencies = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
$notices = Get-Content -LiteralPath $NoticesPath -Raw
$listed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($match in [regex]::Matches($notices, '(?m)^Package: ([^\r\n]+)\r?$')) {
    [void]$listed.Add($match.Groups[1].Value.Trim())
}
$redistributed = @($dependencies.libraries.PSObject.Properties | Where-Object { $_.Value.type -in @('package', 'runtimepack') })
if ($redistributed.Count -eq 0) { throw 'No redistributed dependencies found; check the dependency manifest path.' }
$missing = @($redistributed | Where-Object { -not $listed.Contains($_.Name) })
if ($missing.Count -gt 0) {
    throw "Review the license and update THIRD-PARTY-NOTICES.txt for: $($missing.Name -join ', ')"
}
Write-Output "License notice coverage: $($redistributed.Count)/$($redistributed.Count) redistributed dependency versions."

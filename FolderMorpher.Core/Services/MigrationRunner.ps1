param([Parameter(Mandatory=$true)][string]$PlanPath,
      [Parameter(Mandatory=$true)][int]$Wave,
      [ValidateSet('BASELINE','DELTA','CUTOVER')][string]$Mode='BASELINE',
      [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
$units = @($plan.Units)
# Reject destination reparse ancestors, including an existing root itself.
foreach ($unit in $units) {
    $ancestor = [IO.Path]::GetFullPath($unit.Destination)
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Destination contains a reparse ancestor.' }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor.TrimEnd('\'))
    }
}
$expected = @{}
$selected = @{}
function Within([string]$parent, [string]$child) {
    return $child.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Children([string]$root, [string[]]$excludes=@()) {
    # Enumerate only ordinary directories; never follow junctions into another scope.
    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push($root)
    while ($stack.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $stack.Pop() -Force -ErrorAction Stop) {
            if (@($excludes | Where-Object { $item.FullName -eq $_ -or (Within $_ $item.FullName) }).Count) { continue }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in migration scope; review separately.' }
            $item
            if ($item.PSIsContainer) { $stack.Push($item.FullName) }
        }
    }
}
# Check builds the complete union before writing anything. Unreadable sources abort.
foreach ($unit in $units) {
    $source = Get-Item -LiteralPath $unit.Source -Force
    if (!$source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid source directory.' }
    $expected[$unit.Destination] = @{ Directory=$true }
    foreach ($item in Children $unit.Source @($unit.ExcludedSources)) {
        $excluded = $false
        foreach ($exclude in $unit.ExcludedSources) {
            if ($item.FullName -eq $exclude -or (Within $exclude $item.FullName)) { $excluded=$true; break }
        }
        if ($excluded) { continue }
        $relative = $item.FullName.Substring($unit.Source.TrimEnd('\').Length).TrimStart('\')
        $destination = Join-Path $unit.Destination $relative
        if ($expected.ContainsKey($destination)) {
            $old = $expected[$destination]
            if ([bool]$old.Directory -ne [bool]$item.PSIsContainer) { throw 'File/directory collision in migration plan.' }
            if (!$item.PSIsContainer -and ($old.Length -ne $item.Length -or
                (Get-FileHash -LiteralPath $old.Source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)) {
                throw 'Conflicting source files map to the same destination; resolve the collision before migration.'
            }
        }
        $expected[$destination] = @{Directory=[bool]$item.PSIsContainer; Source=$item.FullName; Length=$item.Length; Modified=$item.LastWriteTimeUtc.Ticks}
        if ($unit.WaveNumber -eq $Wave) { $selected[$destination]=$expected[$destination] }
    }
}
$active = @($units | Where-Object { $_.WaveNumber -eq $Wave })
if (!$active.Count) { throw 'Wave has no transfer units.' }
$deletions = @{}
if ($Mode -eq 'CUTOVER') {
    foreach ($unit in $active) {
        if (!(Test-Path -LiteralPath $unit.Destination)) { continue }
        foreach ($item in Children $unit.Destination) {
            # Never prune a branch owned by another transfer unit / wave.
            $protected = $false
            foreach ($other in $units) {
                if ($other.Destination -ne $unit.Destination -and (Within $unit.Destination $other.Destination) -and
                    ($item.FullName -eq $other.Destination -or (Within $other.Destination $item.FullName))) { $protected=$true; break }
            }
            if (!$protected -and !$expected.ContainsKey($item.FullName)) {
                $deletions[$item.FullName]=@{Directory=[bool]$item.PSIsContainer; Length=$item.Length; Modified=$item.LastWriteTimeUtc.Ticks}
            }
        }
    }
}
$evidence = @{ Wave=$Wave; Mode=$Mode; DryRun=[bool]$DryRun; ExpectedCount=$selected.Count; Deletions=@($deletions.Keys); Verified=$false }
$report = Join-Path (Split-Path -Parent $PlanPath) ('Logs/Wave{0:D2}-{1}-result.json' -f $Wave,$Mode)
if (!$DryRun) { [IO.Directory]::CreateDirectory((Split-Path -Parent $report)) | Out-Null }
Write-Output ('Check: {0} expected items, {1} planned removals' -f $selected.Count,$deletions.Count)
if ($DryRun) { $deletions.Keys | ForEach-Object { Write-Output ('Would remove: ' + $_) }; exit 0 }
foreach ($unit in $active) {
    $arguments=@($unit.Source,$unit.Destination,'/E','/XJ','/DCOPY:DAT','/R:1','/W:1',('/MT:'+$plan.Threads),'/NP')
    if ($plan.CopyAcl) { $arguments+='/COPYALL' } else { $arguments+='/COPY:DAT' }
    if ($unit.ExcludedSources.Count) { $arguments+='/XD'; $arguments+=@($unit.ExcludedSources) }
    & robocopy @arguments
    if ($LASTEXITCODE -ge 8) { throw 'Robocopy failed. No planned removals will be applied.' }
}
# Verify observed source stamps and destination sizes before applying any deletion.
foreach ($target in $selected.Keys) {
    $entry=$selected[$target]
    $actual=Get-Item -LiteralPath $target -Force
    if ([bool]$entry.Directory -ne [bool]$actual.PSIsContainer) { throw 'Destination type mismatch.' }
    if (!$entry.Directory) {
        $current=Get-Item -LiteralPath $entry.Source -Force
        if ($current.Length -ne $entry.Length -or $current.LastWriteTimeUtc.Ticks -ne $entry.Modified -or $actual.Length -ne $entry.Length) { throw 'Source changed or destination size verification failed; no removals applied.' }
    }
}
# Validate every removal candidate before the first removal.
foreach ($target in $deletions.Keys) {
    $entry=$deletions[$target]
    $actual=Get-Item -LiteralPath $target -Force
    if ($actual.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Deletion target became a reparse point.' }
    if (!$entry.Directory -and ($actual.Length -ne $entry.Length -or $actual.LastWriteTimeUtc.Ticks -ne $entry.Modified)) { throw 'Deletion candidate changed after Check.' }
}
foreach ($target in @($deletions.Keys | Sort-Object Length -Descending)) {
    $entry=$deletions[$target]
    $actual=Get-Item -LiteralPath $target -Force
    if ($actual.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Deletion target became a reparse point.' }
    if (!$entry.Directory -and ($actual.Length -ne $entry.Length -or $actual.LastWriteTimeUtc.Ticks -ne $entry.Modified)) { throw 'Deletion candidate changed after Check.' }
    if ($entry.Directory) { [IO.Directory]::Delete($target,$false) } else { [IO.File]::Delete($target) }
}
$evidence.Verified=$true
$evidence.Verification='Source size/time unchanged and destination type/size; not a content hash verification'
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
Write-Output 'Commit and Verify completed.'
exit 0

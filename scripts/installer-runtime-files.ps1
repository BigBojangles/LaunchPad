# Turn the validated payload plan into content-addressed manifest snapshots and
# an Inno [Files] include. No source image is copied or changed.
param(
    [Parameter(Mandatory = $true)][string]$PlanFile,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$generatedRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'src\LaunchPad\obj'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputRoot.StartsWith($generatedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer runtime metadata must stay under generated obj output.'
}
function Assert-Unlinked([string]$Path) {
    $entry = Get-Item -LiteralPath $Path -Force
    while ($null -ne $entry) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Installer input/output contains a link.' }
        $entry = if ($entry -is [IO.DirectoryInfo]) { $entry.Parent } else { $entry.Directory }
    }
}
Assert-Unlinked $outputRoot
Assert-Unlinked $PlanFile
if ((Get-Item -LiteralPath $PlanFile).Length -gt 262144) { throw 'Runtime plan is too large.' }
$plan = Get-Content -LiteralPath $PlanFile -Raw | ConvertFrom-Json
if ($plan.schema -ne 1 -or $plan.files -isnot [Array] -or $plan.files.Count -gt 24) { throw 'Runtime plan schema is invalid.' }
$runtimeEntry = @($plan.files | Where-Object { $_.file -ceq $plan.runtimeManifestFile })
$maintenanceEntry = @($plan.files | Where-Object { $_.file -ceq $plan.maintenanceManifestFile })
if ($runtimeEntry.Count -ne 1 -or $maintenanceEntry.Count -ne 1) { throw 'Runtime plan does not identify both manifests.' }
$inputImages = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($runtimeEntry[0].source))
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in $plan.files) {
    if ($item.file -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$' -or $item.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        $item.immutable -isnot [bool] -or -not $seen.Add([string]$item.file) -or
        -not [string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($item.source)), $inputImages, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($item.source) -cne $item.file) { throw 'Runtime plan contains an invalid artifact.' }
    Assert-Unlinked $item.source
    if ((Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $item.sha256) {
        throw 'Runtime inputs changed after planning; rebuild the plan before compiling.'
    }
}

function Write-Addressed([byte[]]$Bytes, [string]$Prefix) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
    $name = $Prefix + $hash + '.json'
    $path = Join-Path $outputRoot $name
    if (Test-Path -LiteralPath $path) {
        Assert-Unlinked $path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'A previous immutable build manifest conflicts; it was preserved.' }
    } else {
        $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.pending'
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $path)
    }
    return [PSCustomObject]@{ Name=$name; Path=$path }
}
$maintenance = Write-Addressed ([IO.File]::ReadAllBytes($maintenanceEntry[0].source)) 'maintenance-package-'
$runtime = Get-Content -LiteralPath $runtimeEntry[0].source -Raw | ConvertFrom-Json
$runtime | Add-Member -NotePropertyName maintenanceManifest -NotePropertyValue $maintenance.Name -Force
$candidate = Write-Addressed ([Text.UTF8Encoding]::new($false).GetBytes(($runtime | ConvertTo-Json -Depth 8))) 'runtime-package-'
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('#define RuntimeManifestInstallName "' + $candidate.Name + '"')
foreach ($item in $plan.files) {
    if ($item.immutable) {
        $lines.Add('Source: "' + $item.source + '"; DestDir: "{app}\images"; Flags: onlyifdoesntexist uninsneveruninstall')
    }
}
foreach ($snapshot in @($maintenance, $candidate)) {
    $lines.Add('Source: "' + $snapshot.Path + '"; DestDir: "{app}\images"; Flags: onlyifdoesntexist uninsneveruninstall')
}
$includePath = Join-Path $outputRoot 'runtime-files.iss'
if (Test-Path -LiteralPath $includePath) { Assert-Unlinked $includePath }
$includeTemporary = $includePath + '.' + [Guid]::NewGuid().ToString('N') + '.pending'
$includeBytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`r`n"))
$includeStream = [IO.FileStream]::new($includeTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $includeStream.Write($includeBytes, 0, $includeBytes.Length); $includeStream.Flush($true) } finally { $includeStream.Dispose() }
if (Test-Path -LiteralPath $includePath) { [IO.File]::Replace($includeTemporary, $includePath, [Management.Automation.Language.NullString]::Value) }
else { [IO.File]::Move($includeTemporary, $includePath) }
Write-Output $includePath

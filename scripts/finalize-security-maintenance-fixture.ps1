# Translate only the private Linux-editing view's verified backing address.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$Original,
    [Parameter(Mandatory)][string]$OriginalSha256,
    [Parameter(Mandatory)][string]$Backing,
    [Parameter(Mandatory)][string]$BackingSha256
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$guestRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'tests/LaunchPad.Tests/TestResults/guest'))
$fixture = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$originalPath = (Resolve-Path -LiteralPath $Original).Path
$backingPath = (Resolve-Path -LiteralPath $Backing).Path
if (!$fixture.StartsWith($guestRoot + '\security-maintenance-', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Parent $fixture) -ne $guestRoot -or
    !$originalPath.StartsWith($guestRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $originalPath) -ne 'session.qcow2') { throw 'Owned fixture paths required.' }
$migrationRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'tests/LaunchPad.Tests/TestResults/migration'))
if (!$backingPath.StartsWith($migrationRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $backingPath) -ne 'template.qcow2') { throw 'Owned exact backing template required.' }
foreach ($record in @(@{path=$originalPath;sha=$OriginalSha256}, @{path=$backingPath;sha=$BackingSha256})) {
    if ((Get-FileHash -LiteralPath $record.path).Hash.ToLowerInvariant() -ne $record.sha) { throw 'Original/backing identity mismatch.' }
}
$view = Join-Path $fixture 'portable-original.qcow2'
$disk = Join-Path $fixture 'session.qcow2'
foreach ($path in @($view, $disk)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture links forbidden.' }
}
$qemuImg = (Resolve-Path -LiteralPath (Join-Path $projectRoot '../build-launch-qemu/qemu/qemu-img.exe')).Path
$info = (& $qemuImg info --output=json $view) -join "`n" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Private view header unavailable.' }
$name = $info.'backing-filename'
$resolved = if ([IO.Path]::IsPathRooted($name)) { [IO.Path]::GetFullPath($name) }
    else { [IO.Path]::GetFullPath((Join-Path $fixture $name)) }
if ($resolved -ne $backingPath) { throw 'Never substitute different backing contents.' }
$before = (Get-FileHash -LiteralPath $view).Hash.ToLowerInvariant()
if ($name -ne $backingPath) {
    & $qemuImg rebase -u -f qcow2 -F qcow2 -b $backingPath $view
    if ($LASTEXITCODE -ne 0) { throw 'Private view address translation failed.' }
}
$chain = & $qemuImg info --backing-chain --output=json $disk
if ($LASTEXITCODE -ne 0) { throw 'Windows private chain unresolved.' }
$chain | Set-Content -LiteralPath (Join-Path $fixture 'windows-backing-chain-checked.json') -Encoding UTF8
$readAccess = @()
foreach ($path in @($view, $disk)) {
    $identity = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    & icacls.exe $path /grant 'BuildLaunchTest:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Owned fixture read permission failed.' }
    if ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $identity) { throw 'Fixture contents changed during permission setup.' }
    $readAccess += @{path=$path;sha256=$identity;identity='BuildLaunchTest';rights='Read'}
}
if ((Get-FileHash -LiteralPath $originalPath).Hash.ToLowerInvariant() -ne $OriginalSha256 -or
    (Get-FileHash -LiteralPath $backingPath).Hash.ToLowerInvariant() -ne $BackingSha256) { throw 'Source identity changed.' }
@{before=$before;after=(Get-FileHash -LiteralPath $view).Hash.ToLowerInvariant();backing=$backingPath;
  backingSha256=$BackingSha256;original=$originalPath;originalSha256=$OriginalSha256;
  readAccess=$readAccess;
  scope='Verified backing address translation and file-only launch-account read access on private fixture view/disk; no report-directory grant.'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'windows-path-check-private.json') -Encoding UTF8
Write-Output 'Owned private Windows backing chain verified.'

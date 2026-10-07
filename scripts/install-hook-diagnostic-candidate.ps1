param(
    [Parameter(Mandatory=$true)][string]$KitDirectory,
    [Parameter(Mandatory=$true)][string]$AppExe,
    [Parameter(Mandatory=$true)][string]$ExpectedExeSha256,
    [Parameter(Mandatory=$true)][string]$ExpectedImageSha256,
    [Parameter(Mandatory=$true)][string]$ExpectedPayloadSha256
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$reports = Join-Path $repository 'tests\LaunchPad.Tests\TestResults\migration'
$kit = (Resolve-Path -LiteralPath $KitDirectory).Path
if (-not $kit.StartsWith($reports + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use the prepared private diagnostic kit.' }
$root = Join-Path $env:LOCALAPPDATA 'Programs\LaunchPad'
$images = Join-Path $root 'images'
$installedExe = Join-Path $root 'LaunchPad.exe'
$processes = @(Get-CimInstance Win32_Process -Filter "Name='LaunchPad.exe' OR Name='qemu-system-x86_64.exe'")
if ($processes.Count -ne 0) { throw 'Close LaunchPad and its VM windows normally first. This script never forces them closed.' }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function CopyNew([string]$source, [string]$target) {
    $inputStream = [IO.File]::OpenRead($source)
    try {
        $outputStream = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}
function WriteNew([string]$path, $value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 8))
    $output = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $output.Write($bytes,0,$bytes.Length); $output.Flush($true) } finally { $output.Dispose() }
}
foreach ($pair in @(@($AppExe,$ExpectedExeSha256),@((Join-Path $kit 'template.qcow2'),$ExpectedImageSha256),@((Join-Path $kit 'upgrade.tar.gz'),$ExpectedPayloadSha256))) {
    if ($pair[1] -notmatch '^[a-f0-9]{64}$' -or (Hash $pair[0]) -ne $pair[1]) { throw 'Prepared candidate identity changed.' }
    if ((Get-Item -LiteralPath $pair[0]).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked candidate input.' }
}
$active = Join-Path $images 'runtime.json'
$runtime = Get-Content -LiteralPath $active -Raw | ConvertFrom-Json
if ($runtime.version -ne 'runtime-20261006-policy2') { throw 'This diagnostic candidate targets the installed policy2 runtime only.' }
$oldMaintenance = Get-Content -LiteralPath (Join-Path $images $runtime.maintenanceManifest) -Raw | ConvertFrom-Json
$preserved = @($runtime.image) + @($runtime.dependencies)
foreach ($artifact in $preserved) { if ((Hash (Join-Path $images $artifact.file)) -ne $artifact.sha256) { throw 'Installed backing identity differs.' } }
$sessionProof = @(Get-ChildItem -LiteralPath (Join-Path $root 'sessions') -Filter '*.qcow2' -Recurse -File | ForEach-Object {
    [pscustomobject]@{path=$_.FullName;sha256=(Hash $_.FullName)}
})
$version = 'runtime-20261007-hook-diagnostic'
$imageName = 'debian-12-builder-hook-diagnostic-20261007.qcow2'
if (-not ($imageName.StartsWith('debian-12-builder', [StringComparison]::OrdinalIgnoreCase) -and $imageName.EndsWith('.qcow2', [StringComparison]::OrdinalIgnoreCase))) { throw 'Diagnostic selection must satisfy the RuntimeImages builder naming contract.' }
$payloadName = 'launchpad-hook-diagnostic-20261007.tar.gz'
$maintenanceName = 'maintenance-hook-diagnostic-20261007.json'
$runtimeName = 'runtime-hook-diagnostic-20261007.json'
$backupExe = Join-Path $root 'uninstall-state-v2\LaunchPad-before-hook-diagnostic.exe'
$backupManifest = Join-Path $images 'runtime-before-hook-diagnostic-20261007.json'
foreach ($path in @($backupExe,$backupManifest) + @($imageName,$payloadName,$maintenanceName,$runtimeName | ForEach-Object { Join-Path $images $_ })) {
    if (Test-Path -LiteralPath $path) { throw 'Preserve the existing diagnostic candidate/rollback files; this one-time install is already staged.' }
}
$image = Join-Path $images $imageName
CopyNew (Join-Path $kit 'template.qcow2') $image
$imgExe = Join-Path $root 'qemu\qemu-img.exe'
# Readdress only this new owned child to byte-identical installed backing bytes.
# Original templates and session disks are never rebased or overwritten.
& $imgExe rebase -u -f qcow2 -F qcow2 -b (Join-Path $images $runtime.image.file) $image
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic child address preparation failed; original selection unchanged.' }
& $imgExe check $image
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic child failed qcow2 validation.' }
CopyNew (Join-Path $kit 'upgrade.tar.gz') (Join-Path $images $payloadName)
$kitProof = Get-Content -LiteralPath (Join-Path $kit 'kit-proof-private.json') -Raw | ConvertFrom-Json
$maintenance = @{schema=1;version=$version;kernel=$oldMaintenance.kernel;initrd=$oldMaintenance.initrd;
    payload=@{file=$payloadName;sha256=$ExpectedPayloadSha256};guestScriptSha256=$kitProof.helperSha256}
WriteNew (Join-Path $images $maintenanceName) $maintenance
$newRuntime = @{schema=1;version=$version;image=@{file=$imageName;sha256=(Hash $image)};
    dependencies=$preserved;capabilities=$runtime.capabilities;maintenanceManifest=$maintenanceName}
WriteNew (Join-Path $images $runtimeName) $newRuntime
foreach ($name in @($imageName,$payloadName,$maintenanceName,$runtimeName)) {
    & icacls.exe (Join-Path $images $name) /grant 'BuildLaunchTest:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic runtime read access failed; selection unchanged.' }
}
$temporary = Join-Path $root ('LaunchPad-hook-' + [Guid]::NewGuid().ToString('N') + '.tmp')
CopyNew $AppExe $temporary
$oldExeHash = Hash $installedExe
[IO.File]::Replace($temporary,$installedExe,$backupExe)
$runtimeTemporary = Join-Path $images ('runtime-hook-' + [Guid]::NewGuid().ToString('N') + '.tmp')
CopyNew (Join-Path $images $runtimeName) $runtimeTemporary
[IO.File]::Replace($runtimeTemporary,$active,$backupManifest)
foreach ($artifact in $preserved) { if ((Hash (Join-Path $images $artifact.file)) -ne $artifact.sha256) { throw 'Backing preservation check failed.' } }
foreach ($session in $sessionProof) { if ((Hash $session.path) -ne $session.sha256) { throw 'Session preservation check failed.' } }
WriteNew (Join-Path $kit 'installed-diagnostic-proof-private.json') @{installedUtc=[DateTime]::UtcNow;
    exeSha256=(Hash $installedExe);previousExeSha256=$oldExeHash;runtime=$newRuntime;
    preservedSessions=$sessionProof;liveCaptureVerified=$false;externalNotificationsSent=$false}
Write-Output 'Diagnostic candidate installed. Original images/session disks preserved; live hook capture is not yet verified.'

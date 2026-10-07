param(
    [Parameter(Mandatory=$true)][string]$KitDirectory,
    [Parameter(Mandatory=$true)][string]$ExpectedImageSha256,
    [Parameter(Mandatory=$true)][string]$ExpectedPayloadSha256,
    [string]$ExpectedCurrentVersion = 'runtime-20261007-hook-diagnostic',
    [string]$ExpectedHostExeSha256 = 'a8285ecf69b44e4ae303587f8058535290e83082a20e9550f62ebf514905d88a',
    [string]$Version = 'runtime-20261007-hook-directory-mutex'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$reports = Join-Path $repository 'tests\LaunchPad.Tests\TestResults\migration'
$kit = (Resolve-Path -LiteralPath $KitDirectory).Path
if (-not $kit.StartsWith($reports + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use the prepared private diagnostic kit.' }
$root = Join-Path $env:LOCALAPPDATA 'Programs\LaunchPad'
$images = Join-Path $root 'images'
$active = Join-Path $images 'runtime.json'
if (@(Get-CimInstance Win32_Process -Filter "Name='LaunchPad.exe' OR Name='qemu-system-x86_64.exe'").Count -ne 0) { throw 'Close LaunchPad and VM windows normally first. This script never forces them closed.' }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function CopyNew([string]$source, [string]$target) {
    $inputStream = [IO.File]::OpenRead($source)
    try {
        $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}
function WriteNew([string]$path, $value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 10))
    $outputStream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $outputStream.Write($bytes, 0, $bytes.Length); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
}
$runtime = Get-Content -LiteralPath $active -Raw | ConvertFrom-Json
if ($Version -notmatch '^runtime-[a-z0-9-]{1,64}$' -or $ExpectedHostExeSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid pinned candidate identity.' }
if ($runtime.version -ne $ExpectedCurrentVersion -or -not $runtime.image.file.StartsWith('debian-12-builder', [StringComparison]::OrdinalIgnoreCase)) { throw 'The selected current runtime differs from this candidate target.' }
if ((Hash (Join-Path $root 'LaunchPad.exe')) -ne $ExpectedHostExeSha256) { throw 'The matching host reader is not installed.' }
$baseline = Get-Content -LiteralPath (Join-Path $images 'runtime-before-hook-diagnostic-20261007.json') -Raw | ConvertFrom-Json
if ($baseline.version -ne 'runtime-20261006-policy2' -or $baseline.image.file -ne 'debian-12-builder-runtime-20261006-policy2.qcow2' -or $baseline.image.sha256 -ne '65ccb99943c5dcac8710bd38df446d5fb4a5c1c642fad9cb23b6ca1eb1fff7fc') { throw 'Pinned original backing selection differs.' }
$dependencies = @($baseline.image) + @($baseline.dependencies)
$preserved = @($runtime.image) + @($runtime.dependencies)
foreach ($artifact in $preserved) {
    $path = Join-Path $images $artifact.file
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installed backing.' }
    if ((Hash $path) -ne $artifact.sha256) { throw 'Installed backing identity differs.' }
}
foreach ($artifact in $dependencies) { if ((Hash (Join-Path $images $artifact.file)) -ne $artifact.sha256) { throw 'Original backing identity differs.' } }
foreach ($pair in @(@((Join-Path $kit 'template.qcow2'), $ExpectedImageSha256), @((Join-Path $kit 'upgrade.tar.gz'), $ExpectedPayloadSha256))) {
    if ($pair[1] -notmatch '^[a-f0-9]{64}$' -or (Hash $pair[0]) -ne $pair[1]) { throw 'Prepared candidate identity differs.' }
    if ((Get-Item -LiteralPath $pair[0]).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked candidate input.' }
}
$kitProof = Get-Content -LiteralPath (Join-Path $kit 'kit-proof-private.json') -Raw | ConvertFrom-Json
if (-not $kitProof.finalImageCaptureBytesVerified -or $kitProof.payloadSha256 -ne $ExpectedPayloadSha256 -or $kitProof.imageSha256 -ne $ExpectedImageSha256) { throw 'Final candidate receipt differs.' }
$sessions = @(Get-ChildItem -LiteralPath (Join-Path $root 'sessions') -Filter '*.qcow2' -Recurse -File | ForEach-Object { [pscustomobject]@{ path=$_.FullName; sha256=(Hash $_.FullName) } })
$imageName = 'debian-12-builder-' + $Version + '.qcow2'
$payloadName = 'launchpad-' + $Version + '.tar.gz'
$maintenanceName = 'maintenance-' + $Version + '.json'
$runtimeName = $Version + '.json'
$backup = Join-Path $images ('runtime-before-' + $Version + '.json')
$receipt = Join-Path $kit 'installed-directory-mutex-proof-private.json'
foreach ($path in @($backup, $receipt) + @($imageName, $payloadName, $maintenanceName, $runtimeName | ForEach-Object { Join-Path $images $_ })) { if (Test-Path -LiteralPath $path) { throw 'Candidate/rollback output already exists; preserve it rather than retrying installation.' } }
$image = Join-Path $images $imageName
CopyNew (Join-Path $kit 'template.qcow2') $image
$imgExe = Join-Path $root 'qemu\qemu-img.exe'
# Readdress this new owned child only to the byte-identical original policy2 backing.
# Never point it at the different diagnostic child or rebase original/session disks.
& $imgExe rebase -u -f qcow2 -F qcow2 -b (Join-Path $images $baseline.image.file) $image
if ($LASTEXITCODE -ne 0) { throw 'New child address preparation failed; selection unchanged.' }
& $imgExe check $image
if ($LASTEXITCODE -ne 0) { throw 'New child qcow2 validation failed; selection unchanged.' }
CopyNew (Join-Path $kit 'upgrade.tar.gz') (Join-Path $images $payloadName)
$oldMaintenance = Get-Content -LiteralPath (Join-Path $images $runtime.maintenanceManifest) -Raw | ConvertFrom-Json
WriteNew (Join-Path $images $maintenanceName) @{ schema=1; version=$version; kernel=$oldMaintenance.kernel; initrd=$oldMaintenance.initrd; payload=@{file=$payloadName;sha256=$ExpectedPayloadSha256}; guestScriptSha256=$kitProof.helperSha256 }
$newRuntime = @{ schema=1; version=$version; image=@{file=$imageName;sha256=(Hash $image)}; dependencies=$dependencies; capabilities=$runtime.capabilities; maintenanceManifest=$maintenanceName }
WriteNew (Join-Path $images $runtimeName) $newRuntime
foreach ($name in @($imageName, $payloadName, $maintenanceName, $runtimeName)) { & icacls.exe (Join-Path $images $name) /grant 'BuildLaunchTest:R' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'New runtime read access failed; selection unchanged.' } }
$temporary = Join-Path $images ('runtime-mutex-' + [Guid]::NewGuid().ToString('N') + '.tmp')
CopyNew (Join-Path $images $runtimeName) $temporary
[IO.File]::Replace($temporary, $active, $backup)
foreach ($artifact in $preserved) { if ((Hash (Join-Path $images $artifact.file)) -ne $artifact.sha256) { throw 'Original backing preservation check failed.' } }
foreach ($session in $sessions) { if ((Hash $session.path) -ne $session.sha256) { throw 'Session preservation check failed.' } }
WriteNew $receipt @{ installedUtc=[DateTime]::UtcNow; runtime=$newRuntime; preservedImages=$preserved; preservedSessions=$sessions; hostExeSha256=(Hash (Join-Path $root 'LaunchPad.exe')); guestAppArmorVerified=$false; liveHookCaptureVerified=$false; externalNotificationsSent=$false }
Write-Output 'Corrected hook candidate installed. Original images and session disks preserved; real guest capture remains to be observed.'

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Programs\LaunchPad'
$images = Join-Path $root 'images'
$active = Join-Path $images 'runtime.json'
$processes = @(Get-CimInstance Win32_Process -Filter "Name='LaunchPad.exe' OR Name='qemu-system-x86_64.exe'")
if ($processes.Count -ne 0) { throw 'Close LaunchPad and its VM windows normally first. No process is forced closed.' }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function CopyNew([string]$source, [string]$target) {
    $inputStream = [IO.File]::OpenRead($source)
    try {
        $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }
}
$runtime = Get-Content -LiteralPath $active -Raw | ConvertFrom-Json
if ($runtime.version -ne 'runtime-20261007-hook-diagnostic' -or $runtime.image.file -ne 'launchpad-hook-diagnostic-20261007.qcow2' -or $runtime.image.sha256 -ne '7ce00ceecc81cd84ed5493ccc72f1ba4df7f5d5eace34086b2974357f09ac008') { throw 'This repair applies only to the recorded diagnostic selection.' }
$oldImage = Join-Path $images $runtime.image.file
$newName = 'debian-12-builder-hook-diagnostic-20261007.qcow2'
$newImage = Join-Path $images $newName
$newManifest = Join-Path $images 'runtime-hook-diagnostic-builder-20261007.json'
$backupManifest = Join-Path $images 'runtime-before-builder-name-repair-20261007.json'
$proof = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\LaunchPad.Tests\TestResults\migration\hook-diagnostic-kit-20261007-v2\installed-builder-name-repair-private.json'
foreach ($path in @($newImage, $newManifest, $backupManifest, $proof)) {
    if (Test-Path -LiteralPath $path) { throw 'Repair output already exists; preserve it rather than rerunning this one-time repair.' }
}
$originals = @(@($runtime.image) + @($runtime.dependencies) | ForEach-Object { [pscustomobject]@{ file=$_.file; sha256=$_.sha256 } })
foreach ($artifact in $originals) {
    $path = Join-Path $images $artifact.file
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installed image.' }
    if ((Hash $path) -ne $artifact.sha256) { throw 'Installed image identity differs; selection unchanged.' }
}
$sessions = @(Get-ChildItem -LiteralPath (Join-Path $root 'sessions') -Filter '*.qcow2' -Recurse -File | ForEach-Object { [pscustomobject]@{ path=$_.FullName; sha256=(Hash $_.FullName) } })
CopyNew $oldImage $newImage
if ((Hash $newImage) -ne $runtime.image.sha256) { throw 'Corrected image copy differs; selection unchanged.' }
& (Join-Path $root 'qemu\qemu-img.exe') check $newImage
if ($LASTEXITCODE -ne 0) { throw 'Corrected image failed qcow2 check; selection unchanged.' }
& icacls.exe $newImage /grant 'BuildLaunchTest:R' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Corrected image read access failed; selection unchanged.' }
$runtime.image.file = $newName
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($runtime | ConvertTo-Json -Depth 8))
$outputStream = [IO.File]::Open($newManifest, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { $outputStream.Write($bytes, 0, $bytes.Length); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
& icacls.exe $newManifest /grant 'BuildLaunchTest:R' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Corrected manifest read access failed; selection unchanged.' }
$temporary = Join-Path $images ('runtime-builder-repair-' + [Guid]::NewGuid().ToString('N') + '.tmp')
CopyNew $newManifest $temporary
[IO.File]::Replace($temporary, $active, $backupManifest)
foreach ($artifact in $originals) { if ((Hash (Join-Path $images $artifact.file)) -ne $artifact.sha256) { throw 'Original image preservation check failed.' } }
foreach ($session in $sessions) { if ((Hash $session.path) -ne $session.sha256) { throw 'Session preservation check failed.' } }
$receipt = @{ repairedUtc=[DateTime]::UtcNow; runtime=$runtime; preservedImages=$originals; preservedSessions=$sessions; qcowCheckPassed=$true; liveLaunchVerified=$false }
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 8))
$outputStream = [IO.File]::Open($proof, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { $outputStream.Write($bytes, 0, $bytes.Length); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
Write-Output 'Builder filename selection corrected. Original image bytes and session disks preserved; normal launch remains to be observed.'

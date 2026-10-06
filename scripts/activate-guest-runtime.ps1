param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$ExpectedManifestSha256
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$runtimeRoot = [System.IO.Path]::GetFullPath((Join-Path $repository '..\build-launch-qemu'))
$imagesRoot = Join-Path $runtimeRoot 'images'
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$reportsRoot = Join-Path $repository 'tests\LaunchPad.Tests\TestResults\migration'
if (-not $manifestPath.StartsWith($reportsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a reviewed private staging manifest.' }
if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $ExpectedManifestSha256) { throw 'Reviewed manifest changed.' }
$identity = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
if ($identity.schema -ne 1 -or -not $identity.image -or $identity.dependencies.Count -gt 16) { throw 'Invalid runtime manifest.' }
foreach ($file in @($identity.image) + @($identity.dependencies)) {
    if ($file.file -notmatch '^[A-Za-z0-9._-]+\.qcow2$' -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Unsafe image record.' }
    $path = Join-Path $imagesRoot $file.file
    if ((Get-Item -LiteralPath $path).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Linked image.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Image identity mismatch: $($file.file)" }
}
$active = Join-Path $imagesRoot 'runtime.json'
if (Test-Path -LiteralPath $active) {
    $history = Join-Path $imagesRoot ('runtime-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '.json')
    Copy-Item -LiteralPath $active -Destination $history -ErrorAction Stop
}
$temporary = Join-Path $imagesRoot ('runtime-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $bytes = [System.IO.File]::ReadAllBytes($manifestPath)
    $stream = [System.IO.File]::Open($temporary, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    & icacls.exe (Join-Path $imagesRoot $identity.image.file) /grant 'BuildLaunchTest:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Launch identity could not read the new image.' }
    & icacls.exe $temporary /grant 'BuildLaunchTest:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Manifest read access could not be established.' }
    if (Test-Path -LiteralPath $active) { [System.IO.File]::Replace($temporary, $active, $history) }
    else { [System.IO.File]::Move($temporary, $active) }
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
Write-Output "Activated VM runtime $($identity.version). Old images and session disks were preserved."

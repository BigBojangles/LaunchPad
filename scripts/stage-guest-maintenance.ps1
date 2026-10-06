param(
    [Parameter(Mandatory=$true)][string]$KitDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$KernelSha256,
    [Parameter(Mandatory=$true)][string]$InitrdSha256,
    [Parameter(Mandatory=$true)][string]$PayloadSha256,
    [Parameter(Mandatory=$true)][string]$GuestScriptSha256
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$reports = Join-Path $repository 'tests\LaunchPad.Tests\TestResults\migration'
$source = (Resolve-Path -LiteralPath $KitDirectory).Path
if (-not $source.StartsWith($reports + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use reviewed private maintenance inputs.' }
if ($Version -notmatch '^[a-z0-9-]{1,64}$') { throw 'Unsafe maintenance version.' }
foreach ($hash in @($KernelSha256, $InitrdSha256, $PayloadSha256, $GuestScriptSha256)) {
    if ($hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'An expected SHA256 is invalid.' }
}
if ((Get-FileHash -LiteralPath (Join-Path $source 'payload\bl-proof.sh')).Hash -ne $GuestScriptSha256) { throw 'Guest script identity changed.' }
$images = [IO.Path]::GetFullPath((Join-Path $repository '..\build-launch-qemu\images'))
$artifacts = @(
    @{ source='kernel'; file="launchpad-maintenance-$Version.kernel"; sha256=$KernelSha256 },
    @{ source='initrd'; file="launchpad-maintenance-$Version.initrd"; sha256=$InitrdSha256 },
    @{ source='upgrade.tar.gz'; file="launchpad-maintenance-$Version.tar.gz"; sha256=$PayloadSha256 }
)
foreach ($artifact in $artifacts) {
    $inputPath = Join-Path $source $artifact.source
    if ((Get-Item -LiteralPath $inputPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked maintenance input.' }
    if ((Get-FileHash -LiteralPath $inputPath).Hash -ne $artifact.sha256) { throw 'Maintenance input changed.' }
    $target = Join-Path $images $artifact.file
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked maintenance artifact.' }
        if ((Get-FileHash -LiteralPath $target).Hash -ne $artifact.sha256) { throw 'Existing version differs; do not replace it.' }
    } else {
        $inputStream = [IO.File]::OpenRead($inputPath)
        try {
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        if ((Get-FileHash -LiteralPath $target).Hash -ne $artifact.sha256) { throw 'Staged maintenance artifact failed verification.' }
    }
    & icacls.exe $target /grant 'BuildLaunchTest:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Maintenance read access could not be established.' }
}
function Record($artifact) { return @{ file=$artifact.file; sha256=$artifact.sha256.ToLowerInvariant() } }
$manifest = @{ schema=1; version=$Version; kernel=(Record $artifacts[0]); initrd=(Record $artifacts[1]); payload=(Record $artifacts[2]); guestScriptSha256=$GuestScriptSha256.ToLowerInvariant() }
$active = Join-Path $images 'maintenance.json'
if (Test-Path -LiteralPath $active) {
    if ((Get-Item -LiteralPath $active).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked active maintenance manifest.' }
    $history = Join-Path $images ('maintenance-' + [Guid]::NewGuid().ToString('N') + '.json')
    Copy-Item -LiteralPath $active -Destination $history
}
$temporary = Join-Path $images ('maintenance-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 5))
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    & icacls.exe $temporary /grant 'BuildLaunchTest:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Maintenance manifest read access could not be established.' }
    if (Test-Path -LiteralPath $active) { [IO.File]::Replace($temporary, $active, $history) }
    else { [IO.File]::Move($temporary, $active) }
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
Write-Output "Staged offline maintenance $Version. Original templates and sessions were preserved."

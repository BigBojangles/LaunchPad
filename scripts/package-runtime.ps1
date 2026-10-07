# Read-only selection of the exact image chain and maintenance kit for packaging.
# This creates a small build plan, never copies/rebases disks or activates a runtime.
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [Parameter(Mandatory = $true)][string]$OutputFile,
    [string]$DestinationImages,
    [string]$QemuImg
)
$ErrorActionPreference = 'Stop'

function Assert-UnlinkedPath([string]$Path) {
    $entry = Get-Item -LiteralPath $Path -Force
    while ($null -ne $entry) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A runtime package input contains a link.'
        }
        $entry = if ($entry -is [IO.DirectoryInfo]) { $entry.Parent } else { $entry.Directory }
    }
}
function Read-Manifest([string]$Path) {
    Assert-UnlinkedPath $Path
    if ((Get-Item -LiteralPath $Path).Length -gt 65536) { throw 'Runtime manifest exceeds 64 KiB.' }
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (($value.schema -isnot [int] -and $value.schema -isnot [long]) -or $value.schema -ne 1 -or
        $value.version -isnot [string] -or $value.version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
        throw 'Runtime manifest schema/version is invalid.'
    }
    return $value
}

$runtimePath = [IO.Path]::GetFullPath($RuntimeRoot)
$outputPath = [IO.Path]::GetFullPath($OutputFile)
$workspace = Split-Path -Parent $PSScriptRoot
$generatedRoots = @((Join-Path $workspace 'src\LaunchPad\obj'), (Join-Path $workspace 'tests\LaunchPad.Tests\TestResults'))
$allowedOutput = $false
foreach ($generatedRoot in $generatedRoots) {
    if ($outputPath.StartsWith($generatedRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        $allowedOutput = $true
    }
}
if (-not $allowedOutput -or $outputPath.StartsWith($runtimePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    ($DestinationImages -and $outputPath.StartsWith([IO.Path]::GetFullPath($DestinationImages).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) -or
    ($QemuImg -and [string]::Equals($outputPath, [IO.Path]::GetFullPath($QemuImg), [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Output must be a separate generated build/test plan, outside runtime inputs and destination images.'
}
$imagesPath = Join-Path $runtimePath 'images'
$runtimeManifestPath = Join-Path $imagesPath 'runtime.json'
$runtimeManifest = Read-Manifest $runtimeManifestPath
$maintenanceFile = if ($null -eq $runtimeManifest.maintenanceManifest) { 'maintenance.json' } else { $runtimeManifest.maintenanceManifest }
if ($maintenanceFile -isnot [string] -or $maintenanceFile -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.json$') { throw 'The maintenance manifest pointer is invalid.' }
$maintenanceManifestPath = Join-Path $imagesPath $maintenanceFile
$maintenanceManifest = Read-Manifest $maintenanceManifestPath
if ($runtimeManifest.version -cne $maintenanceManifest.version) { throw 'The maintenance kit does not match the selected runtime.' }
if ($runtimeManifest.image -isnot [PSCustomObject] -or $runtimeManifest.dependencies -isnot [Array] -or $runtimeManifest.dependencies.Count -gt 16 -or
    $runtimeManifest.capabilities -isnot [Array] -or $runtimeManifest.capabilities.Count -gt 32 -or
    @($runtimeManifest.capabilities | Where-Object { $_ -isnot [string] }).Count -ne 0 -or
    $runtimeManifest.image.file -isnot [string] -or $runtimeManifest.image.file -notmatch '^debian-12-builder[A-Za-z0-9._-]*\.qcow2$' -or
    $maintenanceManifest.guestScriptSha256 -isnot [string] -or $maintenanceManifest.guestScriptSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'The selected runtime or maintenance schema is invalid.' }

$selectedImages = @($runtimeManifest.image) + @($runtimeManifest.dependencies)
$maintenanceArtifacts = @($maintenanceManifest.kernel, $maintenanceManifest.initrd, $maintenanceManifest.payload)
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files = [Collections.Generic.List[object]]::new()
foreach ($item in @($selectedImages + $maintenanceArtifacts)) {
    if ($item -isnot [PSCustomObject] -or $item.file -isnot [string] -or $item.sha256 -isnot [string] -or
        $item.file -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$' -or $item.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not $names.Add([string]$item.file)) {
        throw 'Runtime artifact has an unsafe/duplicate name or invalid checksum.'
    }
    if ($selectedImages -contains $item -and $item.file -notmatch '\.qcow2$') { throw 'A selected image is not qcow2.' }
    $inputPath = Join-Path $imagesPath $item.file
    Assert-UnlinkedPath $inputPath
    $inputFile = Get-Item -LiteralPath $inputPath
    if ($inputFile.PSIsContainer) { throw 'A runtime artifact is a directory.' }
    $inputHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($inputHash -cne $item.sha256.ToLowerInvariant()) { throw "Runtime input failed its content check: $($item.file)" }
    $files.Add([ordered]@{ file=$item.file; source=$inputPath; length=$inputFile.Length; sha256=$inputHash; immutable=$true })
}

$qemuTool = if ($QemuImg) { [IO.Path]::GetFullPath($QemuImg) } else { Join-Path $runtimePath 'qemu\qemu-img.exe' }
Assert-UnlinkedPath $qemuTool
$chainJson = & $qemuTool info --output=json --backing-chain (Join-Path $imagesPath $runtimeManifest.image.file) 2>&1
if ($LASTEXITCODE -ne 0) { throw 'QEMU could not inspect the selected image chain; package plan was not updated.' }
$chain = ($chainJson -join "`n") | ConvertFrom-Json
if (@($chain).Count -ne $selectedImages.Count) { throw 'The selected image chain differs from its dependency manifest.' }
$expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($image in $selectedImages) { [void]$expectedPaths.Add([IO.Path]::GetFullPath((Join-Path $imagesPath $image.file))) }
$chainRows = [Collections.Generic.List[object]]::new()
foreach ($image in $chain) {
    if ($image.'format-specific'.data.PSObject.Properties['data-file']) { throw 'External qcow2 data files are not supported by this package plan.' }
    $imagePath = [IO.Path]::GetFullPath([string]$image.filename)
    if ($image.format -ne 'qcow2' -or -not $expectedPaths.Remove($imagePath)) { throw 'Unexpected image in the QEMU backing chain.' }
    $backingName = $image.'backing-filename'
    if ($backingName) {
        if ($backingName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.qcow2$') { throw 'A backing image uses a non-portable path.' }
        $expectedBacking = [IO.Path]::GetFullPath((Join-Path $imagesPath $backingName))
        if (-not [string]::Equals($expectedBacking, [IO.Path]::GetFullPath([string]$image.'full-backing-filename'), [StringComparison]::OrdinalIgnoreCase) -or
            -not $names.Contains($backingName)) { throw 'A backing path does not resolve to a packaged image.' }
    }
    $chainRows.Add([ordered]@{ file=[IO.Path]::GetFileName($imagePath); backing=$backingName })
}
if ($expectedPaths.Count -ne 0) { throw 'A declared image is absent from the backing chain.' }

foreach ($manifestPath in @($runtimeManifestPath, $maintenanceManifestPath)) {
    $manifestFile = Get-Item -LiteralPath $manifestPath
    $files.Add([ordered]@{ file=$manifestFile.Name; source=$manifestFile.FullName; length=$manifestFile.Length
        sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); immutable=$false })
}
if ($DestinationImages) {
    $destination = [IO.Path]::GetFullPath($DestinationImages)
    Assert-UnlinkedPath $destination
    foreach ($item in $files) {
        if (-not $item.immutable) { continue }
        $existingPath = Join-Path $destination $item.file
        if (-not (Test-Path -LiteralPath $existingPath)) { continue }
        Assert-UnlinkedPath $existingPath
        if ((Get-FileHash -LiteralPath $existingPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $item.sha256) {
            throw "Existing runtime file conflicts with this package; preserve it and the active selection: $($item.file)"
        }
    }
}

$plan = [ordered]@{ schema=1; version=$runtimeManifest.version; selectedImage=$runtimeManifest.image.file
    runtimeManifestFile='runtime.json'; maintenanceManifestFile=$maintenanceFile
    backingChain=@($chainRows.ToArray()); files=@($files.ToArray()); destinationChecked=[bool]$DestinationImages
    qemuImgSha256=(Get-FileHash -LiteralPath $qemuTool -Algorithm SHA256).Hash.ToLowerInvariant()
    activation='Not performed. Installer integration must validate installed files before activating manifests and preserve old images/session disks.' }
$outputDirectory = [IO.Path]::GetDirectoryName($outputPath)
Assert-UnlinkedPath $outputDirectory
if (Test-Path -LiteralPath $outputPath) { Assert-UnlinkedPath $outputPath }
$temporaryPath = $outputPath + '.' + [Guid]::NewGuid().ToString('N') + '.pending'
try {
    $planBytes = [Text.UTF8Encoding]::new($false).GetBytes(($plan | ConvertTo-Json -Depth 8))
    $outputStream = [IO.FileStream]::new($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $outputStream.Write($planBytes, 0, $planBytes.Length); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
    if (Test-Path -LiteralPath $outputPath) { [IO.File]::Replace($temporaryPath, $outputPath, [Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporaryPath, $outputPath) }
} finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath }
}
Write-Output "Validated $($files.Count) runtime payload files for $($runtimeManifest.version); no activation or image copy."

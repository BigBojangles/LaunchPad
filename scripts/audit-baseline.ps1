# Read-only runtime/provenance inventory. Reports are private generated test output.
[CmdletBinding()]
param(
    [string]$RuntimeRoot,
    [string]$InstalledRoot,
    [switch]$HashArtifacts
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$RuntimeRoot) { $RuntimeRoot = Join-Path (Split-Path -Parent $projectRoot) 'build-launch-qemu' }
if (!$InstalledRoot) { $InstalledRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\LaunchPad' }
$reportRoot = Join-Path $projectRoot 'tests\LaunchPad.Tests\TestResults\baseline'
New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null
$problems = [System.Collections.Generic.List[string]]::new()

function Get-Artifact([string]$Path, [string]$Class, [bool]$Hash) {
    $exists = Test-Path -LiteralPath $Path -PathType Leaf
    $record = [ordered]@{ path = $Path; classification = $Class; exists = $exists; bytes = $null; modifiedUtc = $null; sha256 = $null; error = $null }
    if ($exists) {
        try {
            $item = Get-Item -LiteralPath $Path
            $record.bytes = $item.Length
            $record.modifiedUtc = $item.LastWriteTimeUtc.ToString('o')
            if ($Hash) { $record.sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
        } catch { $record.error = $_.Exception.Message; $problems.Add("Artifact unavailable: $Path") }
    }
    [pscustomobject]$record
}

function Get-DirectoryInventory([string]$Root, [string]$Class, [bool]$Hash) {
    $files = @()
    $aliases = @()
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { return [pscustomobject]@{ root = $Root; exists = $false; files = @(); aliases = @(); bytes = 0 } }
    $pending = [System.Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Force)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $aliases += [pscustomobject]@{ path = $item.FullName; type = $item.LinkType; target = @($item.Target) }
                continue
            }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName); continue }
            # Record provenance without reading secret contents or hashing credentials.
            if ($item.Name -match '^(auth\.json|fence-user\.bin|builder\.pass)$|\.(pem|pfx|p12|secret)$') {
                $files += Get-Artifact $item.FullName 'private-credential-excluded' $false
                continue
            }
            $files += Get-Artifact $item.FullName $Class $Hash
        }
    }
    [pscustomobject]@{ root = $Root; exists = $true; files = $files; aliases = $aliases; bytes = ($files | Measure-Object bytes -Sum).Sum }
}

function Get-Runtime([string]$Root) {
    $relativeFiles = @('LaunchPad.exe', 'qemu\qemu-img.exe', 'qemu\qemu-system-x86_64.exe', 'qemu\fence\qemu-system-x86_64.exe', 'images\debian-12-builder.qcow2', 'images\debian-12-nocloud-amd64-20260601-2496.qcow2')
    $artifacts = @($relativeFiles | ForEach-Object { Get-Artifact (Join-Path $Root $_) 'runtime' $HashArtifacts.IsPresent })
    $sessions = Join-Path $Root 'sessions'
    $sessionDisks = @()
    if (Test-Path -LiteralPath $sessions -PathType Container) {
        $sessionDisks = @(Get-ChildItem -LiteralPath $sessions -Filter '*.qcow2' -Recurse -File | ForEach-Object { Get-Artifact $_.FullName 'persistent-session-state' $false })
    }
    $chains = @()
    $imgTool = Join-Path $Root 'qemu\qemu-img.exe'
    if (Test-Path -LiteralPath $imgTool -PathType Leaf) {
        foreach ($disk in @($artifacts | Where-Object { $_.exists -and $_.path.EndsWith('.qcow2') }) + $sessionDisks) {
            # info only; never repair, rebase, convert, or force access to a locked disk.
            $info = & $imgTool info --backing-chain --output=json $disk.path 2>&1
            $chains += [pscustomobject]@{ path = $disk.path; exitCode = $LASTEXITCODE; output = ($info -join "`n") }
            if ($LASTEXITCODE -ne 0) { $problems.Add("Backing chain unavailable: $($disk.path)") }
        }
    }
    $qemuPayload = Get-DirectoryInventory (Join-Path $Root 'qemu') 'runtime-library-firmware-or-tool' $HashArtifacts.IsPresent
    $preparation = Get-DirectoryInventory (Join-Path $Root 'images/prepare') 'guest-rebuild-input-or-preparation-evidence' $HashArtifacts.IsPresent
    $additionalImages = @()
    $imageRoot = Join-Path $Root 'images'
    if (Test-Path -LiteralPath $imageRoot -PathType Container) {
        $additionalImages = @(Get-ChildItem -LiteralPath $imageRoot -File | Where-Object { $_.Name -notin @('debian-12-builder.qcow2', 'debian-12-nocloud-amd64-20260601-2496.qcow2') } | ForEach-Object {
            $class = if ($_.Name -eq 'debian-12-builder.qcow2.bak-2026-10-05') { 'retained-rollback-backup' } elseif ($_.Name -eq 'SHA512SUMS') { 'upstream-image-checksums' } else { 'unclassified-image-input-review-required' }
            Get-Artifact $_.FullName $class $HashArtifacts.IsPresent
        })
    }
    [pscustomobject]@{ root = $Root; exists = (Test-Path -LiteralPath $Root -PathType Container); artifacts = $artifacts; qemuPayload = $qemuPayload; preparation = $preparation; additionalImages = $additionalImages; sessionsDirectoryExists = (Test-Path -LiteralPath $sessions -PathType Container); sessionDisks = $sessionDisks; backingChains = $chains }
}

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'), (Join-Path $projectRoot 'scripts'), (Join-Path $projectRoot 'installer') -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|TestResults|stage)[\\/]' } | ForEach-Object { Get-Artifact $_.FullName 'source-or-build-input' $true })
$distribution = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'dist') -File | ForEach-Object { Get-Artifact $_.FullName 'live-distribution' $HashArtifacts.IsPresent })
$archives = @(Get-ChildItem -LiteralPath $projectRoot -Directory | Where-Object { $_.Name -match '^v1\.0\..* release$' } | ForEach-Object { Get-DirectoryInventory $_.FullName 'historical-release-archive' $HashArtifacts.IsPresent })
$generated = @('src/LaunchPad/bin', 'src/LaunchPad/obj', 'tests/LaunchPad.Tests/bin', 'tests/LaunchPad.Tests/obj', 'tests/LaunchPad.Tests/TestResults') | ForEach-Object { Get-DirectoryInventory (Join-Path $projectRoot $_) 'generated-build-test-or-private-audit-output' $false }
$quarantine = Get-DirectoryInventory (Join-Path $projectRoot 'trash') 'authorized-duplicate-quarantine-do-not-package' $false
$report = [ordered]@{
    schemaVersion = 2
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    userProfile = [Environment]::GetFolderPath('UserProfile')
    localApplicationData = [Environment]::GetFolderPath('LocalApplicationData')
    os = [Environment]::OSVersion.VersionString
    process64Bit = [Environment]::Is64BitProcess
    launchpadQemuOverride = $env:LAUNCHPAD_QEMU
    artifactHashesRequested = $HashArtifacts.IsPresent
    sourceFiles = $sourceFiles
    distribution = $distribution
    archives = $archives
    generated = @($generated)
    duplicateQuarantine = $quarantine
    developmentRuntime = Get-Runtime $RuntimeRoot
    installedRuntime = Get-Runtime $InstalledRoot
    limitations = @('Inventory does not prove source-to-binary equivalence.', 'Session disks are not hashed because they may be active user state.', 'Agent functionality, security coverage, performance, and installer behavior require separate verification.')
    problems = @($problems.ToArray())
}
$destination = Join-Path $reportRoot ('inventory-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json')
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding UTF8
Write-Output "Inventory: $destination"
Write-Output "Identity: $($report.identity)"
Write-Output "Installed runtime present: $($report.installedRuntime.exists)"
Write-Output "Unavailable inventory checks: $($problems.Count)"

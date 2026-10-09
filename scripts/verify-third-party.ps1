param(
    [ValidateSet('Full','Native')][string]$Profile = 'Full',
    [string]$DistributionRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
$distribution = [IO.Path]::GetFullPath($DistributionRoot)
function RequiredFile([string]$Relative, [string]$ExpectedHash) {
    if ($Relative -notmatch '^[A-Za-z0-9._/-]+$' -or $Relative.Split('/') -contains '..' -or [IO.Path]::IsPathRooted($Relative)) {
        throw 'Third-party metadata contains an unsafe path.'
    }
    $path = Join-Path $distribution $Relative
    $entry = Get-Item -LiteralPath $path
    if ($entry.PSIsContainer -or $entry.Length -eq 0) { throw "Missing/empty third-party file: $Relative" }
    while ($null -ne $entry) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked third-party file: $Relative" }
        $entry = if ($entry -is [IO.DirectoryInfo]) { $entry.Parent } else { $entry.Directory }
    }
    if ($ExpectedHash -and ($ExpectedHash -notmatch '^[a-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedHash)) {
        throw "Changed third-party file: $Relative"
    }
    return $path
}
$manifestPath = RequiredFile ('licenses/manifest-' + $Profile.ToLowerInvariant() + '.json') ''
if ((Get-Item $manifestPath).Length -gt 1048576) { throw 'Third-party manifest exceeds size limit.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.profile -cne $Profile -or -not $manifest.components -or -not $manifest.files) {
    throw 'Invalid third-party manifest.'
}
$knownFiles = @{}
foreach ($file in $manifest.files) {
    if ($knownFiles.ContainsKey($file.path)) { throw 'Duplicate third-party file.' }
    if ($file.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Third-party file hash is missing.' }
    $null = RequiredFile $file.path $file.sha256
    $knownFiles[$file.path] = $true
}
if (-not $knownFiles.ContainsKey('THIRD-PARTY-NOTICES.txt')) { throw 'Third-party notices are missing.' }
$identities = @{}
foreach ($component in $manifest.components) {
    $identity = $component.id.ToLowerInvariant() + '/' + $component.version
    if ($identities.ContainsKey($identity) -or -not $component.license -or $component.source -notmatch '^https://') {
        throw "Incomplete/duplicate third-party component: $identity"
    }
    if (-not $component.files -or -not ($component.files | Where-Object { $_ -match '(?i)/licen[cs]e[^/]*$|/COPYING[^/]*$' })) {
        throw "Full license text missing: $identity"
    }
    foreach ($file in $component.files) { if (-not $knownFiles.ContainsKey($file)) { throw "Unverified license text: $file" } }
    foreach ($binary in $component.binaries) {
        if ($binary.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Runtime binary hash is missing.' }
        $null = RequiredFile $binary.path $binary.sha256
    }
    $identities[$identity] = $true
}
$lockPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/LaunchPad/packages.publish.lock.json'
if ((Get-FileHash $lockPath).Hash.ToLowerInvariant() -cne $manifest.packageLockSha256) { throw 'License inventory does not match the package lock.' }
$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
foreach ($group in $lock.dependencies.PSObject.Properties) {
    foreach ($package in $group.Value.PSObject.Properties) {
        if ($package.Value.resolved -and -not $identities.ContainsKey($package.Name.ToLowerInvariant() + '/' + $package.Value.resolved)) {
            throw "Package license is missing: $($package.Name) $($package.Value.resolved)"
        }
    }
}
if (-not ($manifest.components | Where-Object kind -eq 'dotnet-runtime')) { throw 'Self-contained .NET runtime license is missing.' }
if ($Profile -eq 'Full') {
    if ($manifest.unresolvedBinaries) { throw 'Full runtime has unresolved binary/license provenance; do not distribute.' }
    foreach ($kind in 'qemu','kernel','debian') {
        if (-not ($manifest.components | Where-Object kind -eq $kind)) { throw "Missing Full component inventory: $kind" }
    }
    $null = RequiredFile $manifest.debianInventory.path $manifest.debianInventory.sha256
    $runtime = Get-Content (Join-Path $distribution 'images/runtime.json') -Raw | ConvertFrom-Json
    if ($manifest.debianInventory.imageSha256 -cne $runtime.image.sha256) { throw 'Debian inventory belongs to a different image.' }
    $sourceZip = RequiredFile $manifest.sourceArchive.path $manifest.sourceArchive.sha256
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($sourceZip)
    try {
        $entry = $zip.GetEntry('SOURCE-MANIFEST.json')
        if ($null -eq $entry -or $entry.Length -eq 0 -or $entry.Length -gt 1048576) { throw 'Corresponding source archive has no bounded source manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $sources = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        foreach ($id in 'qemu-fence','qemu-img','glib','libiconv','libintl') {
            $source = @($sources.components | Where-Object id -ceq $id)
            if ($source.Count -ne 1 -or $source[0].correspondenceConfirmed -ne $true -or -not $source[0].files -or -not $source[0].binaries) {
                throw "Corresponding source/build provenance incomplete: $id"
            }
            foreach ($file in $source[0].files) {
                if ($file.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Source archive entry hash is missing.' }
                $archiveEntry = $zip.GetEntry($file.path)
                if ($null -eq $archiveEntry -or $archiveEntry.Length -eq 0) { throw "Source archive entry missing: $($file.path)" }
                $stream = $archiveEntry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
                try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
                finally { $sha.Dispose(); $stream.Dispose() }
                if ($actual -cne $file.sha256) { throw 'Source archive entry changed.' }
            }
            foreach ($binary in $source[0].binaries) {
                if ($binary.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Source binary correspondence hash is missing.' }
                $null = RequiredFile $binary.path $binary.sha256
            }
        }
    } finally { $zip.Dispose() }
    foreach ($binary in Get-ChildItem (Join-Path $distribution 'qemu') -File -Recurse | Where-Object Extension -in '.dll','.exe') {
        $relative = $binary.FullName.Substring($distribution.Length + 1).Replace('\','/')
        if (-not ($manifest.components | Where-Object { $_.binaries | Where-Object path -ceq $relative })) { throw "No component/license for runtime binary: $relative" }
        if ($binary.Extension -eq '.exe' -and $relative -notin 'qemu/fence/qemu-system-x86_64.exe','qemu/qemu-img.exe') { throw "Unexpected machine executable: $relative" }
    }
    $null = RequiredFile 'qemu/fence/QEMU-SOURCE.txt' ''
}
Write-Output "Third-party gate passed for $Profile ($($manifest.components.Count) components)."

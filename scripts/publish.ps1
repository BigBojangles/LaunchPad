# Builds the self-contained x64 exe, writes SHA256 into the download notes,
# and compiles the Inno Setup installer when ISCC is available.
param(
    [ValidateSet('Full', 'Native')][string]$Profile = 'Full',
    [string]$RuntimeRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
# Packaging never needs an ASP.NET development certificate on the host.
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
# A release cannot silently reuse missing or incomplete compliance material.
& (Join-Path $PSScriptRoot 'verify-third-party.ps1') -Profile $Profile -DistributionRoot $dist
$runtimeInclude = $null
if ($Profile -eq 'Full') {
    $runtimeBuild = Join-Path $root 'src\LaunchPad\obj\package'
    New-Item -ItemType Directory -Force -Path $runtimeBuild | Out-Null
    $payloadPlan = Join-Path $runtimeBuild 'runtime-payload-plan.json'
    if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $dist '.' }
    $RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
    if (-not [string]::Equals($RuntimeRoot.TrimEnd('\','/'), [IO.Path]::GetFullPath($dist), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Full release compliance inventory must describe the same dist runtime the installer packages.'
    }
    & (Join-Path $PSScriptRoot 'package-runtime.ps1') -RuntimeRoot $RuntimeRoot -OutputFile $payloadPlan
    $runtimeInclude = & (Join-Path $PSScriptRoot 'installer-runtime-files.ps1') -PlanFile $payloadPlan -OutputDirectory $runtimeBuild
}

Write-Host 'Publishing LaunchPad (win-x64, self-contained, single file)...'
dotnet publish (Join-Path $root 'src\LaunchPad\LaunchPad.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:NuGetLockFilePath=packages.publish.lock.json `
    -p:RestoreLockedMode=true `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$exe = Join-Path $dist 'LaunchPad.exe'
if (-not (Test-Path $exe)) {
    throw "Publish did not produce $exe"
}

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "SHA256: $hash"

function Update-HashPlaceholder([string]$file) {
    if (-not (Test-Path $file)) { return }
    $text = Get-Content -Raw -Path $file
    $updated = [regex]::Replace($text, '\*{0,2}SHA256:\*{0,2}\s*`?[A-Fa-f0-9]{64}`?|SHA256:\s*PENDING', "SHA256: $hash")
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($file, $updated, $utf8)
}

# README is maintained through Casey's documentation handoff; copy it unchanged.
Update-HashPlaceholder (Join-Path $root 'DOWNLOAD-NOTE.txt')

# Keep a copy next to the exe so testers can verify without hunting.
Copy-Item -Force (Join-Path $root 'README.md') (Join-Path $dist 'README.md')
Copy-Item -Force (Join-Path $root 'DOWNLOAD-NOTE.txt') (Join-Path $dist 'DOWNLOAD-NOTE.txt')
Copy-Item -Force (Join-Path $root 'LICENSE') (Join-Path $dist 'LICENSE')
Copy-Item -Force (Join-Path $root 'NOTICE') (Join-Path $dist 'NOTICE')
Copy-Item -Force (Join-Path $root 'CHANGELOG.md') (Join-Path $dist 'CHANGELOG.md')
Set-Content -Path (Join-Path $dist 'SHA256.txt') -Value $hash -NoNewline

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if (-not $iscc) {
    $guess = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($guess) { $iscc = Get-Command $guess }
}

if ($iscc) {
    Write-Host 'Compiling installer...'
    # ISCC may remove matching old output files, so compile away from live dist.
    $installerOutput = Join-Path $root ('src\LaunchPad\obj\package\installer-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $installerOutput | Out-Null
    $outputSwitch = '/O' + $installerOutput
    if ($Profile -eq 'Native') { & $iscc.Source $outputSwitch '/DNativeOnly=1' (Join-Path $root 'installer\LaunchPad.iss') }
    else { & $iscc.Source $outputSwitch ('/DRuntimeFilesInclude=' + $runtimeInclude) ('/DQemuRuntimeRoot=' + $RuntimeRoot) (Join-Path $root 'installer\LaunchPad.iss') }
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
    $installerBase = if ($Profile -eq 'Native') { 'LaunchPad-Native-Setup' } else { 'LaunchPad-Setup' }
    $outputs = @(Get-ChildItem -LiteralPath $installerOutput -File | Where-Object {
        $_.Name -eq ($installerBase + '.exe') -or $_.Name -match ('^' + [regex]::Escape($installerBase) + '-[0-9]+\.bin$')
    })
    if (-not ($outputs | Where-Object Name -eq ($installerBase + '.exe'))) { throw 'Installer output is missing.' }
    foreach ($output in $outputs) {
        if ($output.Length -ge 2147483648) { throw 'Release assets must be smaller than GitHub''s 2 GiB limit.' }
    }
    foreach ($output in $outputs) { Copy-Item -LiteralPath $output.FullName -Destination (Join-Path $dist $output.Name) -Force }
    # Remove only superseded installer slices, after a complete successful build.
    $stale = @(Get-ChildItem -LiteralPath $dist -File | Where-Object {
        $_.Name -match ('^' + [regex]::Escape($installerBase) + '-[0-9]+\.bin$') -and $_.Name -notin $outputs.Name
    })
    foreach ($old in $stale) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($old.FullName)) -cne [IO.Path]::GetFullPath($dist) -or
            ($old.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe stale installer path.' }
        Remove-Item -LiteralPath $old.FullName
    }
} else {
    if ($Profile -eq 'Full') { throw 'Full packaging requires Inno Setup; an existing installer is not a current build.' }
    Write-Host 'Inno Setup compiler not found. Skipping installer. Install Inno Setup 6 to build LaunchPad-Setup.exe.'
}

if ($Profile -eq 'Native') {
    # Explicit contents: no sibling runtime, guest image, state, credentials or old package.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $nativeZip = Join-Path $dist 'LaunchPad-Native-win-x64.zip'
    $zipStream = [System.IO.File]::Open($nativeZip, [System.IO.FileMode]::Create)
    $zipArchive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('LaunchPad.exe', 'README.md', 'LICENSE', 'NOTICE', 'CHANGELOG.md', 'SHA256.txt')) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, (Join-Path $dist $name), $name) | Out-Null
        }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, (Join-Path $root 'installer\native-only.txt'), 'native-only.txt') | Out-Null
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, (Join-Path $dist 'THIRD-PARTY-NOTICES.txt'), 'THIRD-PARTY-NOTICES.txt') | Out-Null
        foreach ($license in Get-ChildItem -LiteralPath (Join-Path $dist 'licenses') -File -Recurse) {
            $relative = $license.FullName.Substring($dist.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $license.FullName, $relative) | Out-Null
        }
    } finally { $zipArchive.Dispose(); $zipStream.Dispose() }
    $assets = @($nativeZip)
    if ($iscc) { $assets += Join-Path $dist 'LaunchPad-Native-Setup.exe' }
    $checksums = foreach ($asset in $assets) {
        if ((Get-Item -LiteralPath $asset).Length -ge 2147483648) { throw 'Release asset exceeds GitHub file limit.' }
        (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path -Leaf $asset)
    }
    [System.IO.File]::WriteAllLines((Join-Path $dist 'LaunchPad-Native-SHA256.txt'), [string[]]$checksums, [System.Text.UTF8Encoding]::new($false))
}
if ($Profile -eq 'Full') {
    $fullAssets = @((Join-Path $dist 'LaunchPad.exe'), (Join-Path $dist 'LaunchPad-Setup.exe'))
    $fullAssets += @(Get-ChildItem -LiteralPath $dist -Filter 'LaunchPad-Setup-*.bin' -File | Sort-Object Name | ForEach-Object FullName)
    $fullAssets += @('README.md','DOWNLOAD-NOTE.txt','LICENSE','NOTICE','CHANGELOG.md','SHA256.txt') | ForEach-Object { Join-Path $dist $_ }
    $compliance = Get-Content -LiteralPath (Join-Path $dist 'licenses\manifest-full.json') -Raw | ConvertFrom-Json
    $fullAssets += Join-Path $dist $compliance.sourceArchive.path
    $fullAssets += Join-Path $dist 'THIRD-PARTY-NOTICES.txt'
    $fullChecksums = foreach ($asset in $fullAssets) {
        if ((Get-Item -LiteralPath $asset).Length -ge 2147483648) { throw 'Release asset exceeds GitHub file limit.' }
        (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path -Leaf $asset)
    }
    [IO.File]::WriteAllLines((Join-Path $dist 'LaunchPad-Full-SHA256.txt'), [string[]]$fullChecksums, [Text.UTF8Encoding]::new($false))
}

Write-Host "Done. Output is in $dist"

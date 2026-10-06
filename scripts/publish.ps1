# Builds the self-contained x64 exe, writes SHA256 into the download notes,
# and compiles the Inno Setup installer when ISCC is available.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host 'Publishing LaunchPad (win-x64, self-contained, single file)...'
dotnet publish (Join-Path $root 'src\LaunchPad\LaunchPad.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -o $dist

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

Update-HashPlaceholder (Join-Path $root 'README.md')
Update-HashPlaceholder (Join-Path $root 'DOWNLOAD-NOTE.txt')

# Keep a copy next to the exe so testers can verify without hunting.
Copy-Item -Force (Join-Path $root 'README.md') (Join-Path $dist 'README.md')
Copy-Item -Force (Join-Path $root 'DOWNLOAD-NOTE.txt') (Join-Path $dist 'DOWNLOAD-NOTE.txt')
Copy-Item -Force (Join-Path $root 'LICENSE') (Join-Path $dist 'LICENSE')
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
    & $iscc.Source (Join-Path $root 'installer\LaunchPad.iss')
} else {
    Write-Host 'Inno Setup compiler not found. Skipping installer. Install Inno Setup 6 to build LaunchPad-Setup.exe.'
}

Write-Host "Done. Output is in $dist"

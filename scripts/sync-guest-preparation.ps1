param(
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$reports = Join-Path $repository 'tests\LaunchPad.Tests\TestResults\migration'
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$report = [IO.Path]::GetFullPath($ReportDirectory)
foreach ($path in @($candidate,$report)) {
    if (-not $path.StartsWith($reports + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use private migration inputs/reports.' }
}
if (Test-Path -LiteralPath $report) { throw 'Preserve earlier evidence; use a new report directory.' }
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
if ((Hash (Join-Path $candidate 'template.qcow2')) -ne 'f0e308e75c21e88c12b824dddf7494e5e1a5d659ae315327b2497de9458bfbba') { throw 'Reviewed candidate changed.' }
$runtime = [IO.Path]::GetFullPath((Join-Path $repository '..\build-launch-qemu'))
$images = Join-Path $runtime 'images'
$prepare = Join-Path $images 'prepare'
foreach ($path in @($runtime,$images,$prepare,$candidate)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked preparation/input directory.' }
}
$files = @(
    @{ file='bl-proof.sh'; old='eb26905fecbf9bead21ac060c56e8392f949d30276cc4b82363f3775ce8c57ae'; next='4a4b98f6f62c4a3d87f220b0a328413739311d087e0027d7697e415c21b4c1c8' },
    @{ file='usr.local.bin.grok'; old='a0f442d22c2d368be784d944c92e0d585ecec299ddd6311a34a9082192b00d6a'; next='f1730fd378d9a55dcda7db25430d495fcff66155ccf95b8088b23bd46ef9c4da' },
    @{ file='bl-proof.service'; old='b1e6f557e2fd9a2e445f671a31c4b3cb99a23aad53cb2bd2387cc20605a7229d'; next='6e5e05e5a291f1771fe6369436dec685b019f8d7ba4ac44ba8106fe7718fdd2a' },
    @{ file='launchpad-agent'; old=$null; next='edd19e6e8b6e833aa6c2d3b998c543894f912d33e0b0bfaf8c7887503b8468e0' },
    @{ file='quiesce.py'; old=$null; next='db1505bc084d312e786ac0c1ce1b3958679f52c1327bd2cd81a4e646eb0a3497' }
)
foreach ($file in $files) {
    $source = Join-Path $candidate $file.file
    $target = Join-Path $prepare $file.file
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked input file.' }
    if ((Hash $source) -ne $file.next) { throw ('Input changed: ' + $file.file) }
    if ($file.old) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked preparation file.' }
        if ((Hash $target) -ne $file.old) { throw ('Preparation changed: ' + $file.file) }
    } elseif (Test-Path -LiteralPath $target) { throw ('Preserve existing input: ' + $file.file) }
}
$recipePath = Join-Path $prepare 'customize.sh'
if ((Get-Item -LiteralPath $recipePath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked recipe.' }
if ((Hash $recipePath) -ne 'c2137a8f4ef212dfb120c909c452923d2477f9b8792a2aa73f2084eba5d1c8bf') { throw 'Reviewed recipe changed.' }
$recipe = [IO.File]::ReadAllText($recipePath).Replace("`r`n","`n")
$needle = '  --upload "${DIR}/bl-proof.sh:/usr/local/bin/bl-proof.sh" \'
if (($recipe.Split([string[]]@($needle),[StringSplitOptions]::None)).Count -ne 2) { throw 'Unexpected startup upload.' }
$insert = @'
  --run-command "mkdir -p /usr/local/lib/launchpad && chown root:root /usr/local/lib/launchpad && chmod 0755 /usr/local/lib/launchpad" \
  --upload "${DIR}/quiesce.py:/usr/local/lib/launchpad/quiesce.py" \
  --chmod "0750:/usr/local/lib/launchpad/quiesce.py" \
  --run-command "chown root:root /usr/local/lib/launchpad/quiesce.py" \
  --upload "${DIR}/launchpad-agent:/usr/local/bin/launchpad-agent" \
  --chmod "0755:/usr/local/bin/launchpad-agent" \
  --run-command "chown root:root /usr/local/bin/launchpad-agent" \
'@
$recipe = $recipe.Replace($needle,$insert + "`n" + $needle)
$recipe = $recipe.Replace('test -f "$DIR/grok"', 'test -f "$DIR/grok"' + "`n" + 'test -f "$DIR/launchpad-agent"' + "`n" + 'test -f "$DIR/quiesce.py"')
$needle = '  --install apparmor,iptables \'
if (($recipe.Split([string[]]@($needle),[StringSplitOptions]::None)).Count -ne 2) { throw 'Unexpected AppArmor install.' }
$recipe = $recipe.Replace($needle,$needle + "`n" + '  --run-command "apparmor_parser --skip-kernel-load --skip-cache /etc/apparmor.d/usr.local.bin.grok" \')
$recipe = $recipe.Replace("`r`n","`n")
New-Item -ItemType Directory -Path $report | Out-Null
$recipeInput = Join-Path $report 'customize.sh'
[IO.File]::WriteAllText($recipeInput,$recipe,[Text.UTF8Encoding]::new($false))
$files += @{ file='customize.sh'; old='c2137a8f4ef212dfb120c909c452923d2477f9b8792a2aa73f2084eba5d1c8bf'; next=Hash $recipeInput; source=$recipeInput }
# Preserve only the small original inputs, not a template/app duplicate.
foreach ($file in $files) {
    if ($file.old) { Copy-Item -LiteralPath (Join-Path $prepare $file.file) -Destination (Join-Path $report ($file.file + '.before')) }
}
$changed = [Collections.Generic.List[object]]::new()
function InstallBytes($source,$target) {
    $temporary = Join-Path $prepare ('input-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = [IO.File]::ReadAllBytes($source)
        $stream = [IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $target) {
            # Windows PowerShell can coerce a null string argument to an empty
            # path. Supply a real private backup path and preserve .before.
            $replacementBackup = Join-Path $report ([IO.Path]::GetFileName($target) + '.replaced')
            [IO.File]::Replace($temporary,$target,$replacementBackup)
        }
        else { [IO.File]::Move($temporary,$target) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
try {
    foreach ($file in $files) {
        $target = Join-Path $prepare $file.file
        $source = if ($file.source) { $file.source } else { Join-Path $candidate $file.file }
        InstallBytes $source $target
        $changed.Add($file)
        if ((Hash $target) -ne $file.next) { throw ('Installed bytes differ: ' + $file.file) }
    }
} catch {
    $failure = $_
    foreach ($file in $changed) {
        $target = Join-Path $prepare $file.file
        if ($file.old) { InstallBytes (Join-Path $report ($file.file + '.before')) $target }
        else { Remove-Item -LiteralPath $target }
    }
    throw $failure
}
@{ capturedUtc=[DateTime]::UtcNow.ToString('o'); candidate=$candidate; inputs=$files; scope='Preparation inputs only; customize.sh NOT executed, no guest image/session/credentials changed.' } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'preparation-inputs-private.json') -Encoding UTF8
Write-Output 'Pinned guest preparation inputs updated; small originals retained. No image customization was run.'

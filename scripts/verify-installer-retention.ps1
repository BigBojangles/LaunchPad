# Owned Inno-engine retention fixture. Never installs the LaunchPad payload,
# uses its AppId, provisions an account, starts a VM or touches user projects.
param(
    [Parameter(Mandatory = $true)][string]$InnoCompiler,
    [Parameter(Mandatory = $true)][string]$QemuImg
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$phase = Join-Path $workspace 'tests\LaunchPad.Tests\TestResults\migration\installer-retention-20261007'
$fixture = Join-Path $phase ('owned-' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $fixture 'installed'
$seed = Join-Path $fixture 'seed'
$output = Join-Path $fixture 'compiled'
foreach ($path in @($fixture,$installed,$seed,$output)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
$fixtureId = '{' + [guid]::NewGuid().ToString('D').ToUpperInvariant() + '}'
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $fixtureId + '_is1'
if (Test-Path -LiteralPath $registryKey) { throw 'Owned uninstall ID unexpectedly exists.' }
$setup = Get-Content -LiteralPath (Join-Path $workspace 'installer\LaunchPad.iss') -Raw
$lineage = [regex]::Match($setup, '(?m)^UninstallFilesDir=([^\r\n]+)\r?$')
if (-not $lineage.Success -or $lineage.Groups[1].Value -cne '{app}\uninstall-state-v2') { throw 'Expected current shared uninstall-log lineage is missing.' }
$immutableFlags = 'onlyifdoesntexist nocompression uninsneveruninstall'
if (-not (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer-runtime-files.ps1') -Raw).Contains('Flags: ' + $immutableFlags)) {
    throw 'Current immutable image retention flags changed.'
}

function Invoke-Owned([string]$Program, [string[]]$Arguments, [string]$Name, [string]$WorkingDirectory) {
    $quoted = @($Arguments | ForEach-Object {
        if ($_.Contains('"') -or $_.EndsWith('\')) { throw 'Owned fixture argument cannot be quoted safely.' }
        '"' + $_ + '"'
    })
    $stdout = Join-Path $fixture ($Name + '.stdout.log')
    $stderr = Join-Path $fixture ($Name + '.stderr.log')
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.Arguments = $quoted -join ' '
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $worker = [Diagnostics.Process]::Start($start)
    $outputTask = $worker.StandardOutput.ReadToEndAsync(); $errorTask = $worker.StandardError.ReadToEndAsync()
    try {
        if (-not $worker.WaitForExit(30000)) {
            # Windows PowerShell lacks Kill(true). This PID belongs to the
            # process object just created; /T includes its Inno worker children.
            & "$env:SystemRoot\System32\taskkill.exe" /PID $worker.Id /T /F *> (Join-Path $fixture ($Name + '.timeout-cleanup.log'))
            if (-not $worker.WaitForExit(5000)) { throw ('Owned fixture process tree cleanup incomplete: ' + $Name) }
            throw ('Owned fixture process timed out: ' + $Name)
        }
        if ($worker.ExitCode -ne 0) { throw ('Owned fixture process failed: ' + $Name + ', exit ' + $worker.ExitCode) }
    } finally {
        if ($worker.HasExited) {
            [IO.File]::WriteAllText($stdout, $outputTask.GetAwaiter().GetResult())
            [IO.File]::WriteAllText($stderr, $errorTask.GetAwaiter().GetResult())
        }
        $worker.Dispose()
    }
    return $stdout
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Install([string]$Name) {
    Invoke-Owned (Join-Path $output ($Name + '.exe')) @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR=' + $installed),('/LOG=' + (Join-Path $fixture ($Name + '-install.log')))) ($Name + '-install') $fixture | Out-Null
}
function Build([string]$Name, [string]$Version, [bool]$Legacy, [bool]$Native) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in @('[Setup]',('AppId={' + $fixtureId),'AppName=LaunchPad owned retention fixture',('AppVersion=' + $Version),
        ('DefaultDirName=' + $installed),'PrivilegesRequired=lowest','UsePreviousAppDir=no','DisableProgramGroupPage=yes',
        'ArchitecturesAllowed=x64compatible','ArchitecturesInstallIn64BitMode=x64compatible',
        ('OutputDir=' + $output),('OutputBaseFilename=' + $Name),'Compression=none','SetupLogging=yes')) { $lines.Add($line) }
    if (-not $Legacy) { $lines.Add('UninstallFilesDir=' + $lineage.Groups[1].Value) }
    $lines.Add('[Files]')
    $lines.Add('Source: "' + (Join-Path $seed ($Name + '.txt')) + '"; DestDir: "{app}"; DestName: "LaunchPad.exe"; Flags: ignoreversion')
    if (-not $Native) {
        $flags = if ($Legacy) { 'ignoreversion nocompression' } else { $immutableFlags }
        foreach ($image in @('debian-12-nocloud.qcow2','debian-12-builder.qcow2')) {
            $lines.Add('Source: "' + (Join-Path $seed $image) + '"; DestDir: "{app}\images"; Flags: ' + $flags)
        }
    } else {
        $lines.Add('Source: "' + (Join-Path $seed 'native-only.txt') + '"; DestDir: "{app}"; Flags: ignoreversion')
    }
    $source = Join-Path $fixture ($Name + '.iss')
    [IO.File]::WriteAllLines($source, $lines, [Text.UTF8Encoding]::new($false))
    Invoke-Owned $InnoCompiler @('/Qp',$source) ($Name + '-compile') $fixture | Out-Null
}

$complete = $false; $failure = $null; $legacyLogs = @(); $preserved = @(); $chain = $null; $uninstall = $null
try {
    foreach ($name in @('legacy','current-full','current-native','native-only')) { [IO.File]::WriteAllText((Join-Path $seed ($name + '.txt')), 'Owned fixture ' + $name) }
    Invoke-Owned $QemuImg @('create','-f','qcow2','debian-12-nocloud.qcow2','16M') 'base-create' $seed | Out-Null
    Invoke-Owned $QemuImg @('create','-f','qcow2','-F','qcow2','-b','debian-12-nocloud.qcow2','debian-12-builder.qcow2') 'builder-create' $seed | Out-Null
    Build 'legacy' '0.1' $true $false
    Build 'current-full' '0.2' $false $false
    Build 'current-native' '0.3' $false $true
    Install 'legacy'
    $legacyLogs = @(Get-ChildItem -LiteralPath $installed -Filter 'unins*.dat' -File | ForEach-Object { [ordered]@{path=$_.FullName;sha256=(Hash $_.FullName)} })
    if ($legacyLogs.Count -ne 1) { throw 'One legacy uninstall log was not observed.' }
    $session = Join-Path $installed 'sessions\owned'
    New-Item -ItemType Directory -Path $session -Force | Out-Null
    Invoke-Owned $QemuImg @('create','-f','qcow2','-F','qcow2','-b','../../images/debian-12-builder.qcow2','session.qcow2') 'overlay-create' $session | Out-Null
    foreach ($state in @('settings.json','projects.json','credential.fixture')) {
        [IO.File]::WriteAllText((Join-Path $installed $state), 'Owned retained state ' + $state)
    }
    $preserved = @(@('images\debian-12-nocloud.qcow2','images\debian-12-builder.qcow2','sessions\owned\session.qcow2','settings.json','projects.json','credential.fixture') | ForEach-Object {
        $path = Join-Path $installed $_; [ordered]@{path=$path;sha256=(Hash $path)}
    })
    Install 'current-full'
    Install 'current-native'
    foreach ($item in $legacyLogs) { if ((Hash $item.path) -cne $item.sha256) { throw 'Legacy uninstall log was changed.' } }
    if (-not (Test-Path -LiteralPath (Join-Path $installed 'native-only.txt'))) { throw 'Native transition marker missing.' }
    $entry = Get-ItemProperty -LiteralPath $registryKey
    $uninstall = Join-Path $installed 'uninstall-state-v2\unins000.exe'
    if ($entry.UninstallString.Trim('"') -cne $uninstall) { throw 'Current uninstall registration does not select the new lineage.' }
    Invoke-Owned $uninstall @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $fixture 'current-uninstall.log'))) 'current-uninstall' $fixture | Out-Null
    if ((Test-Path -LiteralPath (Join-Path $installed 'LaunchPad.exe')) -or (Test-Path -LiteralPath (Join-Path $installed 'native-only.txt')) -or (Test-Path -LiteralPath $registryKey)) {
        throw 'Current owned app/marker/uninstall registration was not removed.'
    }
    foreach ($item in @($preserved) + @($legacyLogs)) { if ((Hash $item.path) -cne $item.sha256) { throw 'A saved file or legacy uninstall log changed.' } }
    $chainLog = Invoke-Owned $QemuImg @('info','--backing-chain','--output=json',(Join-Path $session 'session.qcow2')) 'retained-chain-info' $fixture
    $parsedChain = Get-Content -LiteralPath $chainLog -Raw | ConvertFrom-Json
    $chain = @($parsedChain)
    if ($chain.Count -ne 3 -or @($chain | Where-Object { $_.format -cne 'qcow2' }).Count -ne 0) { throw 'Owned saved overlay did not retain its complete three-file backing chain.' }
    $complete = $true
} catch { $failure = $_.Exception.ToString(); throw }
finally {
    $proof = [ordered]@{ complete=$complete;failure=$failure;fixture=$fixture;appId=$fixtureId;registryKey=$registryKey;
        uninstall=$uninstall;legacyLogs=$legacyLogs;preserved=$preserved;chain=$chain;
        installerSourceSha256=(Hash (Join-Path $workspace 'installer\LaunchPad.iss'));
        helperSha256=(Hash $PSCommandPath);innoCompilerSha256=(Hash $InnoCompiler);qemuImgSha256=(Hash $QemuImg);
        scope='Actual tiny owned Inno legacy->current Full->Native->current-uninstall engine fixture; real owned qcow backing-chain retention. No LaunchPad payload/AppId install, account, VM boot, actual project/appdata credential or whole installed-runtime acceptance.';
        limits='Historical-only components/logs may remain. The old retained uninstaller is not made safe to invoke manually. No live GUI, clean LaunchPad install/repair/activation, failed-install rollback or complete downgrade proof.' }
    $proof | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixture 'installer-retention-proof-private.json') -Encoding UTF8
    Write-Output ('Owned fixture evidence: ' + $fixture)
}

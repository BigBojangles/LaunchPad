# Invoked ONLY by the audit harness as the actual isolated launch account.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Configuration)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$reportRoot = Split-Path -Parent $Configuration
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$result = [ordered]@{
    schemaVersion = 1
    startedUtc = [DateTime]::UtcNow.ToString('o')
    identity = $identity.Name
    sid = $identity.User.Value
    isAdministrator = $isAdmin
    identityMatches = ($identity.Name.Split('\')[-1] -eq 'BuildLaunchTest' -and !$isAdmin)
    tools = @()
    limitations = @('This standard-user audit does not establish privileged-machine coverage.', 'Raw reports are private and may contain sensitive machine data; do not publish them.')
}
function Assert-Pinned([string]$Id) {
    $artifact = @($config.artifacts | Where-Object { $_.id -eq $Id })
    if ($artifact.Count -ne 1) { throw "Missing unique pin: $Id" }
    $path = $artifact[0].path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
    if ($hash -ne $artifact[0].sha256) { throw "Hash mismatch: $Id" }
    return $path
}
try {
    if (!$result.identityMatches) { throw 'Audit must run as non-administrator BuildLaunchTest.' }
    & whoami.exe /all | Out-File -LiteralPath (Join-Path $reportRoot 'host-identity.txt') -Encoding utf8
    try {
        $winpeas = Assert-Pinned 'winpeas'
        $processStart = [Diagnostics.ProcessStartInfo]::new()
        $processStart.FileName = $winpeas
        $processStart.Arguments = 'notcolor'
        $processStart.WorkingDirectory = $reportRoot
        $processStart.UseShellExecute = $false
        $processStart.CreateNoWindow = $true
        $processStart.RedirectStandardOutput = $true
        $processStart.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($processStart)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(900000)) { $process.Kill(); throw 'WinPEAS timed out; mandatory coverage incomplete.' }
        $stdoutTask.Result | Out-File -LiteralPath (Join-Path $reportRoot 'winpeas-private.txt') -Encoding utf8
        $stderrTask.Result | Out-File -LiteralPath (Join-Path $reportRoot 'winpeas-stderr.txt') -Encoding utf8
        $result.tools += [pscustomobject]@{ id = 'winpeas'; execution = 'finished'; exitCode = $process.ExitCode; coverage = 'requires-review'; findingsTriaged = $false }
        $process.Dispose()
    } catch {
        $result.tools += [pscustomobject]@{ id = 'winpeas'; execution = 'blocked'; exitCode = $null; coverage = 'incomplete'; reason = $_.Exception.Message; findingsTriaged = $false }
    }
    try {
        $module = Assert-Pinned 'hardeningkitty-module'
        $machineList = Assert-Pinned 'hardeningkitty-machine-list'
        $userList = Assert-Pinned 'hardeningkitty-user-list'
        Import-Module $module -Force
        foreach ($list in @(@('machine', $machineList), @('user', $userList))) {
            $prefix = 'hardeningkitty-' + $list[0]
            try {
                Invoke-HardeningKitty -Mode Audit -FileFindingList $list[1] -Log -Report `
                    -LogFile (Join-Path $reportRoot ($prefix + '.log')) `
                    -ReportFile (Join-Path $reportRoot ($prefix + '.csv')) `
                    *> (Join-Path $reportRoot ($prefix + '-console.txt'))
                $csvPath = Join-Path $reportRoot ($prefix + '.csv')
                if (!(Test-Path -LiteralPath $csvPath -PathType Leaf)) { throw 'Audit did not produce its required CSV report.' }
                $result.tools += [pscustomobject]@{ id = $prefix; execution = 'finished'; exitCode = 0; coverage = 'requires-review'; findingsTriaged = $false; report = $csvPath }
            } catch {
                $result.tools += [pscustomobject]@{ id = $prefix; execution = 'blocked'; exitCode = $null; coverage = 'incomplete'; reason = $_.Exception.Message; findingsTriaged = $false }
            }
        }
    } catch {
        $result.tools += [pscustomobject]@{ id = 'hardeningkitty'; execution = 'blocked'; coverage = 'incomplete'; reason = $_.Exception.Message; findingsTriaged = $false }
    }
} catch { $result.error = $_.Exception.Message }
finally {
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result.verdict = 'BLOCKED'
    $result.verdictReason = 'Required coverage and finding triage must be verified; tool execution or exit code alone is not a security pass.'
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'host-audit.json') -Encoding UTF8
}

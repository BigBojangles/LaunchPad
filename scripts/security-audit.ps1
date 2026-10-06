# One cross-platform security entry point. Missing coverage is BLOCKED, never PASS.
[CmdletBinding()]
param(
    [string]$RuntimeRoot,
    [string]$CandidateImage,
    [switch]$DownloadTools,
    [switch]$PrepareOnly,
    [switch]$BoundaryOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$RuntimeRoot) { $RuntimeRoot = Join-Path (Split-Path -Parent $projectRoot) 'build-launch-qemu' }
$selectedCandidateSha256 = $null
if (!$CandidateImage) {
    $runtimeManifest = Join-Path $RuntimeRoot 'images/runtime.json'
    if (Test-Path -LiteralPath $runtimeManifest) {
        $selected = Get-Content -LiteralPath $runtimeManifest -Raw | ConvertFrom-Json
        if ($selected.schema -ne 1 -or $selected.image.file -notmatch '^[A-Za-z0-9._-]+\.qcow2$' -or $selected.image.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid selected runtime identity.' }
        $CandidateImage = Join-Path (Join-Path $RuntimeRoot 'images') $selected.image.file
        $selectedCandidateSha256 = $selected.image.sha256.ToLowerInvariant()
    } else { $CandidateImage = Join-Path $RuntimeRoot 'images/debian-12-builder.qcow2' }
}
$generatedRoot = Join-Path $projectRoot 'tests/LaunchPad.Tests/TestResults'
$runRoot = Join-Path $generatedRoot ('security/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$toolRoot = Join-Path $generatedRoot 'security-tools'
New-Item -ItemType Directory -Force -Path $runRoot, $toolRoot | Out-Null
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$platform = if ($windows) { 'Windows' } elseif ((& uname -s) -eq 'Darwin') { 'macOS' } else { 'Linux' }
$identity = if ($windows) { [Security.Principal.WindowsIdentity]::GetCurrent().Name } else { (& id -un) }
$lockPath = Join-Path $PSScriptRoot 'security-tools.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported security tool lock schema.' }
$report = [ordered]@{
    schemaVersion = 1
    startedUtc = [DateTime]::UtcNow.ToString('o')
    platform = $platform
    orchestratorIdentity = $identity
    toolLockSha256 = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
    candidate = [ordered]@{ path = [IO.Path]::GetFullPath($CandidateImage); sha256 = $null; backingChain = @(); error = $null }
    artifacts = @()
    checks = @()
    findings = @()
    verdict = 'BLOCKED'
    limitations = @('Tool execution and numeric scores alone cannot establish a pass.', 'Private raw reports must remain local; summary contains only identity, coverage, and triage metadata.', 'A template or backing-image hash change invalidates all previous security results.')
}
function Add-Coverage([string]$Id, [string]$Status, [string]$Reason, [string]$Evidence = '') {
    $script:report.checks += [pscustomobject]@{ id = $Id; status = $Status; reason = $Reason; evidence = $Evidence }
}
function Invoke-AuditProcess([string]$Exe, [string[]]$Arguments, [string]$LogFile) {
    # Native stderr (including xUnit failures) must be recorded, not converted by
    # Windows PowerShell into a terminating exception that hides other coverage.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command $Exe -ErrorAction Stop).Source
    # WSL parses quoted option names as commands; quote only arguments that need it.
    $start.Arguments = ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]' -or $_.Length -eq 0) { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    }) -join ' '
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $worker = [Diagnostics.Process]::Start($start)
    $stdout = $worker.StandardOutput.ReadToEndAsync()
    $stderr = $worker.StandardError.ReadToEndAsync()
    try {
        if (!$worker.WaitForExit(1200000)) { $worker.Kill(); throw 'Audit test transport timed out; mandatory coverage incomplete.' }
        [IO.File]::WriteAllText($LogFile, $stdout.Result + $stderr.Result)
        return $worker.ExitCode
    } finally { $worker.Dispose() }
}
function Invoke-AuditTests([string]$Filter, [string]$Results, [string]$LogFile, [string]$TrxName) {
    $arguments = @('test', (Join-Path $projectRoot 'LaunchPad.sln'), '--no-restore', '--filter', $Filter, '--logger', ('trx;LogFileName=' + $TrxName), '--results-directory', $Results)
    return Invoke-AuditProcess 'dotnet' $arguments $LogFile
}
function Test-CompleteTrx([string]$Path, [int]$ExpectedMinimum = 1) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    return [int]$counters.total -ge $ExpectedMinimum -and [int]$counters.passed -eq [int]$counters.total -and [int]$counters.notExecuted -eq 0
}
function ConvertTo-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\') { throw 'WSL audit transport requires local drive paths.' }
    return '/mnt/' + $full.Substring(0,1).ToLowerInvariant() + '/' + $full.Substring(3).Replace('\', '/')
}
try {
    try {
        $report.candidate.sha256 = (Get-FileHash -LiteralPath $CandidateImage -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($selectedCandidateSha256 -and $report.candidate.sha256 -ne $selectedCandidateSha256) { throw 'Selected runtime image hash mismatch.' }
        $qemuImg = Join-Path $RuntimeRoot 'qemu/qemu-img.exe'
        if (!$windows) { $qemuImg = Join-Path $RuntimeRoot 'qemu/qemu-img' }
        if (!(Test-Path -LiteralPath $qemuImg -PathType Leaf)) { throw 'Candidate backing-chain tool missing.' }
        $chainJson = & $qemuImg info --backing-chain --output=json $CandidateImage
        if ($LASTEXITCODE -ne 0) { throw 'Candidate backing chain is unavailable; do not force access or repair it.' }
        foreach ($layer in ($chainJson -join "`n" | ConvertFrom-Json)) {
            $report.candidate.backingChain += [pscustomobject]@{ path = $layer.filename; sha256 = (Get-FileHash -LiteralPath $layer.filename -Algorithm SHA256).Hash.ToLowerInvariant(); virtualBytes = $layer.'virtual-size' }
        }
        if ($windows -and !$PrepareOnly) {
            $privateTemplates = [IO.Path]::GetFullPath((Join-Path $generatedRoot 'migration')) + '\'
            $candidatePath = [IO.Path]::GetFullPath($CandidateImage)
            if ($candidatePath.StartsWith($privateTemplates, [StringComparison]::OrdinalIgnoreCase)) {
                if ((Get-Item -LiteralPath $candidatePath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked private audit candidate.' }
                & icacls.exe $candidatePath /grant 'BuildLaunchTest:R' '*S-1-5-12:R' /Q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Private audit candidate read access could not be established.' }
                if ((Get-FileHash -LiteralPath $candidatePath).Hash.ToLowerInvariant() -ne $report.candidate.sha256) { throw 'Private audit candidate changed while granting read access.' }
                Add-Coverage 'private-candidate-launch-read-access' 'verified' 'Read-only launch access to this pinned private image only; raw reports and production protection unchanged.'
            }
        }
        Add-Coverage 'candidate-identity' 'verified' 'Exact candidate and every backing image hashed.'
    } catch { $report.candidate.error = $_.Exception.Message; Add-Coverage 'candidate-identity' 'blocked' $_.Exception.Message }
    foreach ($artifact in $lock.artifacts) {
        if ($artifact.file -ne [IO.Path]::GetFileName($artifact.file) -or $artifact.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid artifact pin/path in security tool lock.' }
        $path = Join-Path $toolRoot $artifact.file
        $record = [ordered]@{ id = $artifact.id; version = $artifact.version; path = $path; sha256 = $artifact.sha256; verified = $false; error = $null }
        try {
            if ($DownloadTools -and !(Test-Path -LiteralPath $path -PathType Leaf)) {
                # Download only exact pinned files; never execute downloaded installers.
                $uri = [uri]$artifact.url
                if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('github.com', 'raw.githubusercontent.com', 'api.github.com', 'nmap.org')) { throw 'Unapproved audit tool origin.' }
                Invoke-WebRequest -UseBasicParsing -Uri $artifact.url -OutFile $path -Headers @{ 'User-Agent' = 'LaunchPad-security-audit' }
            }
            $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
            if ($actualHash -ne $artifact.sha256) { throw 'SHA256 mismatch; tool will not be executed.' }
            $record.verified = $true
        } catch { $record.error = $_.Exception.Message }
        $report.artifacts += [pscustomobject]$record
    }
    if ($PrepareOnly) {
        Add-Coverage 'execution' 'blocked' 'PrepareOnly: no tools or boundary probes executed.'
    } elseif ($windows -and !$BoundaryOnly) {
        # Stage only the host runner/configuration, never a second application copy.
        $hostRoot = Join-Path $runRoot 'host'
        New-Item -ItemType Directory -Force -Path $hostRoot | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'security-host-windows.ps1') -Destination $hostRoot
        $configPath = Join-Path $hostRoot 'audit-config.json'
        @{ artifacts = $report.artifacts; candidateSha256 = $report.candidate.sha256 } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8
        & icacls.exe $hostRoot /grant 'BuildLaunchTest:(OI)(CI)M' '*S-1-5-12:(OI)(CI)M' /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not grant the launch identity access to its diagnostic directory.' }
        foreach ($artifact in @($report.artifacts | Where-Object { $_.verified -and $_.id -match 'winpeas|hardeningkitty' })) {
            $toolAccess = if ($artifact.id -eq 'winpeas') { 'BuildLaunchTest:RX' } else { 'BuildLaunchTest:R' }
            $restrictedToolAccess = if ($artifact.id -eq 'winpeas') { '*S-1-5-12:RX' } else { '*S-1-5-12:R' }
            & icacls.exe $artifact.path /grant $toolAccess $restrictedToolAccess /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not grant required access to pinned host audit tools.' }
        }
        $oldConfig = $env:LAUNCHPAD_HOST_AUDIT_CONFIG
        try {
            $env:LAUNCHPAD_HOST_AUDIT_CONFIG = $configPath
            $hostExit = Invoke-AuditTests 'FullyQualifiedName~HostSecurityAuditTests' $hostRoot (Join-Path $hostRoot 'test-runner.log') 'host-audit.trx'
        } finally { $env:LAUNCHPAD_HOST_AUDIT_CONFIG = $oldConfig }
        $hostReportPath = Join-Path $hostRoot 'host-audit.json'
        if (Test-Path -LiteralPath $hostReportPath -PathType Leaf) {
            $hostReport = Get-Content -LiteralPath $hostReportPath -Raw | ConvertFrom-Json
            if (!$hostReport.identityMatches) { Add-Coverage 'host-identity' 'failed' 'Wrong launch identity or administrator token.' $hostReportPath }
            else { Add-Coverage 'host-identity' 'verified' 'Non-administrator BuildLaunchTest identity captured.' $hostReportPath }
            foreach ($tool in $hostReport.tools) {
                $status = if ($tool.execution -eq 'finished') { 'untriaged' } else { 'blocked' }
                Add-Coverage ('host-' + $tool.id) $status 'Inspect raw report for findings and inaccessible checks; exit code alone is not coverage.' $hostReportPath
            }
        } else { Add-Coverage 'windows-host-audit' 'blocked' ('No host coverage report; test exit ' + $hostExit) (Join-Path $hostRoot 'test-runner.log') }
        Add-Coverage 'host-privileged-machine' 'blocked' 'Standard-user audit cannot verify privileged machine checks; exclusions must be reviewed explicitly.'
        $oldProbe = $env:LAUNCHPAD_GUEST_PROBE
        $oldRuntime = $env:LAUNCHPAD_QEMU
        $oldRepair = $env:LAUNCHPAD_GUEST_REPAIR
        $oldLinpeas = $env:LAUNCHPAD_GUEST_LINPEAS
        $oldNmap = $env:LAUNCHPAD_GUEST_NMAP
        $oldGuestReport = $env:LAUNCHPAD_GUEST_REPORT_PATH
        $oldRestart = $env:LAUNCHPAD_GUEST_RESTART
        $oldDiagnostic = $env:LAUNCHPAD_GUEST_DIAGNOSTIC
        $oldSecurityTemplate = $env:LAUNCHPAD_SECURITY_TEMPLATE
        $oldSecurityTemplateSha256 = $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256
        try {
            if ($report.candidate.sha256 -and !$report.candidate.error) {
                $env:LAUNCHPAD_GUEST_PROBE = '1'
                $env:LAUNCHPAD_QEMU = $RuntimeRoot
                $env:LAUNCHPAD_SECURITY_TEMPLATE = [IO.Path]::GetFullPath($CandidateImage)
                $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256 = $report.candidate.sha256
                $env:LAUNCHPAD_GUEST_REPAIR = $null
                $env:LAUNCHPAD_GUEST_RESTART = $null
                $env:LAUNCHPAD_GUEST_DIAGNOSTIC = $null
                $env:LAUNCHPAD_GUEST_LINPEAS = $null
                $env:LAUNCHPAD_GUEST_NMAP = $null
                $env:LAUNCHPAD_GUEST_REPORT_PATH = Join-Path $runRoot 'guest-startup-probe-private.json'
                $guestExit = Invoke-AuditTests 'FullyQualifiedName~GuestBaselineTests' $runRoot (Join-Path $runRoot 'guest-test-runner.log') 'guest-startup.trx'
                $startupIdentity = if (Test-Path -LiteralPath $env:LAUNCHPAD_GUEST_REPORT_PATH) { Get-Content -LiteralPath $env:LAUNCHPAD_GUEST_REPORT_PATH -Raw | ConvertFrom-Json } else { $null }
                $status = if ($guestExit -eq 0 -and $startupIdentity.templateSha256 -eq $report.candidate.sha256 -and !$startupIdentity.overlayStartupRepairSha256 -and $startupIdentity.guestShutdownObserved) { 'verified' } else { 'failed' }
                Add-Coverage 'guest-custom-startup-and-agent-versions' $status ('Diagnostic custom-program probe exit ' + $guestExit + '; not interactive bundled-agent or security acceptance.') (Join-Path $runRoot 'guest-startup.trx')
                $linpeasArtifact = @($report.artifacts | Where-Object { $_.id -eq 'linpeas' -and $_.verified })
                if ($status -eq 'verified' -and $linpeasArtifact.Count -eq 1) {
                    $env:LAUNCHPAD_GUEST_LINPEAS = $linpeasArtifact[0].path
                    $env:LAUNCHPAD_GUEST_REPORT_PATH = Join-Path $runRoot 'guest-linpeas-probe-private.json'
                    $linpeasExit = Invoke-AuditTests 'FullyQualifiedName~GuestBaselineTests' $runRoot (Join-Path $runRoot 'guest-linpeas-test-runner.log') 'guest-linpeas.trx'
                    if ($linpeasExit -eq 0 -and (Test-Path -LiteralPath $env:LAUNCHPAD_GUEST_REPORT_PATH)) {
                        $linpeasReport = Get-Content -LiteralPath $env:LAUNCHPAD_GUEST_REPORT_PATH -Raw | ConvertFrom-Json
                        if ($linpeasReport.templateSha256 -eq $report.candidate.sha256 -and !$linpeasReport.overlayStartupRepairSha256 -and $linpeasReport.linpeasSha256 -eq $linpeasArtifact[0].sha256 -and $linpeasReport.guestShutdownObserved) {
                            Add-Coverage 'guest-linpeas-as-agent' 'untriaged' 'Pinned tool completed actual report sections as builder in an unmodified disposable candidate overlay. Custom-program context only; bundled profile parity, exclusions and findings require review.' $env:LAUNCHPAD_GUEST_REPORT_PATH
                        } else { Add-Coverage 'guest-linpeas-as-agent' 'failed' 'Guest/tool identity mismatch or diagnostic startup repair substituted for exact candidate.' $env:LAUNCHPAD_GUEST_REPORT_PATH }
                    } else { Add-Coverage 'guest-linpeas-as-agent' 'failed' ('Required guest report sections did not complete; probe exit ' + $linpeasExit) (Join-Path $runRoot 'guest-linpeas.trx') }
                } else { Add-Coverage 'guest-linpeas-as-agent' 'blocked' 'Exact-candidate startup prerequisite failed or pinned LinPEAS artifact unavailable; repaired trial evidence is separate.' }
                $nmapArtifact = @($report.artifacts | Where-Object { $_.id -eq 'nmap-source' -and $_.verified })
                if ($status -eq 'verified' -and $nmapArtifact.Count -eq 1) {
                    $env:LAUNCHPAD_GUEST_LINPEAS = $null
                    $env:LAUNCHPAD_GUEST_NMAP = $nmapArtifact[0].path
                    $env:LAUNCHPAD_GUEST_REPORT_PATH = Join-Path $runRoot 'guest-nmap-probe-private.json'
                    $nmapExit = Invoke-AuditTests 'FullyQualifiedName~GuestBaselineTests' $runRoot (Join-Path $runRoot 'guest-nmap-test-runner.log') 'guest-nmap.trx'
                    if (Test-Path -LiteralPath $env:LAUNCHPAD_GUEST_REPORT_PATH) {
                        $nmapReport = Get-Content -LiteralPath $env:LAUNCHPAD_GUEST_REPORT_PATH -Raw | ConvertFrom-Json
                        $nmapLine = @($nmapReport.output -split "`n" | Where-Object { $_.StartsWith('NMAP-COVERAGE-JSON:') })
                        if ($nmapLine.Count -eq 1) {
                            $nmapCoverage = $nmapLine[0].Substring('NMAP-COVERAGE-JSON:'.Length) | ConvertFrom-Json
                            $unexpected = @($nmapCoverage.observations | Where-Object { !$_.matchesExpectation -and $_.state -ne 'incomplete' })
                            if ($unexpected.Count -gt 0 -or $nmapReport.hostControlUnexpectedConnection) {
                                Add-Coverage 'guest-nmap-controlled-targets' 'failed' 'Owned TCP controls differ from expected connectivity; inspect distinct states/reasons and host listener evidence.' $env:LAUNCHPAD_GUEST_REPORT_PATH
                            } elseif ($nmapExit -eq 0 -and $nmapReport.templateSha256 -eq $report.candidate.sha256 -and !$nmapReport.overlayStartupRepairSha256 -and $nmapReport.nmapSourceSha256 -eq $nmapArtifact[0].sha256 -and $nmapReport.hostControlAliveAfter -and $nmapCoverage.boundaryAssertionsPassed -and $nmapReport.guestShutdownObserved) {
                                Add-Coverage 'guest-nmap-controlled-targets' 'verified' 'Three owned TCP controls match the explicit manifest in actual builder context. Distinct states/reasons captured; this does not establish all bundled-agent, child-process or host confinement.' $env:LAUNCHPAD_GUEST_REPORT_PATH
                            } else { Add-Coverage 'guest-nmap-controlled-targets' 'blocked' 'Nmap controls incomplete or image/tool/host-listener provenance not verified.' $env:LAUNCHPAD_GUEST_REPORT_PATH }
                        } else { Add-Coverage 'guest-nmap-controlled-targets' 'blocked' 'Pinned Nmap did not complete actual scan reports; inspect diagnostic build/probe errors.' $env:LAUNCHPAD_GUEST_REPORT_PATH }
                    } else { Add-Coverage 'guest-nmap-controlled-targets' 'blocked' ('No controlled-connectivity report; probe exit ' + $nmapExit) (Join-Path $runRoot 'guest-nmap.trx') }
                } else { Add-Coverage 'guest-nmap-controlled-targets' 'blocked' 'Exact-candidate startup prerequisite failed or pinned Nmap source unavailable.' }
            }
        } finally { $env:LAUNCHPAD_GUEST_PROBE = $oldProbe; $env:LAUNCHPAD_QEMU = $oldRuntime; $env:LAUNCHPAD_GUEST_REPAIR = $oldRepair; $env:LAUNCHPAD_GUEST_LINPEAS = $oldLinpeas; $env:LAUNCHPAD_GUEST_NMAP = $oldNmap; $env:LAUNCHPAD_GUEST_REPORT_PATH = $oldGuestReport; $env:LAUNCHPAD_GUEST_RESTART = $oldRestart; $env:LAUNCHPAD_GUEST_DIAGNOSTIC = $oldDiagnostic; $env:LAUNCHPAD_SECURITY_TEMPLATE = $oldSecurityTemplate; $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256 = $oldSecurityTemplateSha256 }
    } elseif (!$BoundaryOnly) {
        Add-Coverage 'host-identity' 'blocked' 'Native launch-identity adapter and physical host verification are required.'
        if ($platform -eq 'macOS') {
            Add-Coverage 'host-lynis' 'blocked' 'Darwin host transport not implemented yet; Windows-only tools are not substitutes.'
            Add-Coverage 'host-darwin-peass' 'blocked' 'Supported Darwin PEASS coverage must run as actual launch identity.'
        }
    }
    if (!$PrepareOnly -and !$BoundaryOnly -and $windows -and $report.candidate.sha256 -and !$report.candidate.error) {
        $trivyArtifact = @($report.artifacts | Where-Object { $_.id -eq 'trivy-linux-x64' -and $_.verified })
        if ($trivyArtifact.Count -eq 1) {
            $trivyRoot = Join-Path $runRoot 'trivy'
            $trivyArgs = @('-d', 'Ubuntu', '--', 'bash',
                (ConvertTo-WslPath (Join-Path $PSScriptRoot 'security-trivy-rootfs.sh')),
                (ConvertTo-WslPath $CandidateImage), (ConvertTo-WslPath $trivyArtifact[0].path),
                (ConvertTo-WslPath $trivyRoot), (ConvertTo-WslPath (Join-Path $toolRoot 'trivy-cache')))
            $trivyExit = Invoke-AuditProcess 'wsl.exe' $trivyArgs (Join-Path $runRoot 'trivy-runner.log')
            $trivyCoveragePath = Join-Path $trivyRoot 'trivy-coverage.json'
            if ($trivyExit -eq 0 -and (Test-Path -LiteralPath $trivyCoveragePath -PathType Leaf)) {
                $trivyCoverage = Get-Content -LiteralPath $trivyCoveragePath -Raw | ConvertFrom-Json
                if ($trivyCoverage.candidateSha256 -ne $report.candidate.sha256) {
                    Add-Coverage 'guest-trivy-rootfs-and-agents' 'failed' 'Scanner report identity differs from exact candidate.' $trivyCoveragePath
                } else {
                    Add-Coverage 'guest-trivy-rootfs-and-agents' 'untriaged' 'Read-only rootfs scan finished; package coverage, database freshness, binary exclusions, and findings require review.' $trivyCoveragePath
                    & (Join-Path $PSScriptRoot 'security-triage.ps1') -ScanReport (Join-Path $trivyRoot 'trivy-rootfs-private.json') -OutputDirectory (Join-Path $runRoot 'triage') | Out-Null
                }
            } else { Add-Coverage 'guest-trivy-rootfs-and-agents' 'blocked' ('Rootfs scanner incomplete; exit ' + $trivyExit) (Join-Path $runRoot 'trivy-runner.log') }
        } else { Add-Coverage 'guest-trivy-rootfs-and-agents' 'blocked' 'Pinned rootfs scanner artifact missing or hash not verified.' }
    } else { Add-Coverage 'guest-trivy-rootfs-and-agents' 'blocked' 'Rootfs scanner omitted in this partial/preparation run or unavailable on this platform.' }
    if (!$PrepareOnly -and !$BoundaryOnly -and $windows -and $report.candidate.sha256 -and !$report.candidate.error) {
        $lynisArtifact = @($report.artifacts | Where-Object { $_.id -eq 'lynis-source' -and $_.verified })
        if ($lynisArtifact.Count -eq 1) {
            $oldLynis = $env:LAUNCHPAD_GUEST_LYNIS
            $oldLynisReport = $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH
            $oldLynisRuntime = $env:LAUNCHPAD_QEMU
            $oldLynisTemplate = $env:LAUNCHPAD_SECURITY_TEMPLATE
            $oldLynisTemplateSha256 = $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256
            try {
                $env:LAUNCHPAD_GUEST_LYNIS = $lynisArtifact[0].path
                $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH = Join-Path $runRoot 'guest-lynis-coverage-private.json'
                $env:LAUNCHPAD_QEMU = $RuntimeRoot
                $env:LAUNCHPAD_SECURITY_TEMPLATE = [IO.Path]::GetFullPath($CandidateImage)
                $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256 = $report.candidate.sha256
                $lynisExit = Invoke-AuditTests 'FullyQualifiedName~GuestSystemSecurityTests.LynisAuditsTheRunningGuestWithExplicitPrivilegedDiagnosticSetup' $runRoot (Join-Path $runRoot 'guest-lynis-test-runner.log') 'guest-lynis.trx'
                if ($lynisExit -eq 0 -and (Test-Path -LiteralPath $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH)) {
                    $lynisReport = Get-Content -LiteralPath $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH -Raw | ConvertFrom-Json
                    if ($lynisReport.templateSha256 -eq $report.candidate.sha256 -and $lynisReport.toolSha256 -eq $lynisArtifact[0].sha256 -and $lynisReport.candidateUnchanged -and $lynisReport.productionRuntimeInitializationObserved -and !$lynisReport.agentSudoGranted -and $lynisReport.guestShutdownObserved) {
                        Add-Coverage 'guest-lynis-system' 'untriaged' 'Real guest-system audit completed with explicitly privileged disposable-overlay setup; report coverage/exclusions and findings require review.' $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH
                    } else { Add-Coverage 'guest-lynis-system' 'failed' 'System-audit template/tool identity mismatch or agent privileges changed.' $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH }
                } else { Add-Coverage 'guest-lynis-system' 'blocked' ('Required guest-system audit incomplete; exit ' + $lynisExit) (Join-Path $runRoot 'guest-lynis-test-runner.log') }
            } finally { $env:LAUNCHPAD_GUEST_LYNIS = $oldLynis; $env:LAUNCHPAD_GUEST_LYNIS_REPORT_PATH = $oldLynisReport; $env:LAUNCHPAD_QEMU = $oldLynisRuntime; $env:LAUNCHPAD_SECURITY_TEMPLATE = $oldLynisTemplate; $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256 = $oldLynisTemplateSha256 }
        } else { Add-Coverage 'guest-lynis-system' 'blocked' 'Pinned Lynis source artifact missing or hash not verified.' }
    } else { Add-Coverage 'guest-lynis-system' 'blocked' 'System scanner omitted in this partial/preparation run or unavailable for this platform/candidate.' }
    # Mandatory guest transports remain explicit until implemented and verified.
    # Baseline startup failure must not be hidden behind successful host tools.
    if (@($report.checks | Where-Object id -eq 'guest-linpeas-as-agent').Count -eq 0) {
        Add-Coverage 'guest-linpeas-as-agent' 'blocked' 'Native guest execution prerequisites unavailable on this platform/candidate.'
    }
    if (@($report.checks | Where-Object id -eq 'guest-nmap-controlled-targets').Count -eq 0) {
        Add-Coverage 'guest-nmap-controlled-targets' 'blocked' 'Native controlled guest-connectivity prerequisites unavailable on this platform/candidate.'
    }
    if (!$PrepareOnly -and $windows -and $report.candidate.sha256 -and !$report.candidate.error) {
        $oldBoundaryEnvironment = @{}
        foreach ($name in @('LAUNCHPAD_QEMU','LAUNCHPAD_SECURITY_TEMPLATE','LAUNCHPAD_SECURITY_TEMPLATE_SHA256','LAUNCHPAD_AGENT_TOOLS','LAUNCHPAD_AGENT_REPORT_PATH')) {
            $oldBoundaryEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        }
        try {
            $env:LAUNCHPAD_QEMU = $RuntimeRoot
            $env:LAUNCHPAD_SECURITY_TEMPLATE = [IO.Path]::GetFullPath($CandidateImage)
            $env:LAUNCHPAD_SECURITY_TEMPLATE_SHA256 = $report.candidate.sha256
            $env:LAUNCHPAD_AGENT_TOOLS = '1'
            $env:LAUNCHPAD_AGENT_REPORT_PATH = Join-Path $runRoot 'bundled-boundaries-private.json'
            $boundaryExit = Invoke-AuditTests 'FullyQualifiedName~GuestAgentToolsTests.ActualBundledAgentsExecuteControlledChildCommandsUnderThePolicy' $runRoot (Join-Path $runRoot 'bundled-boundaries-runner.log') 'bundled-boundaries.trx'
            if ($boundaryExit -eq 0 -and (Test-CompleteTrx (Join-Path $runRoot 'bundled-boundaries.trx')) -and (Test-Path -LiteralPath $env:LAUNCHPAD_AGENT_REPORT_PATH)) {
                $boundary = Get-Content -LiteralPath $env:LAUNCHPAD_AGENT_REPORT_PATH -Raw | ConvertFrom-Json
                if ($boundary.templateSha256 -eq $report.candidate.sha256 -and $boundary.templateUnchanged -and $boundary.boundariesPassed -and $boundary.guestShutdownObserved -and $boundary.launchIdentity -eq 'BuildLaunchTest' -and (@($boundary.agents | Sort-Object) -join ',') -eq 'claude,codex,grok' -and $boundary.hostListener.LiveWitness -and $boundary.hostListener.unexpectedConnections -eq 0) {
                    Add-Coverage 'bundled-agents-and-children-boundaries' 'verified' 'Three actual native CLI shell tools and child ancestry satisfy owned project/config writes, system/device/other-project denials, inherited enforcement and the live owned host-gateway control. Controlled local model responses; no authenticated history or all-network claim.' $env:LAUNCHPAD_AGENT_REPORT_PATH
                } else { Add-Coverage 'bundled-agents-and-children-boundaries' 'failed' 'Boundary image, identity, actual agent set, shutdown or host-listener witness mismatch.' $env:LAUNCHPAD_AGENT_REPORT_PATH }
            } else { Add-Coverage 'bundled-agents-and-children-boundaries' 'failed' ('Mandatory actual-agent probe incomplete; exit ' + $boundaryExit) (Join-Path $runRoot 'bundled-boundaries.trx') }
            $returnExit = Invoke-AuditTests 'FullyQualifiedName~ReturnSafetyTests' $runRoot (Join-Path $runRoot 'return-safety-runner.log') 'return-safety.trx'
            if ($returnExit -eq 0 -and (Test-CompleteTrx (Join-Path $runRoot 'return-safety.trx') 30)) {
                Add-Coverage 'return-safety-host-failure-fixtures' 'verified' 'Actual host receiver/applier fixtures cover malformed/truncated/unsafe returns, links, rejected/unavailable scans, host conflicts, rollback and preserved recovery. Scanner results are injected; this is not actual AMSI or installed-package acceptance.' (Join-Path $runRoot 'return-safety.trx')
            } else { Add-Coverage 'return-safety-host-failure-fixtures' 'failed' ('Host return failure fixtures incomplete; exit ' + $returnExit) (Join-Path $runRoot 'return-safety.trx') }
            Add-Coverage 'return-safety-and-recovery' 'blocked' 'Host failure fixtures cover receiver/apply guards; actual native scanner and complete exact-candidate live return/recovery acceptance remain required.' (Join-Path $runRoot 'return-safety.trx')
        } finally {
            foreach ($name in $oldBoundaryEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldBoundaryEnvironment[$name]) }
        }
    } else {
        Add-Coverage 'bundled-agents-and-children-boundaries' 'blocked' 'Exact-candidate native probe unavailable on this platform or PrepareOnly.'
        Add-Coverage 'return-safety-and-recovery' 'blocked' 'Required native host and exact-candidate guest return checks have not run.'
    }
    Add-Coverage 'custom-host-isolation' 'blocked' 'Custom builder network control is covered separately; complete native host file/process boundary acceptance remains required.'
    if ($BoundaryOnly) {
        Add-Coverage 'scanner-execution' 'blocked' 'BoundaryOnly intentionally omits host and guest scanners. This partial run cannot pass the full security suite.'
    }
} catch { Add-Coverage 'runner' 'blocked' $_.Exception.Message }
finally {
    if ($report.candidate.backingChain.Count -gt 0 -and !$report.candidate.error) {
        try {
            foreach ($layer in $report.candidate.backingChain) {
                if ((Get-FileHash -LiteralPath $layer.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $layer.sha256) { throw 'Candidate or backing image changed during audit.' }
            }
            Add-Coverage 'candidate-final-identity' 'verified' 'Candidate and every backing image remain byte-identical after all probes.'
        } catch { Add-Coverage 'candidate-final-identity' 'failed' $_.Exception.Message }
    }
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    if (@($report.checks | Where-Object { $_.status -eq 'failed' }).Count -gt 0) { $report.verdict = 'FAIL' }
    # No blanket PASS path: completion requires each mandatory adapter and triage.
    $destination = Join-Path $runRoot 'security-summary.json'
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding UTF8
    Write-Output ('Security verdict: ' + $report.verdict)
    Write-Output ('Private summary: ' + $destination)
}
if ($report.verdict -eq 'FAIL') { exit 1 }
exit 2

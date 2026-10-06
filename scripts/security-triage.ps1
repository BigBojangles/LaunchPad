# Produce a private review queue; never infer a waiver from scanner exit codes.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScanReport, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$generatedRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/LaunchPad.Tests/TestResults'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (!$outputRoot.StartsWith($generatedRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Private triage output must remain under generated TestResults.'
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$scan = Get-Content -LiteralPath $ScanReport -Raw | ConvertFrom-Json
$rows = @(
    foreach ($target in $scan.Results) {
        foreach ($finding in $target.Vulnerabilities) {
            if ($finding.Severity -notin @('HIGH', 'CRITICAL')) { continue }
            [pscustomobject][ordered]@{
                target = $target.Target
                package = $finding.PkgName
                installedVersion = $finding.InstalledVersion
                vulnerabilityId = $finding.VulnerabilityID
                severity = $finding.Severity
                fixedVersion = $finding.FixedVersion
                vendorStatus = $finding.Status
                dataSource = $finding.DataSource.ID
                primaryUrl = $finding.PrimaryURL
                disposition = 'unreviewed'
                applicabilityEvidence = ''
                remediation = ''
                riskExceptionOwner = ''
                riskExceptionRationale = ''
            }
        }
    }
)
$rows = @($rows | Sort-Object severity,vulnerabilityId,package,target -Unique)
$destination = Join-Path $outputRoot 'high-critical-review-private.csv'
if (Test-Path -LiteralPath $destination) {
    throw 'A review queue already exists; refusing to overwrite review dispositions. Choose a new generated output directory.'
}
$rows | Export-Csv -LiteralPath $destination -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    schemaVersion = 1
    sourceReportSha256 = (Get-FileHash -LiteralPath $ScanReport -Algorithm SHA256).Hash.ToLowerInvariant()
    candidateIdentityMustBeTakenFromScannerCoverage = $true
    queue = $destination
    reviewEntries = $rows.Count
    distinctVulnerabilityIds = @($rows.vulnerabilityId | Sort-Object -Unique).Count
    unreviewedEntries = $rows.Count
    verdict = 'BLOCKED'
    reason = 'Every High/Critical entry needs vendor applicability evidence, remediation or a documented Casey-approved risk exception. The queue is not a vulnerability verdict.'
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'triage-summary.json') -Encoding UTF8
Write-Output ('High/Critical review entries: ' + $rows.Count)
Write-Output ('Private queue: ' + $destination)

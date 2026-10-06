param(
    [Parameter(Mandatory=$true)][string[]]$Before,
    [Parameter(Mandatory=$true)][string[]]$After,
    [Parameter(Mandatory=$true)][string]$Report,
    [string]$ExpectedBeforeAssemblySha256,
    [string]$ExpectedAfterAssemblySha256,
    [hashtable]$ExpectedAfterAssemblyByAgent
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$reports = [IO.Path]::GetFullPath((Join-Path $repository 'tests\LaunchPad.Tests\TestResults')) + '\'
$output = [IO.Path]::GetFullPath($Report)
if (-not $output.StartsWith($reports, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the comparison in private generated reports.' }
if (Test-Path -LiteralPath $output) { throw 'Retain the previous comparison; use a new report path.' }
function Load-Series([string[]]$Paths) {
    $series = @{}
    foreach ($inputPath in $Paths) {
        $full = (Resolve-Path -LiteralPath $inputPath).Path
        if (-not $full.StartsWith($reports, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use private generated measurements.' }
        $data = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($data.agent -notin @('grok','codex','claude') -or $series.ContainsKey($data.agent)) { throw 'Each bundled agent needs one unique complete series.' }
        if ($data.samples.Count -ne 8 -or @($data.samples | Where-Object launchKind -eq 'fresh').Count -ne 3 -or @($data.samples | Where-Object launchKind -eq 'repeat').Count -ne 5) { throw 'Require three fresh and five repeat samples.' }
        if (@($data.samples | Where-Object { -not $_.observedScreen -or -not $_.guestShutdownObserved }).Count -ne 0) { throw 'Missing startup or guest shutdown proof.' }
        if ($data.expectedFresh -ne 3 -or $data.expectedRepeat -ne 5 -or -not $data.coverageComplete) { throw 'Incomplete daily series.' }
        if ($data.fixtureFiles -ne 1001 -or $data.fixturePayloadBytes -ne 16384000) { throw 'Unexpected daily fixture.' }
        $series[$data.agent] = @{ path=$full; identity=(Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant(); data=$data }
    }
    if ($series.Count -ne 3) { throw 'Require all three bundled agents for a shared-launch change.' }
    return $series
}
function Statistics($samples, [string]$stage) {
    $values = @($samples | ForEach-Object {
        if ($null -eq $_.stages.$stage) { throw "Missing required stage $stage" }
        $value = [double]$_.stages.$stage
        if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) { throw 'Invalid measurement.' }
        $value
    } | Sort-Object)
    $middle = [int][Math]::Floor($values.Count / 2)
    $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle-1] + $values[$middle]) / 2 }
    return @{ count=$values.Count; median=$median; max=$values[-1] }
}
$prior = Load-Series $Before
$candidate = Load-Series $After
$rows = @()
$identities = @()
$stages = @('qemuStartToGuestHandoffReadySeconds','transferFirstByteToImportObservedSeconds','agentSelectedObservedToInitialScreenSeconds','qemuStartToInitialScreenSeconds')
foreach ($agent in @('grok','codex','claude')) {
    $old = $prior[$agent].data; $new = $candidate[$agent].data
    $expectedAfter = if ($ExpectedAfterAssemblyByAgent -and $ExpectedAfterAssemblyByAgent.ContainsKey($agent))
        { [string]$ExpectedAfterAssemblyByAgent[$agent] } else { $ExpectedAfterAssemblySha256 }
    if ($old.assemblySha256 -ne $new.assemblySha256) {
        if ($ExpectedBeforeAssemblySha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $expectedAfter -notmatch '^[A-Fa-f0-9]{64}$' -or
            $old.assemblySha256 -ne $ExpectedBeforeAssemblySha256 -or $new.assemblySha256 -ne $expectedAfter) {
            throw 'For an intentional code change, pin both measured build hashes explicitly.'
        }
    }
    foreach ($property in @('hostOs','hostArchitecture','hostProcessor','hostLogicalProcessors','hostInstalledMemoryMegabytes','fixtureFiles','fixturePayloadBytes','fixtureBlockSha256','shutdownMode')) {
        if ($null -eq $old.$property -or $old.$property -ne $new.$property) { throw "Incomparable $agent series: $property differs or is missing." }
    }
    $identities += @{ agent=$agent; before=$prior[$agent].path; beforeReportSha256=$prior[$agent].identity; beforeTemplateSha256=$old.templateSha256; beforeAssemblySha256=$old.assemblySha256; after=$candidate[$agent].path; afterReportSha256=$candidate[$agent].identity; afterTemplateSha256=$new.templateSha256; afterAssemblySha256=$new.assemblySha256 }
    foreach ($kind in @('fresh','repeat')) {
        foreach ($stage in $stages) {
            $previous = Statistics @($old.samples | Where-Object launchKind -eq $kind) $stage
            $current = Statistics @($new.samples | Where-Object launchKind -eq $kind) $stage
            $percent = if ($previous.median -gt 0) { 100 * ($current.median / $previous.median - 1) } else { $null }
            $investigate = if ($null -ne $percent) { $percent -gt 10 } else { $current.median -gt 0 }
            $rows += @{ agent=$agent; kind=$kind; stage=$stage; before=$previous; after=$current; medianPercentChange=$percent; investigate=$investigate }
        }
    }
}
$findings = @($rows | Where-Object investigate)
$result = @{ recordedUtc=[DateTime]::UtcNow.ToString('O'); verdict=$(if($findings.Count){'INVESTIGATE'}else{'NO_OBSERVED_MEDIAN_REGRESSION'}); identities=$identities; comparisons=$rows; investigationCount=$findings.Count;
    limitations='Daily medians/max only; fresh overlay is not cold-host boot, and initial onboarding is not a signed-in usable prompt. A >10% median increase requires investigation and one comparable rerun; this report alone does not confirm a regression or prove optimization, RC tails, app startup, return, installer or full acceptance.' }
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output "$($result.verdict): $($findings.Count) stage/launch-kind comparisons need investigation."
$findings | ForEach-Object { Write-Output ('{0} {1} {2}: {3:N1}%' -f $_.agent,$_.kind,$_.stage,$_.medianPercentChange) }

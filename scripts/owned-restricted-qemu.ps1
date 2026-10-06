# Private owned-fixture diagnostic; never runs an existing project disk.
param([Parameter(Mandatory)][string]$Configuration)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$root = Split-Path -Parent $Configuration
$report = [ordered]@{ identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name; tests=@(); finished=$false }
try {
    if($report.identity.Split('\')[-1] -ne 'BuildLaunchTest') { throw 'Actual launch identity required' }
    foreach($probe in $config.probes) {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $config.exe
        $start.Arguments = $probe.arguments
        $start.WorkingDirectory = $config.directory
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $entry = [ordered]@{name=$probe.name;pid=$process.Id;startUtc=$process.StartTime.ToUniversalTime().ToString('o');exited=$false;exitCode=$null;stdout=$null;stderr=$null;stopped=$false}
        try {
            $entry.exited = $process.WaitForExit(5000)
            if($entry.exited) { $entry.exitCode=$process.ExitCode }
        } finally {
            if(!$process.HasExited) { $process.Kill(); $entry.stopped=$process.WaitForExit(5000) }
            $entry.stdout=$stdout.GetAwaiter().GetResult()
            $entry.stderr=$stderr.GetAwaiter().GetResult()
            $process.Dispose()
            $report.tests += [pscustomobject]$entry
            $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $root 'qemu-diagnostic-private.json') -Encoding UTF8
        }
    }
    $report.finished=$true
} catch { $report.error=$_.Exception.ToString(); throw }
finally { $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $root 'qemu-diagnostic-private.json') -Encoding UTF8 }

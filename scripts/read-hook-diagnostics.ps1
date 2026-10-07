param([Parameter(Mandatory=$true)][string]$SessionDirectory)
$ErrorActionPreference = 'Stop'
$file = Join-Path $SessionDirectory 'hook-diagnostics.jsonl'
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Write-Output 'No hook diagnostic receipts yet.'; exit 0 }
if ((Get-Item -LiteralPath $file).Length -gt 2097152) { throw 'Diagnostic journal exceeds the bounded capture size.' }
Get-Content -LiteralPath $file | ForEach-Object {
    $receipt = $_ | ConvertFrom-Json
    $event = $receipt.diagnostic
    [pscustomobject]@{ Received=$receipt.receivedUtc; Event=$event.Event; Notification=$event.NotificationType;
        Session=$event.SessionHash; StopHookActive=$event.StopHookActive;
        BackgroundTasks=$event.BackgroundTasksCount; Fields=($event.Fields.PSObject.Properties.Name -join ', ') }
} | Format-Table -AutoSize

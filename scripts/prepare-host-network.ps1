param(
    [string]$RuntimeRoot,
    [string]$ApplicationPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path (Split-Path $repository -Parent) 'build-launch-qemu' }
if (-not $ApplicationPath) { $ApplicationPath = Join-Path $repository 'src/LaunchPad/bin/Debug/net8.0/LaunchPad.exe' }
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
if ((Split-Path $application -Leaf) -ne 'LaunchPad.exe') { throw 'Use the reviewed LaunchPad helper.' }
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'qemu') -PathType Container)) { throw 'QEMU runtime is missing.' }
# Windows performs the administrator approval. The helper atomically installs
# only its persistent outbound local-traffic filters for these QEMU paths.
# No account, image, antivirus, global firewall mode or existing rule is changed.
$worker = Start-Process -FilePath $application -ArgumentList ('--prepare-host-network "' + $runtime + '"') -Verb RunAs -Wait -PassThru -WindowStyle Hidden
if ($worker.ExitCode -ne 0) { throw 'Scoped host-network setup failed; fenced launches remain stopped. See the LaunchPad setup log.' }
Write-Output 'Persistent QEMU host-network filters prepared. Live boundary verification remains required.'

# Install Casey's prepared logo pack verbatim. No resizing, tracing or ICO generation.
param([string]$PackRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Logo'))
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repository 'src\LaunchPad\Assets'
$copies = @(
    @{source='rocket\launchpad-rocket-256.png';target=(Join-Path $assets 'launchpad-rocket.png')},
    @{source='icon\icon-64.png';target=(Join-Path $assets 'launchpad-icon-64.png')},
    @{source='icon\LaunchPad.ico';target=(Join-Path $assets 'LaunchPad.ico')},
    @{source='hero\launchpad-hero-dark.png';target=(Join-Path $repository 'docs\launchpad-lockup.png')}
)
foreach ($copy in $copies) {
    $copy.source = (Resolve-Path -LiteralPath (Join-Path $PackRoot $copy.source)).Path
}
foreach ($copy in $copies) {
    Copy-Item -LiteralPath $copy.source -Destination $copy.target -Force
    $expected = (Get-FileHash -LiteralPath $copy.source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $copy.target -Algorithm SHA256).Hash -ne $expected) {
        throw 'Copied logo asset differs from the supplied pack.'
    }
    [pscustomobject]@{source=$copy.source;target=$copy.target;sha256=$expected.ToLowerInvariant();unchangedBytes=$true}
}

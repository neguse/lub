param(
    [switch] $NoPrecheck,
    [switch] $Help
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$Project = Join-Path $RepoRoot "samples\analyzer-demo\analyzer-demo.csproj"

. (Join-Path $ScriptDir "rider-env.ps1")

function Show-Usage {
    Write-Host @"
Usage: samples/analyzer-demo/open-rider-demo.ps1 [-NoPrecheck]

Runs the Rider verification pre-checks, then opens analyzer-demo.csproj in Rider.

Options:
  -NoPrecheck  Open Rider without running verify-rider-prechecks.ps1 first.
  -Help        Show this help.

Environment:
  TCS_RIDER_COMMAND=C:\path\to\rider64.exe  Override Rider command detection.
"@
}

if ($Help) {
    Show-Usage
    exit 0
}

if (-not $NoPrecheck) {
    & (Join-Path $ScriptDir "verify-rider-prechecks.ps1")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$riderCommand = Find-RiderCommand
if (-not $riderCommand) {
    Write-Error "Rider command not found. Set TCS_RIDER_COMMAND=C:\path\to\rider64.exe or install Rider in a standard JetBrains Toolbox location."
    exit 1
}

if (-not (Test-RiderUiLaunchable)) {
    Write-Error "No GUI display was detected from this shell. Rerun this script from the desktop session."
    exit 1
}

Write-Host "Opening $Project"
Write-Host "Rider command: $riderCommand"
Start-Process -FilePath $riderCommand -ArgumentList @("`"$Project`"") | Out-Null

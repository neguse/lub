param(
    [string]$BuildDir = "build-release",
    [string]$Backend = "",
    [int]$ScoreFrame = 3600,
    [int]$Burst = 1,
    [double]$TargetFps = 60.0,
    [int]$MaxSprites = 200000,
    [switch]$Profile,
    [int]$ProfileWindow = 300,
    [switch]$NoBuild,
    [string]$VcvarsPath = "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat"
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BuildPath = if ([System.IO.Path]::IsPathRooted($BuildDir)) {
    $BuildDir
} else {
    Join-Path $RepoRoot $BuildDir
}
$SamplePath = Join-Path $RepoRoot "samples\13_sprites\13_sprites.hxml"
$ExePath = Join-Path $BuildPath "lub.exe"

Push-Location $RepoRoot
try {
    if (-not $NoBuild) {
        & (Join-Path $PSScriptRoot "build-release.ps1") -BuildDir $BuildDir -Target lub -VcvarsPath $VcvarsPath
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "lub.exe was not found: $ExePath. Run without -NoBuild first."
    }
    if ($env:LUB_HAXE) {
        if (-not (Test-Path -LiteralPath $env:LUB_HAXE)) {
            throw "LUB_HAXE points to a missing haxe executable: $env:LUB_HAXE"
        }
        $haxePath = (Resolve-Path -LiteralPath $env:LUB_HAXE).Path
        $haxeDir = Split-Path -Parent $haxePath
        if ($haxeDir) {
            $env:PATH = "$haxeDir;$env:PATH"
        }
    } elseif (-not (Get-Command haxe -ErrorAction SilentlyContinue)) {
        throw "haxe was not found. Install Haxe 5 or set LUB_HAXE before running the .hxml benchmark."
    }
    if (-not (Get-Command haxelib -ErrorAction SilentlyContinue)) {
        throw "haxelib was not found. Put haxelib in PATH or set LUB_HAXE to a Haxe install that contains haxelib."
    }

    if ($Backend) {
        $env:LUB_BACKEND = $Backend
    } else {
        Remove-Item Env:LUB_BACKEND -ErrorAction SilentlyContinue
    }
    $env:LUB_SPRITE_TARGET_FPS = $TargetFps.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:LUB_SPRITE_BURST = $Burst.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:LUB_SPRITE_SCORE_FRAME = $ScoreFrame.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:LUB_SPRITE_MAX = $MaxSprites.ToString([Globalization.CultureInfo]::InvariantCulture)
    if ($Profile) {
        $profileStartFrame = [Math]::Max(0, $ScoreFrame - $ProfileWindow)
        $env:LUB_PROFILE = "1"
        $env:LUB_PROFILE_WINDOW = $ProfileWindow.ToString([Globalization.CultureInfo]::InvariantCulture)
        $env:LUB_PROFILE_START_FRAME = $profileStartFrame.ToString([Globalization.CultureInfo]::InvariantCulture)
        $env:LUB_PROFILE_FRAME = $ScoreFrame.ToString([Globalization.CultureInfo]::InvariantCulture)
        $env:LUB_PROFILE_LABEL = "sprites13"
    } else {
        Remove-Item Env:LUB_PROFILE -ErrorAction SilentlyContinue
        Remove-Item Env:LUB_PROFILE_WINDOW -ErrorAction SilentlyContinue
        Remove-Item Env:LUB_PROFILE_START_FRAME -ErrorAction SilentlyContinue
        Remove-Item Env:LUB_PROFILE_FRAME -ErrorAction SilentlyContinue
        Remove-Item Env:LUB_PROFILE_LABEL -ErrorAction SilentlyContinue
    }

    Write-Host "running sprite benchmark:"
    Write-Host "  exe=$ExePath"
    Write-Host "  sample=$SamplePath"
    $backendLabel = if ($Backend) { $Backend } else { "(default)" }
    Write-Host "  backend=$backendLabel target_fps=$TargetFps score_frame=$ScoreFrame burst=$Burst max_sprites=$MaxSprites"
    if ($Profile) {
        Write-Host "  profile=on profile_window=$ProfileWindow profile_start_frame=$profileStartFrame"
    }

    & $ExePath $SamplePath
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} finally {
    Pop-Location
}

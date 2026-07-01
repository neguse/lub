param(
    [string]$BuildDir = "build-release",
    [string]$Target = "lub",
    [switch]$ConfigureOnly,
    [switch]$NoConfigure,
    [string]$VcvarsPath = "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat"
)

$ErrorActionPreference = "Stop"

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    Write-Host ("> " + $Program + " " + ($Arguments -join " "))
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

function Import-Vcvars([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "vcvars64.bat was not found: $Path. Pass -VcvarsPath if Visual Studio is installed elsewhere."
    }

    $escaped = $Path.Replace('"', '\"')
    $lines = & cmd.exe /d /s /c "call `"$escaped`" >nul && set"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    foreach ($line in $lines) {
        $eq = $line.IndexOf("=")
        if ($eq -le 0) {
            continue
        }
        $name = $line.Substring(0, $eq)
        $value = $line.Substring($eq + 1)
        [Environment]::SetEnvironmentVariable($name, $value, "Process")
    }
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BuildPath = if ([System.IO.Path]::IsPathRooted($BuildDir)) {
    $BuildDir
} else {
    Join-Path $RepoRoot $BuildDir
}

if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot "third_party\SDL\CMakeLists.txt"))) {
    throw "Submodules are missing. Run: git submodule update --init"
}

Push-Location $RepoRoot
try {
    Import-Vcvars $VcvarsPath

    if (-not $NoConfigure) {
        $configureArgs = @(
            "-S", $RepoRoot,
            "-B", $BuildPath,
            "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Release"
        )
        Invoke-Checked "cmake" $configureArgs
    }

    if (-not $ConfigureOnly) {
        Invoke-Checked "cmake" @("--build", $BuildPath, "--target", $Target, "--parallel")
    }
} finally {
    Pop-Location
}

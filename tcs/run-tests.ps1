$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir = Join-Path $ScriptDir "build"
$luaExe = Join-Path $ScriptDir "deps\lua\lua.exe"

function Build-Lua {
    Write-Host "Building Lua 5.5..."
    cmake -B $BuildDir -DCMAKE_BUILD_TYPE=Release -S $ScriptDir 2>&1 | Out-Null
    cmake --build $BuildDir --config Release 2>&1 | Out-Null
    Write-Host "Lua built."
}

function Test-LuaBuildNeeded {
    if (-not (Test-Path $luaExe)) {
        return $true
    }

    $luaTime = (Get-Item $luaExe).LastWriteTimeUtc
    $inputs = @(
        (Join-Path $ScriptDir "CMakeLists.txt"),
        (Join-Path $ScriptDir "deps\lua\luaconf.h"),
        (Join-Path $ScriptDir "deps\lua\lua.c")
    )

    foreach ($input in $inputs) {
        if ((Test-Path $input) -and
            (Get-Item $input).LastWriteTimeUtc -gt $luaTime) {
            return $true
        }
    }

    return $false
}

# Build Lua if missing or stale against the local build inputs
if (Test-LuaBuildNeeded) {
    Build-Lua
}

$luaVersion = (& $luaExe -v 2>&1) -join "`n"
if (-not $luaVersion.StartsWith("Lua 5.5")) {
    Write-Warning "Lua binary version mismatch, rebuilding: $luaVersion"
    Build-Lua
    $luaVersion = (& $luaExe -v 2>&1) -join "`n"
    if (-not $luaVersion.StartsWith("Lua 5.5")) {
        Write-Error "Expected Lua 5.5, got: $luaVersion"
        exit 1
    }
}

# Run dotnet tests
Write-Host "Running dotnet tests..."
dotnet test $ScriptDir --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running tcs check on samples..."
$sampleChecks = @(
    "samples\hello.cs",
    "samples\game.cs",
    "samples\inventory.cs",
    "samples\entity.cs",
    "samples\statemachine.cs",
    "samples\collision.cs"
)

foreach ($sample in $sampleChecks) {
    $samplePath = Join-Path $ScriptDir $sample
    dotnet run --project (Join-Path $ScriptDir "Transpiler") -- check $samplePath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet run --project (Join-Path $ScriptDir "Transpiler") -- `
    check (Join-Path $ScriptDir "samples\host_api_game.cs") `
    --ref (Join-Path $ScriptDir "samples\host_api_stub.cs")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "All tests passed."

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Build Lua if not already built
$luaExe = Join-Path $ScriptDir "deps\lua\lua.exe"
if (-not (Test-Path $luaExe)) {
    Write-Host "Building Lua 5.5..."
    cmake -B (Join-Path $ScriptDir "build") -DCMAKE_BUILD_TYPE=Release -S $ScriptDir 2>&1 | Out-Null
    cmake --build (Join-Path $ScriptDir "build") --config Release 2>&1 | Out-Null
    Write-Host "Lua built."
}

# Run dotnet tests
Write-Host "Running dotnet tests..."
dotnet test $ScriptDir --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "All tests passed."

$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference `
        -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

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

Write-Host "Running analyzer demo build..."
$analyzerOutput = dotnet build `
    (Join-Path $ScriptDir "samples\analyzer-demo\analyzer-demo.csproj") `
    --no-incremental 2>&1
$analyzerExit = $LASTEXITCODE
$analyzerOutput | ForEach-Object { Write-Host $_ }
if ($analyzerExit -ne 0) { exit $analyzerExit }

$tcs1001Count = @($analyzerOutput |
    Where-Object { "$_".Contains("warning TCS1001:") } |
    Sort-Object -Unique).Count
$tcs1002Count = @($analyzerOutput |
    Where-Object { "$_".Contains("warning TCS1002:") } |
    Sort-Object -Unique).Count
if ($tcs1001Count -ne 4 -or $tcs1002Count -ne 1) {
    Write-Error "Analyzer demo expected TCS1001 x4 / TCS1002 x1, got TCS1001 x$tcs1001Count / TCS1002 x$tcs1002Count"
    exit 1
}

Write-Host "Running analyzer severity override build..."
$overrideDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $overrideDir | Out-Null
try {
    Copy-Item `
        (Join-Path $ScriptDir "samples\analyzer-demo\Program.cs") `
        (Join-Path $overrideDir "Program.cs")

    $analyzerProject = Join-Path $ScriptDir "TinyCs.Analyzers\TinyCs.Analyzers.csproj"
    Set-Content -Path (Join-Path $overrideDir "analyzer-override.csproj") -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$analyzerProject"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false"
                      PrivateAssets="all" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $overrideDir ".editorconfig") -Value @"
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = error
dotnet_diagnostic.TCS1003.severity = warning
"@

    $overrideOutput = dotnet build `
        (Join-Path $overrideDir "analyzer-override.csproj") `
        --no-incremental 2>&1
    $overrideExit = $LASTEXITCODE
    $overrideOutput | ForEach-Object { Write-Host $_ }
    if ($overrideExit -eq 0) {
        Write-Error "Analyzer severity override build expected TCS1002 error"
        exit 1
    }

    $overrideTcs1002Count = @($overrideOutput |
        Where-Object { "$_".Contains("error TCS1002:") } |
        Sort-Object -Unique).Count
    if ($overrideTcs1002Count -ne 1) {
        Write-Error "Analyzer severity override expected TCS1002 x1, got x$overrideTcs1002Count"
        exit 1
    }
}
finally {
    Remove-Item -Recurse -Force $overrideDir
}

Write-Host "All tests passed."

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

Write-Host "Running analyzer package consumer build..."
$packageDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
$consumerDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $packageDir | Out-Null
New-Item -ItemType Directory -Path $consumerDir | Out-Null
try {
    dotnet pack `
        (Join-Path $ScriptDir "TinyCs.Analyzers\TinyCs.Analyzers.csproj") `
        -c Release `
        -o $packageDir | Out-Null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Copy-Item `
        (Join-Path $ScriptDir "samples\analyzer-demo\Program.cs") `
        (Join-Path $consumerDir "Program.cs")

    Set-Content -Path (Join-Path $consumerDir "analyzer-package-consumer.csproj") -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreAdditionalProjectSources>$packageDir</RestoreAdditionalProjectSources>
    <RestorePackagesPath>$consumerDir\packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TinyCs.Analyzers" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $consumerDir ".editorconfig") -Value @"
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = warning
dotnet_diagnostic.TCS1003.severity = warning
"@

    $consumerOutput = dotnet build `
        (Join-Path $consumerDir "analyzer-package-consumer.csproj") `
        --no-incremental 2>&1
    $consumerExit = $LASTEXITCODE
    $consumerOutput | ForEach-Object { Write-Host $_ }
    if ($consumerExit -ne 0) { exit $consumerExit }

    $consumerTcs1001Count = @($consumerOutput |
        Where-Object { "$_".Contains("warning TCS1001:") } |
        Sort-Object -Unique).Count
    $consumerTcs1002Count = @($consumerOutput |
        Where-Object { "$_".Contains("warning TCS1002:") } |
        Sort-Object -Unique).Count
    if ($consumerTcs1001Count -ne 4 -or $consumerTcs1002Count -ne 1) {
        Write-Error "Analyzer package consumer expected TCS1001 x4 / TCS1002 x1, got TCS1001 x$consumerTcs1001Count / TCS1002 x$consumerTcs1002Count"
        exit 1
    }

    Write-Host "Running analyzer package severity override build..."
    Set-Content -Path (Join-Path $consumerDir ".editorconfig") -Value @"
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = error
dotnet_diagnostic.TCS1003.severity = warning
"@

    $overrideOutput = dotnet build `
        (Join-Path $consumerDir "analyzer-package-consumer.csproj") `
        --no-incremental 2>&1
    $overrideExit = $LASTEXITCODE
    $overrideOutput | ForEach-Object { Write-Host $_ }
    if ($overrideExit -eq 0) {
        Write-Error "Analyzer package severity override build expected TCS1002 error"
        exit 1
    }

    $overrideTcs1002Count = @($overrideOutput |
        Where-Object { "$_".Contains("error TCS1002:") } |
        Sort-Object -Unique).Count
    if ($overrideTcs1002Count -ne 1) {
        Write-Error "Analyzer package severity override expected TCS1002 x1, got x$overrideTcs1002Count"
        exit 1
    }
}
finally {
    Remove-Item -Recurse -Force $packageDir
    Remove-Item -Recurse -Force $consumerDir
}

Write-Host "All tests passed."

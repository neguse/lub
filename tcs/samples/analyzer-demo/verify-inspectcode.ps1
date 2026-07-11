$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference `
        -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$ToolVersion = "2026.1.3"
$TempRoot = [System.IO.Path]::GetTempPath()
$ToolDir = if ($env:TCS_JETBRAINS_TOOL_DIR) {
    $env:TCS_JETBRAINS_TOOL_DIR
}
else {
    Join-Path $TempRoot "tcs-jetbrains-tools"
}
$OutputDir = if ($env:TCS_INSPECTCODE_OUTPUT_DIR) {
    $env:TCS_INSPECTCODE_OUTPUT_DIR
}
else {
    Join-Path $TempRoot "tcs-inspectcode-analyzer-demo"
}
# $isWindows collides with the pwsh 7 read-only automatic variable
$onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
$Jb = Join-Path $ToolDir ($(if ($onWindows) { "jb.exe" } else { "jb" }))
$ProjectReferenceSarif = Join-Path $OutputDir "project-reference.sarif"
$ProjectReferenceStdoutLog = Join-Path $OutputDir "project-reference.stdout"
$PackageDir = Join-Path $OutputDir "local-nupkg"
$PackageConsumerDir = Join-Path $OutputDir "package-consumer"
$PackageConsumerProject = Join-Path $PackageConsumerDir "analyzer-package-consumer.csproj"
$PackageReferenceSarif = Join-Path $OutputDir "package-reference.sarif"
$PackageReferenceStdoutLog = Join-Path $OutputDir "package-reference.stdout"
$PackageReferenceOverrideSarif = Join-Path $OutputDir "package-reference-severity-override.sarif"
$PackageReferenceOverrideStdoutLog = Join-Path $OutputDir "package-reference-severity-override.stdout"

function ConvertTo-CommandLineArgument {
    param([string] $Argument)

    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [System.Text.StringBuilder]::new()
    [void] $builder.Append([char] 34)
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char] 92) {
            $backslashes += 1
            continue
        }

        if ($character -eq [char] 34) {
            if ($backslashes -gt 0) {
                [void] $builder.Append([char] 92, $backslashes * 2)
                $backslashes = 0
            }
            [void] $builder.Append([char] 92)
            [void] $builder.Append([char] 34)
            continue
        }

        if ($backslashes -gt 0) {
            [void] $builder.Append([char] 92, $backslashes)
            $backslashes = 0
        }
        [void] $builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void] $builder.Append([char] 92, $backslashes * 2)
    }
    [void] $builder.Append([char] 34)
    return $builder.ToString()
}

function Join-CommandLineArguments {
    param([string[]] $Arguments)

    return (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " ")
}

function Invoke-External {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [string] $StdoutLog
    )

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo.FileName = $FilePath
    $process.StartInfo.Arguments = Join-CommandLineArguments $Arguments
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.UseShellExecute = $false

    [void] $process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    $output = $stdoutTask.Result
    $errorOutput = $stderrTask.Result
    if ($StdoutLog) {
        Set-Content -Path $StdoutLog -Encoding UTF8 -Value ($output + $errorOutput)
    }
    elseif (-not [string]::IsNullOrEmpty($output)) {
        Write-Host $output
    }
    if (-not $StdoutLog -and -not [string]::IsNullOrEmpty($errorOutput)) {
        Write-Host $errorOutput
    }

    return $process.ExitCode
}

function Invoke-RequiredExternal {
    param(
        [string] $FilePath,
        [string[]] $Arguments
    )

    $exitCode = Invoke-External -FilePath $FilePath -Arguments $Arguments
    if ($exitCode -ne 0) {
        throw "$FilePath exited with code $exitCode"
    }
}

function Invoke-InspectCode {
    param(
        [string] $Project,
        [string] $Sarif,
        [string] $StdoutLog
    )

    return Invoke-External `
        -FilePath $Jb `
        -Arguments @(
            "inspectcode",
            $Project,
            "--format=Sarif",
            "--output=$Sarif",
            "--no-updates",
            "--verbosity=ERROR"
        ) `
        -StdoutLog $StdoutLog
}

function Get-RuleCount {
    param(
        [string] $Sarif,
        [string] $RuleId
    )

    if (-not (Test-Path -LiteralPath $Sarif)) {
        return 0
    }

    $text = Get-Content -LiteralPath $Sarif -Raw
    return [regex]::Matches($text, [regex]::Escape("""ruleId"": ""$RuleId""")).Count
}

function Assert-FileContains {
    param(
        [string] $Label,
        [string] $File,
        [string] $Needle
    )

    if (-not (Test-Path -LiteralPath $File)) {
        throw "InspectCode $Label output file was not found: $File"
    }

    $text = Get-Content -LiteralPath $File -Raw
    if (-not $text.Contains($Needle)) {
        throw "InspectCode $Label did not contain expected diagnostic text: $Needle; file: $File"
    }
}

function Assert-ExpectedDiagnosticTexts {
    param(
        [string] $Label,
        [string] $File
    )

    foreach ($needle in @(
            "StructDeclaration",
            "LocalFunctionStatement",
            "TryStatement",
            "ThrowStatement",
            "ListPattern",
            "System.IO.File.ReadAllText",
            "List<T> cannot store null elements")) {
        Assert-FileContains -Label $Label -File $File -Needle $needle
    }
}

function Assert-ExpectedCounts {
    param(
        [string] $Label,
        [string] $Sarif,
        [string] $StdoutLog
    )

    $tcs1001Count = Get-RuleCount -Sarif $Sarif -RuleId "TCS1001"
    $tcs1002Count = Get-RuleCount -Sarif $Sarif -RuleId "TCS1002"
    $tcs1003Count = Get-RuleCount -Sarif $Sarif -RuleId "TCS1003"

    if ($tcs1001Count -ne 5 -or $tcs1002Count -ne 1 -or $tcs1003Count -ne 1) {
        throw "InspectCode $Label expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$tcs1001Count / TCS1002 x$tcs1002Count / TCS1003 x$tcs1003Count; SARIF: $Sarif; stdout/stderr log: $StdoutLog"
    }

    Assert-ExpectedDiagnosticTexts -Label $Label -File $Sarif
    Write-Host "InspectCode $Label diagnostics verified."
    Write-Host "TCS1001 x$tcs1001Count / TCS1002 x$tcs1002Count / TCS1003 x$tcs1003Count"
    Write-Host "SARIF: $Sarif"
    Write-Host "stdout/stderr log: $StdoutLog"
}

function Get-StdoutDiagnosticCount {
    param(
        [string] $StdoutLog,
        [string] $DiagnosticId
    )

    if (-not (Test-Path -LiteralPath $StdoutLog)) {
        return 0
    }

    return @(
        Get-Content -LiteralPath $StdoutLog |
            Where-Object { $_.Contains("${DiagnosticId}:") } |
            Sort-Object -Unique
    ).Count
}

if (-not (Test-Path -LiteralPath $Jb)) {
    New-Item -ItemType Directory -Path $ToolDir -Force | Out-Null
    Invoke-RequiredExternal `
        -FilePath "dotnet" `
        -Arguments @(
            "tool",
            "install",
            "JetBrains.ReSharper.GlobalTools",
            "--tool-path",
            $ToolDir,
            "--version",
            $ToolVersion
        )
}

Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutputDir, $PackageDir, $PackageConsumerDir -Force | Out-Null

[void] (Invoke-InspectCode `
    -Project (Join-Path $RepoRoot "samples\analyzer-demo\analyzer-demo.csproj") `
    -Sarif $ProjectReferenceSarif `
    -StdoutLog $ProjectReferenceStdoutLog)
Assert-ExpectedCounts `
    -Label "ProjectReference analyzer demo" `
    -Sarif $ProjectReferenceSarif `
    -StdoutLog $ProjectReferenceStdoutLog

Invoke-RequiredExternal `
    -FilePath "dotnet" `
    -Arguments @(
        "pack",
        (Join-Path $RepoRoot "TinyCs.Analyzers\TinyCs.Analyzers.csproj"),
        "-c",
        "Release",
        "-o",
        $PackageDir
    )
Copy-Item `
    -LiteralPath (Join-Path $RepoRoot "samples\analyzer-demo\Program.cs") `
    -Destination (Join-Path $PackageConsumerDir "Program.cs")

$escapedPackageDir = [System.Security.SecurityElement]::Escape($PackageDir)
$escapedPackagesPath = [System.Security.SecurityElement]::Escape((Join-Path $PackageConsumerDir "packages"))
Set-Content -Path $PackageConsumerProject -Encoding UTF8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreAdditionalProjectSources>$escapedPackageDir</RestoreAdditionalProjectSources>
    <RestorePackagesPath>$escapedPackagesPath</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TinyCs.Analyzers" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@
Set-Content -Path (Join-Path $PackageConsumerDir ".editorconfig") -Encoding UTF8 -Value @"
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = warning
dotnet_diagnostic.TCS1003.severity = warning
"@
Invoke-RequiredExternal `
    -FilePath "dotnet" `
    -Arguments @("restore", $PackageConsumerProject)

[void] (Invoke-InspectCode `
    -Project $PackageConsumerProject `
    -Sarif $PackageReferenceSarif `
    -StdoutLog $PackageReferenceStdoutLog)
Assert-ExpectedCounts `
    -Label "PackageReference consumer" `
    -Sarif $PackageReferenceSarif `
    -StdoutLog $PackageReferenceStdoutLog

Set-Content -Path (Join-Path $PackageConsumerDir ".editorconfig") -Encoding UTF8 -Value @"
root = true

[*.cs]
dotnet_diagnostic.TCS1001.severity = error
dotnet_diagnostic.TCS1002.severity = error
dotnet_diagnostic.TCS1003.severity = error
"@

$overrideAttempt = 1
$overrideMaxAttempts = 2
while ($true) {
    Remove-Item `
        -LiteralPath @($PackageReferenceOverrideSarif, $PackageReferenceOverrideStdoutLog) `
        -Force `
        -ErrorAction SilentlyContinue

    $overrideExit = Invoke-InspectCode `
        -Project $PackageConsumerProject `
        -Sarif $PackageReferenceOverrideSarif `
        -StdoutLog $PackageReferenceOverrideStdoutLog
    $overrideTcs1001Count = Get-StdoutDiagnosticCount `
        -StdoutLog $PackageReferenceOverrideStdoutLog `
        -DiagnosticId "TCS1001"
    $overrideTcs1002Count = Get-StdoutDiagnosticCount `
        -StdoutLog $PackageReferenceOverrideStdoutLog `
        -DiagnosticId "TCS1002"
    $overrideTcs1003Count = Get-StdoutDiagnosticCount `
        -StdoutLog $PackageReferenceOverrideStdoutLog `
        -DiagnosticId "TCS1003"

    if ($overrideExit -ne 0 -and
        $overrideTcs1001Count -eq 5 -and
        $overrideTcs1002Count -eq 1 -and
        $overrideTcs1003Count -eq 1) {
        break
    }

    if ($overrideAttempt -ge $overrideMaxAttempts) {
        if ($overrideExit -eq 0) {
            throw "InspectCode PackageReference severity override expected TCS errors; SARIF: $PackageReferenceOverrideSarif; stdout/stderr log: $PackageReferenceOverrideStdoutLog"
        }

        throw "InspectCode PackageReference severity override expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$overrideTcs1001Count / TCS1002 x$overrideTcs1002Count / TCS1003 x$overrideTcs1003Count; stdout/stderr log: $PackageReferenceOverrideStdoutLog"
    }

    Write-Warning "InspectCode PackageReference severity override returned incomplete diagnostics on attempt $overrideAttempt; retrying."
    $overrideAttempt += 1
}

Assert-ExpectedDiagnosticTexts `
    -Label "PackageReference severity override" `
    -File $PackageReferenceOverrideStdoutLog

Write-Host "InspectCode PackageReference severity override verified."
Write-Host "TCS1001 error x$overrideTcs1001Count / TCS1002 error x$overrideTcs1002Count / TCS1003 error x$overrideTcs1003Count"
Write-Host "stdout/stderr log: $PackageReferenceOverrideStdoutLog"

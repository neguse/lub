$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference `
        -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$OutputDir = if ($env:TCS_RIDER_PRECHECK_OUTPUT_DIR) {
    $env:TCS_RIDER_PRECHECK_OUTPUT_DIR
}
else {
    Join-Path ([System.IO.Path]::GetTempPath()) "tcs-rider-verification-precheck"
}
$Summary = Join-Path $OutputDir "summary.md"
$Failed = $false

. (Join-Path $ScriptDir "rider-env.ps1")

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

function Get-PowerShellExecutable {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh) {
        return $pwsh.Source
    }

    $powershell = Get-Command powershell -ErrorAction SilentlyContinue
    if ($powershell) {
        return $powershell.Source
    }

    return $null
}

function Invoke-LoggedProcess {
    param(
        [string] $Label,
        [string] $LogName,
        [string] $FilePath,
        [string[]] $Arguments
    )

    $log = Join-Path $OutputDir $LogName
    Write-Host "Running $Label..."

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

    Set-Content -Path $log -Encoding UTF8 -Value ($stdoutTask.Result + $stderrTask.Result)

    if ($process.ExitCode -eq 0) {
        Add-Content -Path $Summary -Encoding UTF8 -Value "- ``$Label``: pass ([log]($log))"
    }
    else {
        Add-Content -Path $Summary -Encoding UTF8 -Value "- ``$Label``: fail, exit $($process.ExitCode) ([log]($log))"
        $script:Failed = $true
    }
}

Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$riderCommand = Find-RiderCommand
$riderUiReady = if ($riderCommand -and (Test-RiderUiLaunchable)) { "yes" } else { "no" }
$dotnetSdk = (& dotnet --version 2>&1) -join "`n"
$osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription

Set-Content -Path $Summary -Encoding UTF8 -Value @"
# Rider verification prechecks

- Date: $([DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
- OS: $osDescription
- .NET SDK: $dotnetSdk
- TCS_RIDER_COMMAND: $(Format-ValueOrUnset $env:TCS_RIDER_COMMAND)
- Rider command: $(Format-ValueOrUnset $riderCommand)
- Rider UI launchable from this shell: $riderUiReady
- DISPLAY: $(Format-ValueOrUnset $env:DISPLAY)
- WAYLAND_DISPLAY: $(Format-ValueOrUnset $env:WAYLAND_DISPLAY)
- XDG_SESSION_TYPE: $(Format-ValueOrUnset $env:XDG_SESSION_TYPE)
- Xvfb: $(Get-CommandPathOrNotFound "Xvfb")
- xvfb-run: $(Get-CommandPathOrNotFound "xvfb-run")

## Results
"@

$powerShellExe = Get-PowerShellExecutable
if (-not $powerShellExe) {
    throw "PowerShell executable was not found."
}

Invoke-LoggedProcess `
    -Label "PowerShell run-tests.ps1" `
    -LogName "run-tests.log" `
    -FilePath $powerShellExe `
    -Arguments @(
        "-NoProfile",
        "-File",
        (Join-Path $RepoRoot "run-tests.ps1")
    )

Invoke-LoggedProcess `
    -Label "samples/analyzer-demo/verify-inspectcode.ps1" `
    -LogName "verify-inspectcode.log" `
    -FilePath $powerShellExe `
    -Arguments @(
        "-NoProfile",
        "-File",
        (Join-Path $RepoRoot "samples\analyzer-demo\verify-inspectcode.ps1")
    )

Invoke-LoggedProcess `
    -Label "dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental" `
    -LogName "analyzer-demo-build.log" `
    -FilePath "dotnet" `
    -Arguments @(
        "build",
        (Join-Path $RepoRoot "samples\analyzer-demo\analyzer-demo.csproj"),
        "--no-incremental"
    )

Add-Content -Path $Summary -Encoding UTF8 -Value @"

## Expected Rider Diagnostics

- TCS1001 x5: StructDeclaration, LocalFunctionStatement, TryStatement, ThrowStatement, ListPattern
- TCS1002 x1: System.IO.File.ReadAllText
- TCS1003 x1: List<T> null storage
"@

Write-Host "Summary: $Summary"
if ($Failed) {
    exit 1
}

Write-Host "Rider verification prechecks passed."

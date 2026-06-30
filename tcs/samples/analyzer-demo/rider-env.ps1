$ErrorActionPreference = "Stop"

function Format-ValueOrUnset {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "(unset)"
    }

    return $Value
}

function Get-CommandPathOrNotFound {
    param([string] $CommandName)

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return "(not found)"
}

function Resolve-CommandOrPath {
    param([string] $Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $null
    }

    $command = Get-Command $Candidate -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    if (Test-Path -LiteralPath $Candidate) {
        return (Resolve-Path -LiteralPath $Candidate).Path
    }

    return $null
}

function Find-RiderCommand {
    $configured = $env:TCS_RIDER_COMMAND
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        return Resolve-CommandOrPath $configured
    }

    foreach ($candidate in @(
            "rider",
            "jetbrains-rider",
            "rider64.exe",
            "rider.exe",
            "rider.bat",
            "rider.cmd",
            "rider.sh")) {
        $resolved = Resolve-CommandOrPath $candidate
        if ($resolved) {
            return $resolved
        }
    }

    $roots = @()
    if ($env:LOCALAPPDATA) {
        $roots += (Join-Path $env:LOCALAPPDATA "JetBrains\Toolbox\apps")
        $roots += (Join-Path $env:LOCALAPPDATA "Programs")
    }
    if ($env:ProgramFiles) {
        $roots += (Join-Path $env:ProgramFiles "JetBrains")
    }
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFilesX86) {
        $roots += (Join-Path $programFilesX86 "JetBrains")
    }
    if ($HOME) {
        $roots += (Join-Path $HOME ".local/share/JetBrains/Toolbox/apps")
        $roots += (Join-Path $HOME ".local/share/JetBrains")
    }
    $roots += "/opt"
    $roots += "/usr/local"
    $roots += "/Applications/Rider.app/Contents/MacOS"
    $roots += "/Applications/JetBrains Rider.app/Contents/MacOS"

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($name in @("rider64.exe", "rider.exe", "rider.bat", "rider.cmd", "rider.sh", "rider")) {
            $candidate = Get-ChildItem -LiteralPath $root `
                -Recurse `
                -File `
                -Filter $name `
                -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($candidate) {
                return $candidate.FullName
            }
        }
    }

    return $null
}

function Test-RiderUiLaunchable {
    $isWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
    $isMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)

    if ($isWindows -or $isMacOS) {
        return [Environment]::UserInteractive
    }

    return -not [string]::IsNullOrWhiteSpace($env:DISPLAY) -or
        -not [string]::IsNullOrWhiteSpace($env:WAYLAND_DISPLAY)
}

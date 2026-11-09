$ErrorActionPreference = "Stop"

function Get-TcsCacheDir {
    param([string] $Name)

    $root = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        $env:LOCALAPPDATA
    }
    elseif ($env:XDG_CACHE_HOME) {
        $env:XDG_CACHE_HOME
    }
    else {
        Join-Path $HOME ".cache"
    }

    return Join-Path (Join-Path $root "tcs") $Name
}

# 自分だけが書けるディレクトリを用意する。他人の所有物や symlink はそのまま使わない。
function Initialize-PrivateDir {
    param([string] $Path)

    $onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
    # DirectoryInfo は symlink を辿らずに属性を返す。リンク先が無い symlink もここで弾く。
    $fullPath = [System.IO.Path]::Combine((Get-Location).Path, $Path).TrimEnd('\', '/')
    $info = [System.IO.DirectoryInfo]::new($fullPath)
    if ([int] $info.Attributes -ne -1) {
        if ([int] ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Path is a symlink; refusing to use it."
        }

        $owned = if ($onWindows) {
            (Get-Acl -LiteralPath $fullPath).Owner -eq
                [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        }
        else {
            (Get-Item -LiteralPath $fullPath -Force).User -eq [Environment]::UserName
        }
        if (-not $owned) {
            throw "$Path is not owned by the current user; refusing to use it."
        }
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    # Windows は %LOCALAPPDATA% のユーザー専用の ACL を継承するので、権限は変えない。
    if (-not $onWindows) {
        & chmod 700 $fullPath
        if ($LASTEXITCODE -ne 0) {
            throw "chmod 700 $Path exited with code $LASTEXITCODE"
        }
    }
}

# Remove-Item は symlink の先を消すことがあるので、消す前にも確かめる。
function Reset-PrivateDir {
    param([string] $Path)

    Initialize-PrivateDir $Path
    Remove-Item -LiteralPath $Path -Recurse -Force
    Initialize-PrivateDir $Path
}

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
    # $isWindows / $isMacOS collide with pwsh 7 read-only automatic variables
    $onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
    $onMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)

    if ($onWindows -or $onMacOS) {
        return [Environment]::UserInteractive
    }

    return -not [string]::IsNullOrWhiteSpace($env:DISPLAY) -or
        -not [string]::IsNullOrWhiteSpace($env:WAYLAND_DISPLAY)
}

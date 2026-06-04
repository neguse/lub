param(
    [string]$BuildDir = "build-release",
    [string]$Target = "lub",
    [switch]$ConfigureOnly,
    [switch]$NoConfigure,
    [string[]]$DependencySourceRoot = @("build-release\_deps", "build\_deps"),
    [int]$DownloadTimeoutSec = 1200,
    [string]$VcvarsPath = "C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat"
)

$ErrorActionPreference = "Stop"

$LuaVersion = "5.5.0"
$LuaSha256 = "57ccc32bbbd005cab75bcc52444052535af691789dba2b9016d5c50640d68b3d"
$LuaArchives = @(
    @{
        Url = "https://www.lua.org/ftp/lua-$LuaVersion.tar.gz"
        Sha256 = $LuaSha256
    },
    @{
        Url = "https://sourceforge.net/projects/lua.mirror/files/v$LuaVersion/Lua%20$LuaVersion%20source%20code.tar.gz/download"
        Sha256 = "98d99ea54561843f36b5edb86255824fc81d072c42f22ae18f873eb0d0c2a05e"
    },
    @{
        Url = "https://fossies.org/linux/misc/lua-$LuaVersion.tar.gz"
        Sha256 = $LuaSha256
    }
)

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

function Add-FetchContentSource([string[]]$InputArgs, [string]$Name, [string]$Path, [string]$Marker) {
    if (Test-Path -LiteralPath (Join-Path $Path $Marker)) {
        return $InputArgs + @("-DFETCHCONTENT_SOURCE_DIR_$Name=$Path")
    }
    return $InputArgs
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BuildPath = if ([System.IO.Path]::IsPathRooted($BuildDir)) {
    $BuildDir
} else {
    Join-Path $RepoRoot $BuildDir
}

$DepRoots = foreach ($root in $DependencySourceRoot) {
    if ([System.IO.Path]::IsPathRooted($root)) {
        $root
    } else {
        Join-Path $RepoRoot $root
    }
}

function Find-DependencySource([string]$Subdir, [string]$Marker) {
    foreach ($root in $DepRoots) {
        $candidate = Join-Path $root $Subdir
        if (Test-Path -LiteralPath (Join-Path $candidate $Marker)) {
            return $candidate
        }
    }
    return $null
}

function Remove-UnderDirectory([string]$Root, [string]$Path) {
    $rootPath = (Resolve-Path -LiteralPath $Root).Path
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $targetPath = (Resolve-Path -LiteralPath $Path).Path
    if (-not $targetPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove outside ${rootPath}: $targetPath"
    }
    Remove-Item -LiteralPath $targetPath -Recurse -Force
}

function Ensure-LuaSource() {
    $existing = Find-DependencySource "lua-src" "src\lapi.c"
    if ($existing -ne $null) {
        return $existing
    }

    $depsDir = Join-Path $BuildPath "_deps"
    $luaSource = Join-Path $depsDir "lua-src"
    $archive = Join-Path $depsDir "lua-$LuaVersion.tar.gz"
    $download = "$archive.download"
    $extractDir = Join-Path $depsDir "lua-extract"
    New-Item -ItemType Directory -Force -Path $depsDir | Out-Null

    $haveArchive = $false
    if (Test-Path -LiteralPath $archive) {
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        foreach ($entry in $LuaArchives) {
            if ($hash -eq $entry.Sha256) {
                $haveArchive = $true
                break
            }
        }
    }

    if (-not $haveArchive) {
        foreach ($entry in $LuaArchives) {
            $url = $entry.Url
            $expectedSha256 = $entry.Sha256
            Write-Host "fetching Lua $LuaVersion from $url"
            Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
            try {
                Invoke-WebRequest -Uri $url -OutFile $download -TimeoutSec $DownloadTimeoutSec -UserAgent "lub-build-release"
                $hash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -eq $expectedSha256) {
                    Move-Item -LiteralPath $download -Destination $archive -Force
                    $haveArchive = $true
                    break
                }
                Write-Warning "Lua archive hash mismatch from $url; got $hash"
            } catch {
                Write-Warning "Lua download failed from ${url}: $($_.Exception.Message)"
            }
        }
    }

    if (-not $haveArchive) {
        throw "Failed to fetch Lua $LuaVersion archive with SHA256 $LuaSha256."
    }

    Remove-UnderDirectory $depsDir $extractDir
    Remove-UnderDirectory $depsDir $luaSource
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Push-Location $extractDir
    try {
        Invoke-Checked "cmake" @("-E", "tar", "xzf", $archive)
    } finally {
        Pop-Location
    }

    $lapi = Get-ChildItem -Path $extractDir -Recurse -Filter "lapi.c" | Select-Object -First 1
    if ($lapi -eq $null) {
        throw "Lua archive did not contain lapi.c."
    }

    if ($lapi.Directory.Name -eq "src") {
        $sourceRoot = $lapi.Directory.Parent.FullName
        Move-Item -LiteralPath $sourceRoot -Destination $luaSource
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $luaSource "src") | Out-Null
        Copy-Item -Path (Join-Path $lapi.Directory.FullName "*") -Destination (Join-Path $luaSource "src") -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $luaSource "src\lapi.c"))) {
        throw "Lua source normalization failed: $luaSource"
    }
    Remove-UnderDirectory $depsDir $extractDir
    return $luaSource
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
        $sdlSource = Find-DependencySource "sdl3-src" "CMakeLists.txt"
        $luaSource = Ensure-LuaSource
        if ($sdlSource -ne $null) {
            $configureArgs = Add-FetchContentSource $configureArgs "SDL3" $sdlSource "CMakeLists.txt"
        }
        $configureArgs = Add-FetchContentSource $configureArgs "LUA" $luaSource "src\lapi.c"
        Invoke-Checked "cmake" $configureArgs
    }

    if (-not $ConfigureOnly) {
        Invoke-Checked "cmake" @("--build", $BuildPath, "--target", $Target, "--parallel")
    }
} finally {
    Pop-Location
}

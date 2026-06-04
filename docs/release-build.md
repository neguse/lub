# Release Build

Use the platform script instead of hand-composing CMake commands. First-time
Release builds may fetch and build dependencies, so automation should run these
commands with a 2-hour process timeout.

## Windows

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Useful variants:

```powershell
# Build a different target.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Target lub_haxe_build_smoke

# Configure only.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -ConfigureOnly

# Build from the existing configure step.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -NoConfigure

# Slow network or first-time dependency fetch.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -DownloadTimeoutSec 1200

# Visual Studio installed outside the default path.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -VcvarsPath "C:\path\to\vcvars64.bat"
```

The Windows script imports the Visual Studio x64 developer environment, then
configures and builds `build-release`.

## Linux

```sh
bash scripts/build-release.sh
```

Useful variants:

```sh
# Build a different target.
bash scripts/build-release.sh --target lub_haxe_build_smoke

# Configure only.
bash scripts/build-release.sh --configure-only

# Build from the existing configure step.
bash scripts/build-release.sh --no-configure

# Slow network or first-time dependency fetch.
bash scripts/build-release.sh --download-timeout-sec 1200
```

Linux prerequisites:

- CMake 3.20+
- C11 / C++17 compiler
- Vulkan loader and development headers
- Ninja is preferred when installed; otherwise the default CMake generator is used
- `curl` or `wget` for dependency fallback downloads
- `sha256sum`

Both scripts reuse already fetched dependency sources from `build-release/_deps`
or `build/_deps` when present. If Lua sources are missing, they fetch,
SHA256-verify, and normalize the source archive into `build-release/_deps`.

The Linux script defaults to `build-release-linux` so it can coexist with a
Windows `build-release` directory in the same checkout.

For benchmark runs, use the benchmark script for the platform:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1
```

```sh
bash scripts/run-sprites-bench.sh
```

# Release Build

Use the platform script instead of hand-composing CMake commands. Compiled
dependencies (SDL3, Lua, Box2D, Box3D) are git submodules under `third_party/`;
run `git submodule update --init` once after cloning. First-time Release builds
compile all dependencies from source, so automation should run these commands
with a 2-hour process timeout.

## Windows

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Useful variants:

```powershell
# Build a different target.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Target lub_serve_smoke

# Configure only.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -ConfigureOnly

# Build from the existing configure step.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -NoConfigure

# Visual Studio installed outside the default path.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -VcvarsPath "C:\path\to\vcvars64.bat"
```

The Windows script imports the Visual Studio x64 developer environment, then
configures and builds `build-release`. D3D12 is the default backend. CMake also
builds the optional Vulkan backend when it finds a Vulkan SDK through
`VULKAN_SDK`; the SDK is not required for a D3D12 build.

## Linux

```sh
bash scripts/build-release.sh
```

Useful variants:

```sh
# Build a different target.
bash scripts/build-release.sh --target lub_serve_smoke

# Configure only.
bash scripts/build-release.sh --configure-only

# Build from the existing configure step.
bash scripts/build-release.sh --no-configure
```

Linux prerequisites:

- CMake 3.22+
- C11 / C++17 compiler
- Vulkan loader and development headers
- Ninja is preferred when installed; otherwise the default CMake generator is used

The Linux script defaults to `build-release-linux` so it can coexist with a
Windows `build-release` directory in the same checkout.

For benchmark runs, use the benchmark script for the platform:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1
```

```sh
bash scripts/run-sprites-bench.sh
```

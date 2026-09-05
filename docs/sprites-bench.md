# Sprite Benchmark

Use the platform script for the `rsushi`-style Release benchmark. It builds
Release when needed, runs `samples/13_sprites/Sprites13.csproj`, prints a
`SPRITES13_SCORE ...` line, and exits.

The normal interactive sample advances its animation on a 60 Hz fixed
simulation clock, independently of the display refresh rate. Benchmark mode
(`LUB_SPRITE_SCORE_FRAME > 0`, as set by the platform scripts) intentionally
keeps one workload tick per rendered frame: `score frame` is a rendered-frame
workload contract, not elapsed wall-clock time.

Automation should run these commands with a 2-hour process timeout.

The benchmark entry is a C# project, so the dotnet SDK must be in `PATH`
(lub runs the TinyC# transpiler from `third_party/tcs` with it).

## Windows

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1
```

Useful variants:

```powershell
# Reuse an existing Release build.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -NoBuild

# Run the SDL GPU backend.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -Backend sdlgpu

# Print generic CPU profile timing over the final profile window.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -Profile

# Longer single-spawn benchmark run.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -ScoreFrame 7200 -Burst 1
```

## Linux

```sh
bash scripts/run-sprites-bench.sh
```

The Linux script uses `build-release-linux` by default.

Useful variants:

```sh
# Reuse an existing Release build.
bash scripts/run-sprites-bench.sh --no-build

# Run the SDL GPU backend.
bash scripts/run-sprites-bench.sh --backend sdlgpu

# Print generic CPU profile timing over the final profile window.
bash scripts/run-sprites-bench.sh --profile

# Longer single-spawn benchmark run.
bash scripts/run-sprites-bench.sh --score-frame 7200 --burst 1
```

For comparable performance scores, run on a real GPU in a normal desktop
session. Headless/lavapipe runs are useful for correctness, not for score
comparison.

When reporting a score, include the exact command, OS, backend, target FPS,
score frame, burst, and the full `LUB_PROFILE` / `LUB_PROFILE_SCOPE` /
`SPRITES13_SCORE` lines.

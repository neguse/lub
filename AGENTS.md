# AGENTS.md

## Documentation

Follow the documentation policy in `docs/README.md`. Docs are split by
directory:

- **Current-state docs** (repo root, `docs/` top level, subproject READMEs)
  describe the repository as it is now. When a commit changes behavior,
  update the affected current-state docs in the same commit.
- **Records** (`docs/log/` and completed feature directories such as
  `haxe-wasm/`) are frozen snapshots with a leading `> 記録:` banner.
  Do not rewrite their body; only update the banner's pointers or fix
  broken links.

A design doc written before implementation must not stay in between: once
implemented, either rewrite it as a current-state doc or freeze it as a record.

## Release Build

Do not hand-compose native Release build commands. Use the platform script:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

```sh
bash scripts/build-release.sh
```

If the sandbox blocks Ninja, CMake, compiler, linker, resource compiler, or
dependency tool execution, request approval for the same script command instead
of asking the user to provide a build command. Run Release build and benchmark
commands with a 2-hour process timeout; first-time dependency fetches and SDL
builds can take several minutes. For benchmark tasks, use the benchmark script
below; it delegates to `scripts\build-release.ps1`.

On this Windows host, Ubuntu WSL is available when commands are run outside the
sandbox. Normal sandbox commands may report no WSL distributions. For Linux
checks, request approval and use:

```powershell
wsl.exe -d Ubuntu --cd /mnt/d/github.com/neguse/lub -e bash -lc '<command>'
```

## Sprite Benchmark

Do not hand-compose the Release build and run sequence for the sprite benchmark.
Use the platform script:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1
```

```sh
bash scripts/run-sprites-bench.sh
```

This is the canonical command for the `rsushi`-style sample. It configures
`build-release` on Windows or `build-release-linux` on Linux, builds `lub`,
runs `samples\13_sprites\13_sprites.hxml`, and waits for the
`SPRITES13_SCORE ...` line.

Common variants:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -NoBuild
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -Backend sdlgpu
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -Profile
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -ScoreFrame 7200 -Burst 1
```

```sh
bash scripts/run-sprites-bench.sh --no-build
bash scripts/run-sprites-bench.sh --backend sdlgpu
bash scripts/run-sprites-bench.sh --profile
bash scripts/run-sprites-bench.sh --score-frame 7200 --burst 1
```

When reporting benchmark results, include the exact command, backend, target
FPS, score frame, burst, and the full `LUB_PROFILE` / `LUB_PROFILE_SCOPE` /
`SPRITES13_SCORE` lines.

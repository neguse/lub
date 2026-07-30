# AGENTS.md

Guidance for coding agents working in this repo. Shared rules live in the
current-state docs; this file only adds agent-specific notes and pointers.

## Documentation

Follow the documentation policy in `docs/README.md`: current-state docs vs
frozen records split by directory, plain wording (no dev jargon, no
completion notes, no bold emphasis). `scripts/docs-lint.sh` enforces the
mechanical part and runs in the commit hook and the CI lint job.

## Workflow

Work flows branch → PR; the merge gate is PR CI (`.github/workflows/ci.yml`,
required check = the aggregate `gate` job).
The commit hook (`scripts/pre-commit.sh`) runs format, whitespace, and docs
lint. Full local verification is `scripts/pre-push.sh`. See `CLAUDE.md`
(Japanese) for the complete working conventions.

## Release Build

Do not hand-compose native Release build commands. Use the platform scripts
described in `docs/release-build.md`:

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
builds can take several minutes.

On this Windows host, Ubuntu WSL is available when commands are run outside the
sandbox. Normal sandbox commands may report no WSL distributions. For Linux
checks, request approval and use:

```powershell
wsl.exe -d Ubuntu --cd /mnt/d/github.com/neguse/lub -e bash -lc '<command>'
```

## Sprite Benchmark

Do not hand-compose the Release build and run sequence for the sprite
benchmark. Use `scripts/run-sprites-bench.sh` /
`scripts\run-sprites-bench.ps1`; flags and score reading are described in
`docs/sprites-bench.md`. When reporting benchmark results, include the exact
command, backend, target FPS, score frame, burst, and the full `LUB_PROFILE`
/ `LUB_PROFILE_SCOPE` / `SPRITES13_SCORE` lines.

# Crane game

Hold Space or the mouse button to move right, release, then hold again to move
back. Releasing the second time starts the automatic drop, grab, lift, return,
and release. After four seconds without input, the demo aims at a prize's torso.
It follows the torso's rotation when a prize has fallen over.

The head hangs from a physical cable. Two motor-driven fingers grip the prizes
with friction and wide rubber tips; prizes are never attached or teleported to
the claw. The head stays awake so changing the cable length also moves a resting
claw. Closing takes 1.67 seconds, including time for contacts to settle. The grab
and hold power sliders limit motor torque; even with zero power, a prize can
occasionally rest on a finger or be scooped by its weight.

## Repeatable checks

Build the runtime using the platform script in
[release-build.md](../../docs/release-build.md), then compile the sample:

```sh
bash scripts/run-cs-sample.sh 23_crane_game --build
```

From the repository root, run the replay with the native executable:

```powershell
.\build-release\lub.exe tests/lua/test_crane_game.lua
```

```sh
scripts/run-headless.sh ./build-release-linux/lub tests/lua/test_crane_game.lua
```

The replay runs the actual sample at 60 physics ticks per simulated second,
without drawing each tick or waiting for real time. It checks centered and
turned prizes, a missed aim, fingers held open, zero motor power, and 200 seconds
of normal play. It checks delivery, full cycles, the head's height before carrying,
and sudden movement. `CRANE_CASE` lines give measurements and `CRANE_PASS` means
all checks passed. The native CI gate runs it after compiling the samples.

Set `CRANE_SAMPLE_LUA` to a saved, compiled sample to compare an earlier version
with the same cases. No LLM input timing is involved.

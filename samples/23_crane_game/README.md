# Crane game

Hold Space or the mouse button to move right, release, then hold again to move
back. Releasing the second time starts the drop, grab, lift, return, and release.
After four seconds without input, the demo aims at a prize's torso, accounting
for its rotation. The force sliders are in newtons. `show linkage` removes the
head cover and glass from the drawing and zooms in on the moving shaft and links.
Orange dots mark actual contact points on the fingers.

## Mechanism

A cable suspends the dynamic head. Inside it, a 0.20 kg shaft travels 79 mm along
a guide. A solenoid pulls the shaft upward and applies an equal, opposite force
to the housing at the same point. Two 104.4 mm links connect the shaft to levers
60 mm above the finger pivots. The fingers have passive hinges; neither finger
has an independent motor. A damped return spring opens the assembly when power
is removed. The default pull is 40 N during both grab and carry.

The finger tips are 50 by 130 by 8 mm plates with friction 0.9. Their drawn and
physical shapes match. They support the prize from below and constrain its
motion through contact and friction. The head stays awake while the cable is
reeled in. No joint, positioning command, or additional force is applied to a
prize. Opening at the chute releases the contacts and the prize falls.

The shaft, return spring, and linked fingers follow the mechanical principle in
[US6234487B1](https://patents.google.com/patent/US6234487B1/en). This sample uses
its own dimensions and an idealized constant coil force, massless rigid links,
and rigid compound prize shapes; it is not a calibrated model of a particular
commercial machine or of fabric deformation. Link constraints use 120 Hz tuning
with eight physics substeps to limit their numerical stretch under load.

## Repeatable checks

Build the runtime with the platform script in
[release-build.md](../../docs/release-build.md), then compile the sample:

```sh
bash scripts/run-cs-sample.sh 23_crane_game --build
```

Run the replay from the repository root:

```powershell
.\build-release\lub.exe tests/lua/test_crane_game.lua
```

```sh
scripts/run-headless.sh ./build-release-linux/lub tests/lua/test_crane_game.lua
```

The replay runs the actual compiled sample at 60 physics ticks per simulated
second, without drawing each tick or waiting for real time. Its aiming grid
covers 16 orientations, each with a centered aim and diagonal offsets of plus
and minus 20 mm. Additional cases cover a missed aim, no power, power removed
when carrying begins, and 200 seconds of normal play.

The checks inspect contacts while the prize is off the floor, sustained support
while carrying, delivery, release after power loss, complete cycles, head height,
sudden movement, and linkage stretch. They also reject prize-attached joints and
explicit force, impulse, positioning, or velocity commands targeting a prize.
The grid must deliver at least 36 of 48 prizes, including 13 of the 16 centered
attempts, and sustain at least 36 grips for 90 carry ticks. A delivered prize must
have been held by both fingers off the floor for at least 10 ticks. A missed or
unpowered grab must not award a prize. Normal play must deliver at least four
prizes in 200 seconds. These checks allow physical misses and slipping.

`CRANE_CASE` and `CRANE_GRID` report measurements; `CRANE_PASS` means all checks
passed. The native CI gate runs this after compiling the samples. Set
`CRANE_SAMPLE_LUA` to a saved compiled sample for a comparison with older code.
No LLM input timing is involved.

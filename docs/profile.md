# Profiling

Enable the built-in CPU profiler with environment variables.

Windows:

```powershell
$env:LUB_PROFILE = "1"
$env:LUB_PROFILE_START_FRAME = "300"
$env:LUB_PROFILE_FRAME = "600"
$env:LUB_PROFILE_LABEL = "my-run"
$env:LUB_PROFILE_EVERY = "300"
.\build-release\lub.exe samples\13_sprites\Sprites13.csproj
```

Linux:

```sh
LUB_PROFILE=1 \
LUB_PROFILE_START_FRAME=300 \
LUB_PROFILE_FRAME=600 \
LUB_PROFILE_LABEL=my-run \
LUB_PROFILE_EVERY=300 \
./build-release-linux/lub samples/13_sprites/Sprites13.csproj
```

When using `scripts/build-release.sh`, the default Linux binary is
`./build-release-linux/lub`.

The runtime always records these scopes when profiling is enabled:

- `runtime.begin_frame`
- `script.onFrame`
- `runtime.pass_guard`
- `runtime.end_frame`

Game code can add its own scopes:

```csharp
using Lub;

Profiler.BeginScope("game.update");
Update();
Profiler.EndScope("game.update");
Profiler.Report("my-sample");
```

The profiler prints:

- `LUB_PROFILE ...` for the measured frame window;
- `LUB_PROFILE_LUA ...` for the Lua heap in that window (not printed by the .NET run, which has no Lua VM);
- `LUB_PROFILE_SCOPE ...` for each named scope.

`LUB_PROFILE_LUA` has these fields:

| field | meaning |
| --- | --- |
| `avg_alloc_kb` | KB the Lua VM allocated per frame on average |
| `max_alloc_kb` | KB allocated by the frame that allocated the most |
| `avg_free_kb` | KB the Lua VM released per frame on average, mostly the GC's work |
| `gc_cycles` | GC cycles finished in the window |

Allocations made between two frames (in `OnEvent`, for example) count toward the next frame.
Only the Lua heap is counted, not the memory of the C runtime or the GPU.
Lua's GC has no hook for its time or steps, so a frame's GC time is not separated from `script.onFrame`.

`LUB_PROFILE_FRAME` prints once when that frame ends, and `LUB_PROFILE_EVERY`
prints every N measured frames. If the run ends before either has printed
(for example `--capture-frame 240` with `LUB_PROFILE_FRAME=300`, or neither
variable set), the frames measured so far are printed once at exit with the
label `exit` (or `LUB_PROFILE_LABEL` if set).

For Release measurements, use the Release script and give automation a 2-hour
process timeout:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

```sh
bash scripts/build-release.sh
```

For the sprite benchmark with profiling:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1 -Profile
```

```sh
bash scripts/run-sprites-bench.sh --profile
```

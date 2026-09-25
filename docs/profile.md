# Profiling

Enable the built-in profiler with environment variables. It measures frame and
scope time and, for the Lua heap, how much memory the game allocates and how
long the garbage collector runs.

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

The variables:

- `LUB_PROFILE`: turns the profiler on. Any value except empty, `0` and
  `false`.
- `LUB_PROFILE_START_FRAME`: the first recorded frame, counted from 0. Earlier
  frames are not recorded.
- `LUB_PROFILE_FRAME`: print one report when the Nth frame ends, counted from
  1 like `--capture-frame`.
- `LUB_PROFILE_EVERY`: print a report every N recorded frames, then start a new
  window.
- `LUB_PROFILE_LABEL`: the `label=` of the `LUB_PROFILE_FRAME` and
  `LUB_PROFILE_EVERY` reports. Without it the label is `frame` or `every`.

If the process ends before a `LUB_PROFILE_FRAME` or `LUB_PROFILE_EVERY` report
was printed (a short run, or `LUB_PROFILE_FRAME` later than the last frame),
the runtime prints one report with label `exit` when it shuts down. With
`--capture-frame N`, a `LUB_PROFILE_FRAME` of N or less gives the `frame`
report; a larger value gives the `exit` report over the same frames. A script
that ends the process with `os.exit` gets no `exit` report.

## Scopes

The runtime always records these scopes when profiling is enabled:

- `runtime.begin_frame`
- `script.onFrame`
- `runtime.pass_guard`
- `runtime.end_frame`

Game code can add its own scopes:

```csharp
using static Lub;

Profiler.BeginScope("game.update");
Update();
Profiler.EndScope("game.update");
Profiler.Report("my-sample");
```

A scope's time and allocation include its inner scopes.

## Output

The profiler prints:

- `LUB_PROFILE ...` for the measured frame window;
- `LUB_PROFILE_SCOPE ...` for each named scope;
- `LUB_PROFILE_HEAP ... heap=lua` for the Lua heap;
- `LUB_PROFILE_HEAP ... heap=dotnet` for the managed heap, in .NET runs only.

Example from `13_sprites` run headless (lavapipe) with
`LUB_PROFILE_START_FRAME=60 LUB_PROFILE_FRAME=240`, some scope lines left out:

```text
LUB_PROFILE label=frame frames=180 avg_frame_ms=4.531 max_frame_ms=31.950 alloc_kb_avg=58.507 alloc_kb_max=123.113 gc_ms_avg=0.055 gc_steps_avg=1.1
LUB_PROFILE_SCOPE label=frame name=script.onFrame calls=180 total_ms=154.048 avg_ms=0.856 max_ms=2.895 pct=18.9 alloc_kb=10531.346 alloc_kb_avg=58.507 gc_ms=9.884
LUB_PROFILE_SCOPE label=frame name=sprites.update calls=180 total_ms=10.027 avg_ms=0.056 max_ms=0.140 pct=1.2 alloc_kb=95.766 alloc_kb_avg=0.532 gc_ms=0.000
LUB_PROFILE_SCOPE label=frame name=sprites.draw calls=180 total_ms=29.179 avg_ms=0.162 max_ms=0.642 pct=3.6 alloc_kb=2220.734 alloc_kb_avg=12.337 gc_ms=2.044
LUB_PROFILE_SCOPE label=frame name=sprites.hud calls=180 total_ms=63.745 avg_ms=0.354 max_ms=1.289 pct=7.8 alloc_kb=7925.510 alloc_kb_avg=44.031 gc_ms=7.649
LUB_PROFILE_SCOPE label=frame name=runtime.end_frame calls=180 total_ms=655.491 avg_ms=3.642 max_ms=30.889 pct=80.4 alloc_kb=0.000 alloc_kb_avg=0.000 gc_ms=0.000
LUB_PROFILE_HEAP label=frame heap=lua frames=180 alloc_kb_avg=58.507 alloc_kb_max=123.113 allocs_avg=1269.6 free_kb_avg=53.892 live_kb=1496.5 gc_steps=200 gc_cycles=11 gc_ms_total=9.884 gc_ms_avg=0.055 gc_ms_max_frame=0.847 gc_ms_max_step=0.532 gc_pct=1.2 outside_kb=0.000
```

Here the HUD text makes three quarters of the frame's garbage.

`LUB_PROFILE`:

- `frames`, `avg_frame_ms`, `max_frame_ms`: recorded frames and their wall
  time.
- `alloc_kb_avg`, `alloc_kb_max`: Lua allocation per frame, on average and in
  the largest frame.
- `gc_ms_avg`, `gc_steps_avg`: GC time and GC steps per frame.

`LUB_PROFILE_SCOPE`:

- `calls`, `total_ms`, `avg_ms`, `max_ms`, and `pct` (share of the total frame
  time).
- `alloc_kb`: Lua allocation inside the scope over the window; `alloc_kb_avg`
  per call.
- `gc_ms`: GC time inside the scope.

`LUB_PROFILE_HEAP heap=lua`:

- `alloc_kb_avg`, `alloc_kb_max`: as above. `allocs_avg`: new blocks per
  frame.
- `free_kb_avg`: bytes given back per frame (collected objects and shrunk
  blocks).
- `live_kb`: the Lua heap size when the report was printed.
- `gc_steps`, `gc_cycles`: GC steps and finished collection cycles in the
  window.
- `gc_ms_total`, `gc_ms_avg`, `gc_ms_max_frame`, `gc_ms_max_step`: GC time in
  the window, per frame, in the largest frame, and in the largest single step.
- `gc_pct`: GC time as a share of the frame time.
- `outside_kb`: allocation in the window but outside frames: `on_event` between
  frames, the gap just before the first recorded frame, and `on_quit` in the
  `exit` report. With `LUB_PROFILE_START_FRAME=0` the gap before frame 0
  includes loading the scripts and `on_init`.

## What the heap numbers mean

`alloc` is the number of bytes the Lua state asks its allocator for: new
tables, strings, closures and userdata, and growing tables and string buffers.
It is not the heap growth. A frame that makes 50 KB of temporary tables shows
50 KB even though the heap keeps its size, because the collector has to free
those 50 KB later. So `alloc_kb_avg` is the number to watch for GC pressure. It
also shows cost that neither Lua instruction counts nor C scope times reveal,
since the collector runs at allocation points spread over the script and the
bindings.

Unlike time, allocation hardly depends on GPU speed or timing, so runs of the
same script and input give nearly the same numbers. They can still differ a
little: Lua picks a new string hash seed on every run, which can move when a
table grows. Block sizes differ between the 64-bit native build and the 32-bit
web build.

The collector runs in small steps at allocation points (creating tables,
joining strings, bindings that return tables). Each step is timed. Full
collections are not timed or counted: `collectgarbage("collect")`, and the
emergency collection Lua runs when an allocation fails.

`live_kb` counts every block the Lua state holds, including string buffers
built by the Lua libraries (`string.rep`, `table.concat` and the like) and the
long strings made from them. `collectgarbage("count")` leaves those out, so it
can be lower by their size.

With `LUB_PROFILE` off, the Lua state uses the normal allocator and nothing is
counted. With it on, every allocation goes through a counting wrapper and every
GC step reads the clock twice. Measured on headless Linux over frames 60 to
240, `avg_frame_ms` and the `script.onFrame` time stayed within run-to-run
noise both for `13_sprites` (about 60 KB per frame) and for `12_sfb` (about
3 MB per frame).

## .NET runs

When a game runs on .NET (`dotnet/Lub`), its Lua state is not used: the
`heap=lua` numbers stay near 0, and the allocation fields of `LUB_PROFILE` and
`LUB_PROFILE_SCOPE` count Lua only. The host reports the managed heap instead:

```text
LUB_PROFILE_HEAP label=frame heap=dotnet frames=180 alloc_kb_avg=5.846 alloc_kb_max=37.633 gc_gen0=0 gc_gen1=0 gc_gen2=0 gc_pause_ms_total=0.000 gc_pause_ms_max_frame=0.000 gc_pct=0.0
```

- `alloc_kb_avg`, `alloc_kb_max`: managed allocation on the main thread
  (`GC.GetAllocatedBytesForCurrentThread`) per frame, taken from the end of one
  frame to the end of the next, so `OnEvent` counts too.
- `gc_gen0`, `gc_gen1`, `gc_gen2`: collections in the window
  (`GC.CollectionCount`; a generation 2 collection also counts in 0 and 1).
- `gc_pause_ms_total`, `gc_pause_ms_max_frame`, `gc_pct`: time the collector
  paused the program (`GC.GetTotalPauseDuration`).

## Web

The web player takes environment variables only from its `?env=` URL
parameter, which only dev and test builds accept, and the playground does not
set it. Profiling on the web is for test harnesses. Browser clocks are coarse,
so `gc_steps` and `gc_cycles` are more reliable there than GC times.

## Allocation test

`tests/lua/test_profile_alloc.lua` runs in `scripts/native-gate.sh` with
`LUB_PROFILE=1`. Every frame it makes the same amount of garbage, measured once
with `collectgarbage("count")` while the collector is stopped, and the gate
reads the `exit` report: frame and scope allocation must match that amount, GC
steps and cycles must be counted, and a scope that only calls bindings
returning plain values must allocate nothing.

## Release measurements

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

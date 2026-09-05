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
using static Lub;

Profiler.BeginScope("game.update");
Update();
Profiler.EndScope("game.update");
Profiler.Report("my-sample");
```

The profiler prints:

- `LUB_PROFILE ...` for the measured frame window;
- `LUB_PROFILE_SCOPE ...` for each named scope.

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

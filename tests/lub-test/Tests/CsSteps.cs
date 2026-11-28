namespace LubTest.Tests;

/// <summary>Tests of tests/cs that take more than one run, or particular launch arguments or environment.</summary>
public static class CsSteps
{
    public static IEnumerable<Test> All(Site site)
    {
        var list = new CsList(site);
        // .NET execution shares the C runtime with the tcs VM player, so it is run on one OS
        string[] runners = site.Linux ? ["vm", "dotnet"] : ["vm"];
        // Runs one entry on the tcs VM player or on .NET execution
        Task<Output> On(Run run, string runner, string entry, IEnumerable<string> args, IDictionary<string, string?>? env = null) =>
            runner == "vm" ? CsRuntime.OnVm(run, entry, site.Backend, args, env) : CsRuntime.OnDotnet(run, entry, args, env);

        foreach (var runner in runners)
        {
            foreach (var dt in new[] { "0.0125", CsRuntime.FixedDt })
                yield return new Test($"cs.TestFixedDt.{dt}.{runner}", ["cs", "cs-steps"], run => On(run, runner, "TestFixedDt", ["--fixed-dt", dt]));
        }

        // The backend reuses the binds of the previous draw; each draw must still render with its own state.
        // The frame in which a readback arrives is the same on every backend, and without --readback-fixed-latency it does not wait for the GPU.
        // sdlgpu is covered by the runtime tests, so these run on the backend the OS has besides it
        string direct = site.Linux ? "vulkan" : site.Backend;
        yield return new Test($"cs.TestDrawStateReuse.{direct}", ["cs", "cs-steps", direct], run => CsRuntime.OnVm(run, "TestDrawStateReuse", direct, list.Args));
        foreach (var entry in new[] { "TestReadbackFixedLatency", "TestReadbackNoWait" })
            yield return new Test($"cs.{entry}.{direct}.nowait", ["cs", "cs-steps", direct], run => CsRuntime.OnVm(run, entry, direct, []));

        if (site.Linux)
        {
            foreach (var runner in runners)
            {
                // save/<name>/ goes into $XDG_DATA_HOME/<name>/, so the test writes into a directory of its own instead of the user's data
                yield return new Test($"cs.TestSaveDir.{runner}", ["cs", "cs-steps"], async run =>
                {
                    await On(run, runner, "TestSaveDir", list.Args, new Dictionary<string, string?> { ["XDG_DATA_HOME"] = run.Temp });
                    var saved = Path.Combine(run.Temp, "lub-test", "scores.txt");
                    run.Check(File.Exists(saved) && File.ReadAllText(saved).Trim() == "saved", "expected 'saved' in " + saved);
                });
                // The test checks the capture PNG in OnQuit, with and without loading it back
                foreach (var noLoad in new[] { "0", "1" })
                {
                    yield return new Test($"cs.TestReadbackCapture.{(noLoad == "0" ? "load" : "noload")}.{runner}", ["cs", "cs-steps"], async run =>
                    {
                        var png = Path.Combine(run.Temp, "capture.png");
                        var env = new Dictionary<string, string?> { ["LUB_READBACK_CAPTURE_TEST_OUT"] = png, ["LUB_READBACK_CAPTURE_TEST_NO_LOAD"] = noLoad };
                        await On(run, runner, "TestReadbackCapture", [.. list.Args, "--capture", png, "--capture-frame", "5", "--fixed-dt", CsRuntime.FixedDt], env);
                    });
                }
            }
            // Blend is a value struct record: the per-frame digests (which include the Blend of the draws) are the same on the tcs VM player and on .NET execution
            yield return new Test("cs.TestBlendValue.digest", ["cs", "cs-steps"], async run =>
            {
                string[] args = [.. list.Args, "--fixed-dt", CsRuntime.FixedDt, "--digest"];
                var vm = await On(run, "vm", "TestBlendValue", args);
                var dotnet = await On(run, "dotnet", "TestBlendValue", args);
                SameDigests(run, "the tcs VM player", vm, ".NET execution", dotnet);
            });
            // Bindings made new every frame, reused, and cleared and refilled make the same C API calls, on the tcs VM player and on .NET execution
            yield return new Test("cs.TestBindingsDigest", ["cs", "cs-steps"], async run =>
            {
                Output? first = null;
                foreach (var runner in runners)
                {
                    foreach (var mode in new[] { "new", "reuse", "clear" })
                    {
                        var output = await On(run, runner, "TestBindingsDigest", ["--fixed-dt", CsRuntime.FixedDt, "--digest"],
                            new Dictionary<string, string?> { ["LUB_TEST_BINDINGS_MODE"] = mode });
                        first ??= output;
                        SameDigests(run, "vm new", first, runner + " " + mode, output);
                    }
                }
            });
        }

        // `lub export` uses neither the GPU nor a display: it compiles the shaders used in OnExport for the players of the target, puts them in cache/shader/ of the snapshot, and copies the files.
        // The player started inside the snapshot draws the same picture as the one run from the repository
        if (!site.Mac)
        {
            foreach (var entry in new[] { "TestVertexPull", "TestDepthSample" })
            {
                yield return new Test($"cs.export.{entry}", ["cs", "cs-steps", "export"], async run =>
                {
                    await Prepare.Lub(run);
                    await Prepare.CsTests(run);
                    var snapshot = Path.Combine(run.Temp, "snapshot");
                    await run.Must(site.Lub, ["export", site.Os, Prepare.AllTcsb(site), "--entry", entry, "-o", snapshot],
                        new Dictionary<string, string?> { ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null });
                    string[] capture = ["--capture-frame", "5", "--fixed-dt", CsRuntime.FixedDt];
                    var fromRepo = Path.Combine(run.Temp, "repo.png");
                    var fromSnapshot = Path.Combine(run.Temp, "snapshot.png");
                    await CsRuntime.OnVm(run, entry, site.Backend, ["--capture", fromRepo, .. capture]);
                    await CsRuntime.OnVm(run, entry, site.Backend, ["--capture", fromSnapshot, .. capture], cwd: snapshot, module: entry + ".tcsb");
                    run.Check(File.ReadAllBytes(fromRepo).AsSpan().SequenceEqual(File.ReadAllBytes(fromSnapshot)), "the snapshot draws a different picture");
                    if (site.Windows)
                    {
                        // Windows compiles every shader for two targets: d3d12 (DXIL) and vulkan / sdlgpu (SPIR-V)
                        int shaders = entry == "TestVertexPull" ? 1 : 2;
                        int files = Directory.GetFiles(Path.Combine(snapshot, "cache", "shader")).Length;
                        run.Check(files == shaders * 2, $"expected {shaders * 2} files in cache/shader, got {files}");
                    }
                });
            }
        }
    }

    /// <summary>The shader cache on macOS: `lub export macos` fills the cache of a snapshot, and a player without Slang (what an iOS app is) run in the snapshot
    /// renders the same frame as the player with Slang run from the repository, and names the key when a shader is missing.</summary>
    public static IEnumerable<Test> ShaderCache(Site site)
    {
        if (!site.Mac) yield break;
        string[] capture = ["--capture-frame", "30", "--fixed-dt", CsRuntime.FixedDt];
        string playerBuild = site.Path_("build-mac-player");
        string player = Path.Combine(playerBuild, "lub");
        Task Player(Run run) => site.Need("player-without-slang", () => Prepare.BuildLub(run, playerBuild, "-DLUB_NO_SLANG=ON"));
        async Task<string> Export(Run run, string entry)
        {
            await Prepare.Lub(run);
            await Prepare.CsTests(run);
            var snapshot = Path.Combine(run.Temp, "snapshot-" + entry + "-" + Directory.GetDirectories(run.Temp).Length);
            await run.Must(site.Lub, ["export", "macos", Prepare.AllTcsb(site), "--entry", entry, "-o", snapshot]);
            return snapshot;
        }
        // One test, so that only one share of the tests builds the second player
        yield return new Test("cs.shader-cache", ["cs", "export"], async run =>
        {
            await Player(run);
            foreach (var entry in new[] { "TestVertexPull", "TestDepthSample" })
            {
                var snapshot = await Export(run, entry);
                var withSlang = Path.Combine(run.Temp, entry + "-slang.png");
                var fromCache = Path.Combine(run.Temp, entry + "-cache.png");
                await CsRuntime.OnVm(run, entry, "metal", ["--capture", withSlang, .. capture]);
                var cached = await run.Headless(player, [entry + ".tcsb", "--capture", fromCache, .. capture], "metal", cwd: snapshot);
                run.Check(cached.Ok, "the player without Slang exited with " + cached.ExitCode);
                run.Check(File.ReadAllBytes(withSlang).AsSpan().SequenceEqual(File.ReadAllBytes(fromCache)), entry + ": the player without Slang draws a different picture");
            }
            var missing = await Export(run, "TestVertexPull");
            Directory.Delete(Path.Combine(missing, "cache"), true);
            var output = await run.Headless(player, ["TestVertexPull.tcsb", "--capture", Path.Combine(run.Temp, "miss.png"), "--capture-frame", "2", "--fixed-dt", CsRuntime.FixedDt],
                "metal", cwd: missing);
            run.Check(output.Text.Contains("this player has no shader compiler", StringComparison.Ordinal), "expected a shader cache miss error");
        })
        { Timeout = TimeSpan.FromHours(1) };
    }

    /// <summary>.NET execution on Windows: the facade calls the shared library (lub.dll) through P/Invoke, and the frame digests of one sample are the same as on the tcs VM player.</summary>
    public static IEnumerable<Test> DotnetSample(Site site)
    {
        if (!site.Windows) yield break;
        yield return new Test("samples.00_hello.dotnet-digest", ["dotnet-digest"], async run =>
        {
            await Prepare.Lub(run);
            await Prepare.Target(run, "lub_shared");
            var module = await Prepare.SampleModule(run, "00_hello");
            string[] args = ["--capture-frame", "30", "--fixed-dt", CsRuntime.FixedDt, "--digest"];
            var vm = await CsRuntime.OnVm(run, "Hello00", "d3d12", ["--capture", Path.Combine(run.Temp, "vm.png"), .. args], module: module);
            await run.Must("dotnet", ["build", "dotnet/SampleRunner", "-p:Sample=00_hello", "-nologo", "-v", "q"], timeout: TimeSpan.FromMinutes(20));
            var dotnet = await run.Headless(site.Path_("dotnet", "SampleRunner", "bin", "00_hello", "lub-sample-00_hello.exe"),
                ["--capture", Path.Combine(run.Temp, "dotnet.png"), .. args], "d3d12", Prepare.NativeLib(site));
            run.Check(dotnet.Ok, ".NET execution exited with " + dotnet.ExitCode);
            SameDigests(run, "the tcs VM player", vm, ".NET execution", dotnet);
        });
    }

    /// <summary>The `digest ` lines of two runs must be the same, and there must be some.</summary>
    public static void SameDigests(Run run, string nameA, Output a, string nameB, Output b)
    {
        static string[] Digests(Output o) => [.. o.Text.Split('\n').Where(l => l.StartsWith("digest ", StringComparison.Ordinal))];
        var da = Digests(a);
        var db = Digests(b);
        run.Check(da.Length > 0, nameA + " printed no digest");
        run.Check(da.SequenceEqual(db), $"the digests of {nameA} ({da.Length} frames) and {nameB} ({db.Length} frames) differ");
    }
}

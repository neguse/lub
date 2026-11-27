namespace LubTest.Tests;

/// <summary>Checks of the bindings, the generated files, the samples and the developer tools.</summary>
public static class Tools
{
    public static IEnumerable<Test> All(Site site)
    {
        // ---- bindings: the glue that tcs c and the tcs VM use to call lub, and lub-gen (C and .NET give the same output)
        yield return Scripted("bindings.tcs", ["bindings"], "scripts/verify-tcs-binding.sh");
        yield return Scripted("bindings.vm", ["bindings"], "scripts/verify-vm-binding.sh");
        yield return new Test("bindings.lub-gen", ["bindings"], run => run.Script("scripts/build-lub-gen.sh", ["--verify", run.Temp], timeout: TimeSpan.FromMinutes(30)));

        // ---- tools
        // The description of the API surface (cs-lib/lub_stub.cs) and the files generated from it (header, host functions of the tcs VM, API docs, facade) agree.
        // If they differ, regenerate with scripts/gen-api.sh
        yield return new Test("tools.gen-api", ["tools"], async run => await run.Script("scripts/gen-api.sh", ["--check"], await Prepare.TcsEnv(run)));
        // The facade and the host of .NET execution
        yield return new Test("tools.dotnet-bindings", ["tools"], async run =>
        {
            await Prepare.Target(run, "lub_shared");
            await run.Must("dotnet", ["build", "dotnet/Lub", "-nologo", "-v", "q"], timeout: TimeSpan.FromMinutes(20));
            await run.Must("dotnet", ["run", "--project", "tests/dotnet/Bindings", "-c", "Release"], Prepare.NativeLib(site), timeout: TimeSpan.FromMinutes(20));
        });
        // tcs vm does not stop on warnings, so the warnings of tests/cs are checked with tcs check
        yield return new Test("tools.cs-tests-check", ["tools"], async run => await run.Script("scripts/tcs-cs-tests.sh", ["check"], await Prepare.TcsEnv(run)));
        // The samples are examples: they are checked (tcs check fails on warnings too) and converted, not built with .NET
        foreach (var dir in Directory.GetDirectories(site.Path_("samples")).Order(StringComparer.Ordinal))
        {
            if (Directory.GetFiles(dir, "*.cs").Length == 0) continue;
            var sample = Path.GetFileName(dir);
            yield return new Test("samples." + sample, ["tools", "samples"], async run =>
            {
                var env = await Prepare.TcsEnv(run);
                await run.Script("scripts/run-cs-sample.sh", [sample, "--check"], env);
                await run.Script("scripts/run-cs-sample.sh", [sample, "--vm"], env);
            });
        }
        // The template is used by copying it, so it must build with real .NET
        yield return new Test("tools.template", ["tools"], run => run.Must("dotnet", ["build", "templates/game/Game.csproj", "-nologo"], timeout: TimeSpan.FromMinutes(20)));
        // lub stops the tcs child process it started, however lub itself ends
        yield return WithLub("tools.tcs-process-cleanup", ["tools", "process"], "scripts/test-tcs-process-cleanup.sh");
        yield return WithLub("tools.lub-check", ["tools"], "scripts/test-lub-check.sh");
        // lub takes the frames out of the standard output of the tcs child process (skipping kinds it does not know). Uses a fake tcs
        yield return new Test("tools.tcs-pipeline", ["tools", "process"], async run =>
        {
            await Prepare.Target(run, "lub_tcs_pipeline_smoke");
            await run.Script("scripts/test-tcs-pipeline-smoke.sh", [site.ForScript(site.Exe("lub_tcs_pipeline_smoke"))]);
        });
        // An outside project builds its own player with find_package(lub)
        yield return new Test("tools.cmake-player", ["tools"], async run =>
        {
            await Prepare.Target(run, "all");
            var build = Path.Combine(run.Temp, "player");
            await run.Must("cmake", ["-S", "tests/cmake/player", "-B", build, "-G", "Ninja", "-Dlub_DIR=" + site.Build], timeout: TimeSpan.FromMinutes(20));
            await run.Must("cmake", ["--build", build], timeout: TimeSpan.FromMinutes(30));
        });

        Test Scripted(string name, string[] labels, string script) =>
            new(name, labels, run => run.Script(script, [], timeout: TimeSpan.FromMinutes(30)));
        Test WithLub(string name, string[] labels, string script) => new(name, labels, async run =>
        {
            await Prepare.Lub(run);
            var env = await Prepare.TcsEnv(run);
            if (site.Windows)
            {
                env["LUB_BACKEND"] = "d3d12";
                env["LUB_D3D12_WARP"] = "1";
            }
            await run.Script(script, [site.ForScript(site.Lub)], env, TimeSpan.FromMinutes(15));
        });
    }
}

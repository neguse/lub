namespace LubTest.Tests;

/// <summary>Checks of the bindings, the generated files, the samples and the developer tools.</summary>
public static class Tools
{
    public static IEnumerable<Test> All(Site site)
    {
        // The C lub-gen (lub-gen converted to C with tcs c, as shipped in the release kit; scripts/build-lub-gen.sh puts it in <build>/tcs/) gives the same output as lub-gen on .NET
        yield return new Test("bindings.lub-gen", ["bindings"], async run =>
        {
            var native = Path.Combine(site.Build, "tcs", site.Windows ? "lub-gen.exe" : "lub-gen");
            run.Check(File.Exists(native), native + " is missing (build it with scripts/build-lub-gen.sh " + site.Build + ")");
            async Task Same(string name, params string[] args)
            {
                var fromC = Path.Combine(run.Temp, "native." + name);
                var fromDotnet = Path.Combine(run.Temp, "dotnet." + name);
                await run.Must(native, [.. args, "-o", fromC]);
                await Prepare.MustLubGen(run, [.. args, "-o", fromDotnet]);
                run.Check(File.ReadAllBytes(fromC).AsSpan().SequenceEqual(File.ReadAllBytes(fromDotnet)), "lub-gen " + name + ": the C and the .NET outputs differ");
            }
            foreach (var verb in new[] { "header", "vm", "docs", "facade", "facade-lubx", "model" }) await Same(verb, verb);
            // lub-gen tcs reads lib/TinySystem from the directory of the executable (as tcs does)
            string[] sources = [.. Directory.GetFiles(site.Path_("cs-lib", "lub"), "*.cs").Order(StringComparer.Ordinal),
                .. Directory.GetFiles(site.Path_("cs-lib", "lubx"), "*.cs").Order(StringComparer.Ordinal), "samples/13_sprites/Sprites13.cs"];
            await Same("tcs", ["tcs", "--stub", "cs-lib/lub_stub.cs", .. sources.SelectMany(x => new[] { "--source", x })]);
        });

        // ---- tools
        // The description of the API surface (cs-lib/lub_stub.cs) and the files generated from it agree (GenApi.cs). If they differ, regenerate with scripts/gen-api.sh
        yield return new Test("tools.gen-api", ["tools"], run => GenApi.Generate(run, check: true));
        // The facade and the host of .NET execution
        yield return new Test("tools.dotnet-bindings", ["tools"], async run =>
        {
            await Prepare.Target(run, "lub_shared");
            await run.Must("dotnet", ["build", "dotnet/Lub", "-nologo", "-v", "q"], timeout: TimeSpan.FromMinutes(20));
            await run.Must("dotnet", ["run", "--project", "tests/dotnet/Bindings", "-c", "Release"], Prepare.NativeLib(site), timeout: TimeSpan.FromMinutes(20));
        });
        // tcs vm does not stop on warnings, so the warnings of tests/cs are checked with tcs check
        yield return new Test("tools.cs-tests-check", ["tools"], run =>
            run.MustTcs(["check", .. Prepare.Sources(site, site.Path_("tests", "cs")), "--ref", "cs-lib/lub_stub.cs"]));
        // The samples are examples: they are checked (tcs check fails on warnings too) and converted, not built with .NET
        foreach (var dir in Directory.GetDirectories(site.Path_("samples")).Order(StringComparer.Ordinal))
        {
            if (Directory.GetFiles(dir, "*.cs").Length == 0) continue;
            var sample = Path.GetFileName(dir);
            yield return new Test("samples." + sample, ["tools", "samples"], async run =>
            {
                await run.MustTcs(["check", .. Prepare.Sources(site, dir), "--ref", "cs-lib/lub_stub.cs"]);
                await Prepare.SampleModule(run, sample);
            });
        }
        // The template is used by copying it, so it must build with real .NET
        yield return new Test("tools.template", ["tools"], run => run.Must("dotnet", ["build", "templates/game/Game.csproj", "-nologo"], timeout: TimeSpan.FromMinutes(20)));
        // An outside project builds its own player with find_package(lub)
        yield return new Test("tools.cmake-player", ["tools"], async run =>
        {
            await Prepare.Target(run, "all");
            var build = Path.Combine(run.Temp, "player");
            await run.Must("cmake", ["-S", "tests/cmake/player", "-B", build, "-G", "Ninja", "-Dlub_DIR=" + site.Build], timeout: TimeSpan.FromMinutes(20));
            await run.Must("cmake", ["--build", build], timeout: TimeSpan.FromMinutes(30));
        });
    }
}

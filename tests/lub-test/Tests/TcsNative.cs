namespace LubTest.Tests;

/// <summary>The C tcs (tcs converted to C with tcs c; &lt;build&gt;/tcs/tcs, built by scripts/build-tcs-native.sh before the tests) gives the same output as the .NET tcs:
/// the diagnostics and exit codes of check, the C of tcs c, the .tcsb of tcs vm, the reload batch, and what the file operations do.
/// For tcs itself as the input, the C is the same too: the C tcs produces its own C (the fixed point).</summary>
public static class TcsNative
{
    public static IEnumerable<Test> All(Site site)
    {
        string[] labels = ["tcs-native"];
        // The samples: check, C, and .tcsb with the static methods of the entry class exported
        foreach (var (name, cls) in new[]
                 {
                     ("hello", "Hello"), ("game", "Battle"), ("inventory", "Game"), ("entity", "EntitySample"), ("statemachine", "StateMachineSample"),
                     ("collision", "CollisionSample"), ("host_api_game", "HostApiSample"),
                 })
        {
            yield return new Test("tcs.native.sample." + name, labels, async run =>
            {
                var file = "tests/tcs/samples/" + name + ".cs";
                string[] refs = name == "host_api_game" ? ["--ref", "tests/tcs/samples/host_api_stub.cs"] : [];
                await SameRun(run, "check", ["check", file, .. refs]);
                await SameFile(run, "c", ["c", file, .. refs], "out.c");
                await SameFile(run, "vm", ["vm", "--export", cls + ".*", file, .. refs], "out.tcsb");
            });
        }
        // The diagnostics of an input with an error
        yield return new Test("tcs.native.bad-check", labels, async run =>
        {
            var bad = Path.Combine(run.Temp, "Bad.cs");
            File.WriteAllText(bad, "public class Bad { public static int F() { try { return 1; } finally { } } }\n");
            // The path is in the diagnostics, so both run in the directory of the file
            await SameRun(run, "check", ["check", "Bad.cs"], run.Temp);
        });
        // tcs itself as the input
        yield return new Test("tcs.native.self.check", labels, async run => await SameRun(run, "check", ["check", .. await Self(run)]));
        yield return new Test("tcs.native.self.vm", labels, async run => await SameFile(run, "vm", ["vm", "--export", "Tcs.Cli.*", .. await Self(run)], "self.tcsb"));
        yield return new Test("tcs.native.self.c", labels, async run => await SameFile(run, "c", ["c", "--lib", .. await Self(run)], "self.c"));
        // The digest kernels (the entry is Main)
        foreach (var (kernel, entry) in new[] { ("particles", "Particles"), ("spawn_churn", "SpawnChurn"), ("sprite_update", "SpriteUpdate") })
        {
            yield return new Test("tcs.native.kernel." + kernel, labels, run =>
                SameFile(run, "vm", ["vm", "--entry", entry, "tests/tcs/DigestKernels/" + kernel + ".cs"], "out.tcsb"));
        }
        // The module and the batch of a reload
        yield return new Test("tcs.native.reload", labels, async run =>
        {
            const string host = "tests/tcs-vm/host/";
            foreach (var side in new[] { "dotnet", "native" })
            {
                var output = await Tcs(run, side, ["vm", "--export", "Game.*", "--reload-from", host + "ReloadV1.cs.txt", host + "ReloadV2.cs.txt",
                    "-o", Path.Combine(run.Temp, side + ".tcsb"), "--batch", Path.Combine(run.Temp, side + ".batch")]);
                run.Check(output.Ok, side + ": tcs exited with " + output.ExitCode);
            }
            run.Check(Bytes(run, "dotnet.batch").SequenceEqual(Bytes(run, "native.batch")), "the batches differ");
        });
        yield return new Test("tcs.native.files", labels, Files);
    }

    private static string Native(Run run)
    {
        var native = Path.Combine(run.Site.Build, "tcs", run.Site.Windows ? "tcs.exe" : "tcs");
        run.Check(File.Exists(native), native + " is missing (build it with scripts/build-tcs-native.sh " + run.Site.Build + ")");
        return native;
    }

    private static byte[] Bytes(Run run, string name) => File.ReadAllBytes(Path.Combine(run.Temp, name));

    /// <summary>The arguments that give tcs its own source (what src/tcs/sources.sh prints): the declarations as --ref, then Compiler, CBackend, VmBackend, Api and Cli.</summary>
    private static Task<string[]> Self(Run run)
    {
        string[] refs = ["src/tcs/Api/TcsFiles.cs", "src/tcs/Cli/CliProcess.cs"];
        var sources = new[] { "Compiler", "CBackend", "VmBackend", "Api", "Cli" }
            .SelectMany(d => Directory.GetFiles(run.Site.Path_("src", "tcs", d), "*.cs").Order(StringComparer.Ordinal))
            .Select(f => Path.GetRelativePath(run.Site.Root, f).Replace('\\', '/')).Where(f => !refs.Contains(f));
        return Task.FromResult<string[]>([.. refs.SelectMany(r => new[] { "--ref", r }), .. sources]);
    }

    /// <summary>Runs the .NET tcs or the C tcs. The C tcs reads the files shipped with it from the directory of its executable.</summary>
    private static async Task<Output> Tcs(Run run, string side, string[] args, string? cwd = null)
    {
        if (side == "dotnet") return await run.Tcs(args, cwd);
        return await run.Exec(Native(run), args, new Dictionary<string, string?> { ["TCS_HOME"] = null }, cwd, TimeSpan.FromMinutes(20));
    }

    /// <summary>Both give the same stdout, stderr and exit code.</summary>
    private static async Task SameRun(Run run, string what, string[] args, string? cwd = null)
    {
        var dotnet = await Tcs(run, "dotnet", args, cwd);
        var native = await Tcs(run, "native", args, cwd);
        run.Check(dotnet.ExitCode == native.ExitCode, $"tcs {what}: exit code {dotnet.ExitCode} on .NET, {native.ExitCode} in C");
        run.Check(dotnet.Stdout == native.Stdout, $"tcs {what}: stdout differs");
        run.Check(dotnet.Stderr == native.Stderr, $"tcs {what}: stderr differs");
    }

    /// <summary>Both succeed and write the same bytes with -o.</summary>
    private static async Task SameFile(Run run, string what, string[] args, string name)
    {
        foreach (var side in new[] { "dotnet", "native" })
        {
            var output = await Tcs(run, side, [.. args, "-o", Path.Combine(run.Temp, side + "." + name)]);
            run.Check(output.Ok, $"tcs {what} ({side}) exited with {output.ExitCode}");
        }
        run.Check(Bytes(run, "dotnet." + name).SequenceEqual(Bytes(run, "native." + name)), $"tcs {what}: the outputs differ");
    }

    /// <summary>File operations: the C implementation (Native/files.c) and the .NET one (Dotnet/Files.cs) behave the same.
    /// Each tcs runs the same command in its own copy of the same files, and the output, the exit code and the files left behind are compared.</summary>
    private static async Task Files(Run run)
    {
        string Dir(string side) => Path.Combine(run.Temp, side);
        foreach (var side in new[] { "dotnet", "native" })
        {
            Directory.CreateDirectory(Dir(side));
            File.WriteAllText(Path.Combine(Dir(side), "a.cs"), "public static class A { public static int F() { return 1; } }\n");
            // A UTF-8 byte order mark is not part of the text
            File.WriteAllBytes(Path.Combine(Dir(side), "bom.cs"),
                [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("public static class Bom { public static int F() { return 2; } }\n")]);
            Directory.CreateDirectory(Path.Combine(Dir(side), "dir.cs"));
        }
        async Task<(Output Dotnet, Output Native)> Both(params string[] args) => (await Tcs(run, "dotnet", args, Dir("dotnet")), await Tcs(run, "native", args, Dir("native")));
        async Task Same(string what, params string[] args)
        {
            var (dotnet, native) = await Both(args);
            run.Check(dotnet.ExitCode == native.ExitCode && dotnet.Stdout == native.Stdout && dotnet.Stderr == native.Stderr, what + ": the output or the exit code differs");
        }
        void SameContent(string file) =>
            run.Check(File.ReadAllBytes(Path.Combine(Dir("dotnet"), file)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Dir("native"), file))), file + " differs");

        await Same("an input with a byte order mark", "check", "bom.cs");
        await Same("a missing input", "c", "missing.cs", "-o", "out.c");
        await Same("a directory as the input", "c", "dir.cs", "-o", "out.c");
        await Same("the output is the input", "c", "a.cs", "-o", "a.cs");
        SameContent("a.cs");
        // The directories of the output path are made, and no temporary file is left next to the output
        await Same("new directories", "vm", "--export", "A.*", "a.cs", "-o", "new/deep/a.tcsb");
        SameContent(Path.Combine("new", "deep", "a.tcsb"));
        string[] Left(string side) => [.. Directory.GetFileSystemEntries(Path.Combine(Dir(side), "new", "deep")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        run.Check(Left("dotnet").SequenceEqual(Left("native")) && Left("native").Length == 1, "something is left next to the output");
        // Links and permission bits are not checked on Windows
        if (OperatingSystem.IsWindows()) return;

        // A link to an input is the same file as the input
        foreach (var side in new[] { "dotnet", "native" }) File.CreateSymbolicLink(Path.Combine(Dir(side), "link.cs"), "a.cs");
        await Same("the output is a link to the input", "c", "a.cs", "-o", "link.cs");
        SameContent("a.cs");
        // Replacing an output keeps its permissions
        const UnixFileMode kept = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        foreach (var side in new[] { "dotnet", "native" })
        {
            var keep = Path.Combine(Dir(side), "keep.c");
            File.WriteAllText(keep, "old\n");
            File.SetUnixFileMode(keep, kept);
        }
        await Same("replacing an output", "c", "a.cs", "-o", "keep.c");
        SameContent("keep.c");
        foreach (var side in new[] { "dotnet", "native" })
            run.Check(File.GetUnixFileMode(Path.Combine(Dir(side), "keep.c")) == kept, side + ": replacing changed the permissions");
        // An output that cannot be written fails, and stays as it was. The reason names the OS error in words that differ between C and .NET, so only the exit code is compared
        foreach (var side in new[] { "dotnet", "native" })
        {
            var locked = Path.Combine(Dir(side), "locked.c");
            File.WriteAllText(locked, "locked\n");
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        var (ld, ln) = await Both("c", "a.cs", "-o", "locked.c");
        run.Check(ld.ExitCode == ln.ExitCode && !ld.Ok, $"a read-only output: exit code {ld.ExitCode} on .NET, {ln.ExitCode} in C");
        SameContent("locked.c");
        run.Check(File.ReadAllText(Path.Combine(Dir("native"), "locked.c")) == "locked\n", "the read-only output was changed");
    }
}

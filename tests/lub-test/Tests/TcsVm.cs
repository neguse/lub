using System.Text;
using System.Text.RegularExpressions;

namespace LubTest.Tests;

/// <summary>The tests of the tcs VM (docs/tcs/vm-design.md §16): programs run on the VM (tcs vm, then tcsvm) and are compared with the expected stdout,
/// and C hosts that link the VM library check the host API, tracing, inspection, profiling and the mixing of C modules.</summary>
public static class TcsVm
{
    // Inputs that the C backend cannot run (only the expected stdout is checked).
    // OutCalls: tcs c does not handle the discarded out of `int.TryParse(s, out _)` (unbound IL variable: _)
    private static readonly string[] NoC = ["OutCalls"];

    public static IEnumerable<Test> All(Site site)
    {
        var dir = site.Path_("tests", "tcs-vm");
        foreach (var source in Directory.GetFiles(dir, "*.cs").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(source);
            yield return Program(name, [source], name, run => Task.FromResult(File.ReadAllText(Path.Combine(dir, "expected", name + ".txt"))));
        }
        // The tcs samples: the same entry expressions and expected strings as SampleParityTests in tests/tcs-backends
        foreach (var (sample, expression, expected) in new[]
                 {
                     ("hello", "Hello.Greet(\"TinyC#\") + \",\" + Hello.Add(2, 3).ToString()", "Hello, TinyC#!,5"),
                     ("game", "Battle.Run()", "Dragon: HP=145 [attacking] | alive=2"),
                     ("inventory", "Game.Test()", "Items=4 Total=330 Best=Sword Shield=1"),
                     ("entity", "EntitySample.Run()", "Slime:enemy@6,2 HP=13"),
                     ("statemachine", "StateMachineSample.Run()", "open,open,locked,closed"),
                     ("collision", "CollisionSample.Run()", "hit,miss,hit"),
                 })
        {
            yield return new Test("tcs.vm.sample." + sample, ["tcs", "tcs-scripts"], async run =>
            {
                var entry = Path.Combine(run.Temp, "entry.cs");
                File.WriteAllText(entry, $"public static class TcsEntry {{ public static void Main() {{ System.Console.WriteLine({expression}); }} }}\n");
                await RunProgram(run, "sample_" + sample, [site.Path_("tests", "tcs", "samples", sample + ".cs"), entry], "TcsEntry", expected + "\n", null);
            });
        }
        // The digest kernels: the same fixed digests as the C checks (TcsC). particles_struct does the same operations as particles, so its digest is the same
        foreach (var (kernel, entry, digest) in new[]
                 {
                     ("sprite_update", "SpriteUpdate", "e8814b32"), ("spawn_churn", "SpawnChurn", "9274159d"), ("particles", "Particles", "8bf97e09"),
                     ("particles_struct", "ParticlesStruct", "8bf97e09"),
                 })
        {
            yield return new Test("tcs.vm.digest." + kernel, ["tcs", "tcs-scripts"], run =>
                RunProgram(run, kernel, [site.Path_("tests", "tcs", "DigestKernels", kernel + ".cs")], entry, null, digest));
        }

        // host_test.c registers the surface of stub.cs as host functions and calls the entries of Game.
        // With the Reject.cs module, loading is rejected when the registration does not match the structs of the module
        yield return new Test("tcs.vm.host", ["tcs", "tcs-scripts"], async run =>
        {
            var host = Path.Combine(dir, "host");
            var exe = await HostProgram(run, Path.Combine(host, "host_test.c"), ["-std=c11", "-O1", "-g", "-ffp-contract=off"]);
            var module = Path.Combine(run.Temp, "host.tcsb");
            var reject = Path.Combine(run.Temp, "reject.tcsb");
            await run.MustTcs(["vm", "--ref", Path.Combine(host, "stub.cs"), "--export", "Game.*", Path.Combine(host, "Game.cs"), "-o", module]);
            await run.MustTcs(["vm", "--ref", Path.Combine(host, "stub.cs"), "--export", "Reject.*", Path.Combine(host, "Reject.cs"), "-o", reject]);
            foreach (var mode in new[] { Array.Empty<string>(), ["--gc-stress"] })
            {
                var output = await run.Must(exe, [module, reject, .. mode]);
                run.Check(output.Stdout == File.ReadAllText(Path.Combine(host, "expected.txt")), "stdout differs from host/expected.txt" + (mode.Length > 0 ? " under --gc-stress" : ""));
            }
        });
        // The (line, self) pairs of --trace, without the lines passed only with self being another object of the same type as the remembered one
        yield return Hosted("trace", ["vm", "--ref", "stub.cs", "--export", "Game.*", "--trace", "Game.cs", "Game.cs"], []);
        // Viewing part of a large array: tvm_inspect spends no effort on the elements outside the requested range
        yield return Hosted("inspect", ["vm", "--export", "Game.*", "Game.cs"], []);
        // The cost per line: the time and the allocations of the --trace lines and the GC pauses, as fixed values with the test clock
        yield return Hosted("profile", ["vm", "--ref", "stub.cs", "--export", "Game.*", "--trace", "Game.cs", "Game.cs", "Lib.cs"], ["-pthread"]);
        // Mixed mode (docs/tcs/object-model.md M-4): functions in the C made by tcs c --lib read and write VM objects
        yield return new Test("tcs.vm.mixed", ["tcs", "tcs-scripts"], async run =>
        {
            var mixed = Path.Combine(dir, "mixed");
            var c = Path.Combine(run.Temp, "mixed.c");
            var module = Path.Combine(run.Temp, "mixed.tcsb");
            await run.MustTcs(["c", "--lib", "-o", c, Path.Combine(mixed, "Mixed.cs")]);
            await run.MustTcs(["vm", "--export", "Game.*", Path.Combine(mixed, "Mixed.cs"), "-o", module]);
            var exe = await HostProgram(run, Path.Combine(mixed, "mixed_test.c"), ["-std=gnu11", "-O1", "-g", $"-DMIXED_C=\"{c}\""]);
            var output = await run.Must(exe, [module]);
            run.Check(output.Text == File.ReadAllText(Path.Combine(mixed, "expected.txt")), "the output differs from mixed/expected.txt");
        });
        // Mixing per module (docs/tcs/object-model.md §10): the editor is C from tcs c --lib --vm-host, and the content is reloaded in the VM.
        // The heap is shared: run normally, and with the GC stress of the C runtime (the nursery is filled with poison values and everything is collected every frame)
        yield return new Test("tcs.vm.modules", ["tcs", "tcs-scripts"], async run =>
        {
            var mods = Path.Combine(dir, "modules");
            var v1 = Path.Combine(run.Temp, "Content.cs");
            var v2 = Path.Combine(run.Temp, "Content2.cs");
            File.Copy(Path.Combine(mods, "ContentV1.cs.txt"), v1);
            File.Copy(Path.Combine(mods, "ContentV2.cs.txt"), v2);
            var editor = Path.Combine(run.Temp, "editor.c");
            var content = Path.Combine(run.Temp, "content1.tcsb");
            var batch = Path.Combine(run.Temp, "content2.batch");
            await run.MustTcs(["c", "--lib", "--vm-host", "-o", editor, Path.Combine(mods, "Editor.cs")]);
            await run.MustTcs(["vm", "--ref", Path.Combine(mods, "Editor.cs"), "--export", "Game.*", v1, "-o", content]);
            await run.MustTcs(["vm", "--ref", Path.Combine(mods, "Editor.cs"), "--export", "Game.*", "--reload-from", v1, "--batch", batch, v2, "-o", Path.Combine(run.Temp, "content2.tcsb")]);
            foreach (var stress in new[] { Array.Empty<string>(), ["-DTCS_GC_STRESS"] })
            {
                var exe = await HostProgram(run, Path.Combine(mods, "modules_test.c"), ["-std=gnu11", "-O1", "-g", .. stress, $"-DEDITOR_C=\"{editor}\""]);
                var output = await run.Must(exe, [content, batch]);
                run.Check(output.Text == File.ReadAllText(Path.Combine(mods, "expected.txt")), "the output differs from modules/expected.txt" + (stress.Length > 0 ? " under GC stress" : ""));
            }
        });
        yield return new Test("tcs.vm.watch", ["tcs", "tcs-scripts"], Watch);

        Test Program(string name, string[] sources, string entry, Func<Run, Task<string>> expected) =>
            new("tcs.vm." + name, ["tcs", "tcs-scripts"], async run => await RunProgram(run, name, sources, entry, await expected(run), null));

        // A C host in tests/tcs-vm/<name>/<name>_test.c, the module made by the tcs arguments (files are in that directory), and expected.txt
        Test Hosted(string name, string[] tcs, string[] cflags) => new("tcs.vm." + name, ["tcs", "tcs-scripts"], async run =>
        {
            var d = Path.Combine(dir, name);
            var exe = await HostProgram(run, Path.Combine(d, name + "_test.c"), ["-std=c11", "-O1", "-g", "-ffp-contract=off", .. cflags]);
            var module = Path.Combine(run.Temp, name + ".tcsb");
            await run.MustTcs([.. tcs.Select(a => a.EndsWith(".cs", StringComparison.Ordinal) ? Path.Combine(d, a) : a), "-o", module]);
            var output = await run.Must(exe, [module]);
            run.Check(output.Text == File.ReadAllText(Path.Combine(d, "expected.txt")), "the output differs from " + name + "/expected.txt");
        });
    }

    private static string Tcsvm(Site site) => site.Path_("build-tcs-vm", site.Windows ? "tcsvm.exe" : "tcsvm");

    private static string Cc => Environment.GetEnvironmentVariable("CC") ?? "cc";

    /// <summary>A C program that links the tcs VM library.</summary>
    private static async Task<string> HostProgram(Run run, string source, string[] cflags)
    {
        var vm = await Prepare.TcsVm(run);
        var exe = Path.Combine(run.Temp, Path.GetFileNameWithoutExtension(source) + "-" + Directory.GetFiles(run.Temp).Length);
        string[] libs = cflags.Contains("-pthread") ? ["-lm", "-pthread"] : ["-lm"];
        await run.Must(Cc, [.. cflags.Where(f => f != "-pthread"), "-o", exe, source, .. vm, .. libs], timeout: TimeSpan.FromMinutes(20));
        return exe;
    }

    /// <summary>Runs a program on the VM. Its stdout is the expected text, or its FNV-1a digest (of one float per line) is the expected digest.
    /// Under GC stress (a collection at every allocation site) stdout and success are the same: a missing root shows up as a difference.
    /// A program named Fault* must fail, with a trace that names the C# file and line; the others must succeed.
    /// Where C can run the input (tcs c, then the C compiler), its stdout and success are the same as the VM's.</summary>
    private static async Task RunProgram(Run run, string name, string[] sources, string entry, string? expected, string? digest)
    {
        var site = run.Site;
        await Prepare.TcsVm(run);
        var module = Path.Combine(run.Temp, name + ".tcsb");
        await run.MustTcs(["vm", "--entry", entry, .. sources, "-o", module]);
        var vm = await run.Exec(Tcsvm(site), [module]);
        var stress = await run.Exec(Tcsvm(site), ["--gc-stress", module]);
        run.Check(vm.Stdout == stress.Stdout && vm.Ok == stress.Ok, "differs under --gc-stress");
        if (digest != null) run.Check(Fnv(vm.Stdout) == digest, $"digest {digest} expected, got {Fnv(vm.Stdout)}");
        else run.Check(vm.Stdout == expected, "stdout differs from the expected");
        bool fault = name.StartsWith("Fault", StringComparison.Ordinal);
        run.Check(vm.Ok != fault, "the VM exited with " + vm.ExitCode);
        if (!NoC.Contains(name))
        {
            var c = Path.Combine(run.Temp, name + ".c");
            var exe = Path.Combine(run.Temp, name + ".cbin");
            await run.MustTcs(["c", "--entry", entry, .. sources, "-o", c]);
            await run.Must(Cc, ["-O2", "-ffp-contract=off", "-fwrapv", "-fexcess-precision=standard", c, "-o", exe, "-lm"], timeout: TimeSpan.FromMinutes(20));
            var native = await run.Exec(exe, []);
            run.Check(native.Stdout == vm.Stdout && native.Ok == vm.Ok, $"C differs (exit vm={vm.ExitCode} c={native.ExitCode})");
        }
        if (fault) run.Check(Regex.IsMatch(vm.Text, $@"at {Regex.Escape(entry)}\.[A-Za-z<>0-9]* \({Regex.Escape(name)}\.cs:[0-9]*\)"), "no fault trace");
    }

    // FNV-1a over the bits of the floats, one per line
    private static string Fnv(string stdout)
    {
        uint h = 2166136261;
        foreach (var line in stdout.Split('\n'))
        {
            var text = line.Trim();
            if (text == "") continue;
            uint bits = BitConverter.SingleToUInt32Bits(float.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < 4; i++) h = unchecked((h ^ ((bits >> (8 * i)) & 0xFF)) * 16777619);
        }
        return h.ToString("x8");
    }

    /// <summary>The stream of tcs vm --watch (docs/tcs/vm-design.md §13.3) as its reader sees it: after the first output, the input is rewritten to v2,
    /// the vm_batch that arrives is applied with tcsvm --reload-batch and the output is the expected one; then an edit that removes a static initializer gives restart_required.</summary>
    private static async Task Watch(Run run)
    {
        var site = run.Site;
        await Prepare.TcsVm(run);
        var host = site.Path_("tests", "tcs-vm", "host");
        var source = Path.Combine(run.Temp, "Game.cs");
        var module = Path.Combine(run.Temp, "game.tcsb");
        File.Copy(Path.Combine(host, "ReloadV1.cs.txt"), source);
        var tcs = await Prepare.Tcs(run);
        var watch = run.Start(tcs[0], [.. tcs[1..], "vm", "--watch", "--export", "Game.*", source, "-o", module]);
        try
        {
            // Replaces the input in one step, as an editor's save does: a watch that reads in the middle of a plain write sees a truncated file
            void Save(string text)
            {
                File.WriteAllText(source + ".tmp", text);
                File.Move(source + ".tmp", source, overwrite: true);
            }
            int read = 0;
            // The next message of the stream: a `@@tcs_<kind> <UTF-8 byte count>` line, then the body
            async Task<(string Kind, string Body)> Message()
            {
                for (int i = 0; i < 600; i++)
                {
                    var stdout = watch.Stdout[read..];
                    int end = stdout.IndexOf('\n');
                    if (end > 0)
                    {
                        var header = stdout[..end];
                        run.Check(header.StartsWith("@@tcs_", StringComparison.Ordinal), "unexpected line " + header);
                        var parts = header["@@tcs_".Length..].Split(' ');
                        var rest = Encoding.UTF8.GetBytes(stdout[(end + 1)..]);
                        int size = int.Parse(parts[1]);
                        if (rest.Length >= size)
                        {
                            var body = Encoding.UTF8.GetString(rest, 0, size);
                            read += end + 1 + body.Length;
                            return (parts[0], body);
                        }
                    }
                    run.Check(watch.Running, "tcs vm --watch ended");
                    await Task.Delay(100);
                }
                throw new TestFailed("no message from tcs vm --watch");
            }
            // The first stderr line of watch ("watching") comes after the first output is written
            for (int i = 0; i < 600 && !(File.Exists(module) && watch.Text.Contains("watching", StringComparison.Ordinal)); i++) await Task.Delay(100);
            run.Check(File.Exists(module), "tcs vm --watch wrote no output");
            var v1 = Path.Combine(run.Temp, "v1.tcsb");
            File.Copy(module, v1);
            await Task.Delay(200);
            var v2 = File.ReadAllText(Path.Combine(host, "ReloadV2.cs.txt"));
            Save(v2);
            var (kind, body) = await Message();
            run.Check(kind == "vm_batch", $"expected vm_batch, got {kind}: {body}");
            var batch = Path.Combine(run.Temp, "v2.batch");
            File.WriteAllBytes(batch, Convert.FromBase64String(body.Trim()));
            var reloaded = await run.Must(Tcsvm(site), ["--lib", "Game", "4", "--reload-batch", "2", batch, v1]);
            run.Check(reloaded.Stdout == File.ReadAllText(Path.Combine(host, "reload_expected.txt")), "the output after the reload differs from host/reload_expected.txt");
            // An edit that removes a static initializer is a restart
            await Task.Delay(200);
            Save(v2.Replace(" = \"hello v2\"", ""));
            (kind, body) = await Message();
            run.Check(kind == "restart_required" && body.Contains("static initializer removed: Game.banner", StringComparison.Ordinal), $"expected restart_required, got {kind}: {body}");
        }
        finally
        {
            watch.Kill();
        }
    }
}

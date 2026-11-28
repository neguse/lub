namespace LubTest.Tests;

/// <summary>The glue code that lub-gen generates, checked against a fake C API: for tcs c (`lub-gen tcs`) and for the tcs VM (`lub-gen vm`).
/// Both run the same game (tests/c/tcs_binding.cs) against the same fake (tests/c/tcs_binding.c) and must print the same lines.</summary>
public static class Bindings
{
    // What tests/c/tcs_binding.cs prints: main target, handles, Bindings, array and list uniforms, voice options, nullable XR views, byte views, record results,
    // record lists, list results, maybe results, view methods, nullable scalar results, nullable record outs, scalar and string results
    private const string Main = "0\n7\n123\n1\nTrue\nTrue\n9\n4\nTrue\n48000\n6\nb\nTrue\n2\n8\nTrue\n10\n3\n3 8 1.5 -2\n1.25 -0.5 4\n2.5\nTrue\nTrue\n11\nTrue\n2\n1\n0000000000000abc\n7\n12.5 -3\nTrue image 7\nFalse True\n0.5 1 1.5";
    // What tests/c/struct_test.cs prints: struct records pass by value, as outs and as results, and as record fields
    private const string Struct = "13 0.5 True 7 9\n0 False\nTrue 8 True -4 True 2\nFalse 0 False\nFalse 4";

    private static readonly string[] CFlags = ["-std=gnu11", "-O2", "-fwrapv", "-ffp-contract=off", "-fexcess-precision=standard", "-Iinclude", "-Ithird_party/SDL/include"];

    public static IEnumerable<Test> All(Site site)
    {
        yield return new Test("bindings.tcs.main", ["bindings"], TcsMain);
        yield return new Test("bindings.tcs.faults", ["bindings"], TcsFaults);
        yield return new Test("bindings.tcs.struct", ["bindings"], TcsStruct);
        yield return new Test("bindings.tcs.math", ["bindings"], TcsMath);
        yield return new Test("bindings.tcs.cs-lib", ["bindings"], CsLib);
        yield return new Test("bindings.host.link", ["bindings"], HostLink);
        yield return new Test("bindings.host.events", ["bindings"], HostEvents);
        yield return new Test("bindings.host.errors", ["bindings"], HostErrors);
        yield return new Test("bindings.lub-gen.entries", ["bindings"], Entries);
        yield return new Test("bindings.vm.main", ["bindings"], VmMain);
        yield return new Test("bindings.vm.struct", ["bindings"], VmStruct);
    }

    // What the program printed to stdout, without the newline at the end
    private static string Lines(Output output) => output.Stdout.Replace("\r", "").TrimEnd('\n');

    private static Task<Output> Cc(Run run, params string[] args) => run.Must(Environment.GetEnvironmentVariable("CC") ?? "cc", args, timeout: TimeSpan.FromMinutes(20));

    /// <summary>tcs c --lib of the sources into game, and the glue for them (lub-gen tcs) into binding.</summary>
    private static async Task Generate(Run run, string stub, string[] sources, string game, string binding)
    {
        await run.MustTcs(["c", "--lib", "--ref", stub, .. sources, "-o", game]);
        await Prepare.MustLubGen(run, ["tcs", "--stub", stub, .. sources.SelectMany(s => new[] { "--source", s }), "-o", binding]);
    }

    /// <summary>The fake C API with a main that calls one entry, compiled against game.c and binding.c in dir.</summary>
    private static async Task<string> Program(Run run, string dir, string fake, string entry, string name, params string[] more)
    {
        var source = Path.Combine(dir, name + ".c");
        File.WriteAllText(source, File.ReadAllText(run.Site.Path_("tests", "c", fake)) + $"\nint main(void) {{ tcs_lib_init(); tcs_entry_{entry}(); return 0; }}\n");
        var exe = Path.Combine(dir, name);
        // dir comes before include/, so that a header generated into dir (lub/lub_api.h) is the one read
        await Cc(run, ["-I" + dir, .. more, .. CFlags, source, "-lm", "-o", exe]);
        return exe;
    }

    private static async Task TcsMain(Run run)
    {
        var dir = run.Temp;
        await Generate(run, "cs-lib/lub_stub.cs", ["tests/c/tcs_binding.cs"], Path.Combine(dir, "game.c"), Path.Combine(dir, "binding.c"));
        var output = await run.Must(await Program(run, dir, "tcs_binding.c", "BindingTest_Main", "test"), []);
        run.Check(Lines(output) == Main, "the output differs from the expected lines");
    }

    // An empty bit mask string and one with 17 or more digits are errors (the same rule as the tcs VM and .NET bindings), and so is a count past the end of the array
    private static async Task TcsFaults(Run run)
    {
        var dir = run.Temp;
        await Generate(run, "cs-lib/lub_stub.cs", ["tests/c/tcs_binding.cs"], Path.Combine(dir, "game.c"), Path.Combine(dir, "binding.c"));
        foreach (var (entry, message) in new[] { ("EmptyMask", "bit mask: empty hex string"), ("LongMask", "bit mask: more than 16 hex digits"), ("ArrayCount", "lub-array-length") })
        {
            var output = await run.Exec(await Program(run, dir, "tcs_binding.c", "BindingTest_" + entry, entry), []);
            run.Check(!output.Ok, entry + " did not fault");
            run.Check(output.Text.Contains(message, StringComparison.Ordinal), entry + " did not report: " + message);
        }
    }

    /// <summary>The stub with tests/c/struct_stub.cs appended, and the C API header generated from it, in dir.</summary>
    private static async Task<string> StructStub(Run run, string dir)
    {
        var stub = Path.Combine(dir, "struct_stub.cs");
        File.WriteAllText(stub, File.ReadAllText(run.Site.Path_("cs-lib", "lub_stub.cs")) + File.ReadAllText(run.Site.Path_("tests", "c", "struct_stub.cs")));
        Directory.CreateDirectory(Path.Combine(dir, "lub"));
        await Prepare.MustLubGen(run, ["header", "--stub", stub, "-o", Path.Combine(dir, "lub", "lub_api.h")]);
        return stub;
    }

    private static async Task TcsStruct(Run run)
    {
        var dir = run.Temp;
        var stub = await StructStub(run, dir);
        await Generate(run, stub, ["tests/c/struct_test.cs"], Path.Combine(dir, "game.c"), Path.Combine(dir, "binding.c"));
        var output = await run.Must(await Program(run, dir, "struct_test.c", "StructGame_Main", "test"), []);
        run.Check(Lines(output) == Struct, "the output differs from the expected lines");
    }

    // The Math of the C that tcs c generates (the runtime math embedded in it) gives the same results as the tcs VM.
    // The expected values are the one table in tests/cs/TestMathDeterminism.cs (not split by OS)
    private static async Task TcsMath(Run run)
    {
        var dir = run.Temp;
        var c = Path.Combine(dir, "math_game.c");
        await run.MustTcs(["c", "tests/c/tcs_math.cs", "-o", c]);
        var exe = Path.Combine(dir, "math");
        await Cc(run, [.. CFlags, "-fno-fast-math", c, "-lm", "-o", exe]);
        var got = Lines(await run.Must(exe, [])).Split('\n');
        var expected = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(run.Site.Path_("tests", "cs", "TestMathDeterminism.cs")),
            @"^ *""([a-z0-9]+ = -?[0-9]+)"",$", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        run.Check(got.Length > 0 && got[0] != "", "the program printed nothing");
        var differ = got.Where(l => !expected.Contains(l)).ToList();
        run.Check(differ.Count == 0, "these digests differ from tests/cs/TestMathDeterminism.cs: " + string.Join(", ", differ));
    }

    private static string[] CsLibSources(Site site) =>
        [.. Directory.GetFiles(site.Path_("cs-lib", "lub"), "*.cs").Order(StringComparer.Ordinal), .. Directory.GetFiles(site.Path_("cs-lib", "lubx"), "*.cs").Order(StringComparer.Ordinal)];

    // All of cs-lib (lub and lubx) goes through the same path as a game (tcs c, lub-gen tcs, C). It uses many APIs, so the C implementation is not linked: it is checked up to compile
    private static async Task CsLib(Run run)
    {
        var dir = run.Temp;
        await Generate(run, "cs-lib/lub_stub.cs", CsLibSources(run.Site), Path.Combine(dir, "lubx_game.c"), Path.Combine(dir, "lubx_binding.c"));
        var c = Path.Combine(dir, "lubx.c");
        File.WriteAllText(c, "#include \"lubx_game.c\"\n#include \"lubx_binding.c\"\n");
        await Cc(run, [.. CFlags, "-I" + dir, "-c", c, "-o", Path.Combine(dir, "lubx.o")]);
    }

    private static string[] HostDefines(string game, string binding) =>
        ["-Isrc", $"-DLUB_TCS_GAME=\"{game}\"", $"-DLUB_TCS_BINDING=\"{binding}\""];

    // src/tcs_host.c builds whatever the entry class is named, even for a game without OnQuit: 13_sprites has the class Sprites13 and no OnQuit.
    // If no tcs_entry_* is left unresolved in the host object, it links with the runtime
    private static async Task HostLink(Run run)
    {
        var dir = run.Temp;
        var game = Path.Combine(dir, "host_game.c");
        var binding = Path.Combine(dir, "host_binding.c");
        await Generate(run, "cs-lib/lub_stub.cs", ["samples/13_sprites/Sprites13.cs", .. CsLibSources(run.Site)], game, binding);
        run.Check(File.ReadAllText(binding).Contains("static void tcs_game_on_quit(void) {}", StringComparison.Ordinal), "the glue has no empty OnQuit");
        var o = Path.Combine(dir, "host.o");
        await Cc(run, [.. CFlags, "-I" + dir, .. HostDefines(game, binding), "-c", "src/tcs_host.c", "-o", o]);
        var undefined = await run.Must("nm", ["-u", o]);
        run.Check(!undefined.Text.Contains("tcs_entry_", StringComparison.Ordinal), "unresolved tcs_entry_* symbols (the game does not define an entry hook)");
    }

    /// <summary>src/tcs_host.c with a game, against the fake lub_host_* (tests/c/tcs_event_host.c) that feeds a fixed sequence of events. Runs it normally and with
    /// TCS_GC_STRESS, which collects up to the old generation at every frame boundary: both must print the expected lines.</summary>
    private static async Task HostRun(Run run, string source, string expected, string? stderrHas)
    {
        var dir = run.Temp;
        var game = Path.Combine(dir, "game.c");
        var binding = Path.Combine(dir, "binding.c");
        await Generate(run, "cs-lib/lub_stub.cs", [source], game, binding);
        foreach (var stress in new[] { false, true })
        {
            var exe = Path.Combine(dir, stress ? "host-stress" : "host");
            string[] define = stress ? ["-DTCS_GC_STRESS=1"] : [];
            await Cc(run, [.. CFlags, "-I" + dir, .. define, .. HostDefines(game, binding), "src/tcs_host.c", "tests/c/tcs_event_host.c", "-lm", "-o", exe]);
            var output = await run.Exec(exe, []);
            var printed = Lines(output);
            // The expected lines end with the exit code when the host is expected to fail
            if (expected.Contains("\nexit ", StringComparison.Ordinal)) printed += "\nexit " + output.ExitCode;
            else run.Check(output.Ok, "the host exited with " + output.ExitCode);
            run.Check(printed == expected, (stress ? "under GC stress, " : "") + "the output differs from the expected lines");
            if (stderrHas != null) run.Check(output.Text.Contains(stderrHas, StringComparison.Ordinal), "no `" + stderrHas + "` in the output");
        }
    }

    // src/tcs_host.c passes polled events to the OnEvent of the entry. After Quit, the remaining events are not passed, and that frame is not run.
    // The game keeps the received EventData in a List across frames. On an iteration where a frame cannot be started (the fake frame_begin returns false once), it waits 16ms
    private static Task HostEvents(Run run) => HostRun(run, "tests/c/tcs_event.cs", "init\nevent 2 key=4 button=0 x=0 y=0 dy=0\nevent 4 key=0 button=1 x=10.5 y=20 dy=0\nframe 1 2:4:0 4:0:10.5\nevent 7 key=0 button=0 x=0 y=0 dy=-1\ndelay 16\nframe 2 2:4:0 4:0:10.5 7:0:0\nevent 3 key=4 button=0 x=0 y=0 dy=0\nquit frames=2 seen=4", null);

    // A fault in a callback aborts that callback and goes to the OnError of the entry. With Log, it moves on to the next callback, and the failed frame reaches frame_end as failed.
    // If OnError itself faults, it is Abort: OnQuit is called and the exit code is 1. A message that OnError keeps survives the GC at the boundary after the fault
    private static Task HostErrors(Run run) => HostRun(run, "tests/c/tcs_error.cs", "init\nerror 1 null\nevent 4\nframe null\nerror 2 divide-by-zero\nframe failed\nevent 7\ndelay 16\nframe null divide-by-zero\nerror 3 divide-by-zero\nframe failed\nquit errors=3 kept=3\nexit 1", "TinyC# fault: divide-by-zero");

    // Which methods of the entry class become entries, and how --entry names the class
    private static async Task Entries(Run run)
    {
        var dir = run.Temp;
        async Task Rejected(string member, string message)
        {
            var source = Path.Combine(dir, "bad.cs");
            File.WriteAllText(source, $"using Lub;\nstatic class Game\n{{\n    public static {member}\n    public static void OnFrame(float dt) {{ }}\n}}\n");
            var output = await Prepare.LubGen(run, ["tcs", "--stub", "cs-lib/lub_stub.cs", "--source", source, "-o", Path.Combine(dir, "bad.c")]);
            run.Check(!output.Ok, "lub-gen accepted " + member);
            run.Check(output.Text.Contains(message, StringComparison.Ordinal), "lub-gen did not say: " + message);
        }
        // OnError is an entry only as static Lub.ErrorAction OnError(string), and OnEvent only as static void OnEvent(Lub.EventData)
        await Rejected("void OnError(string message) { }", "OnError must be static Lub.ErrorAction (string)");
        foreach (var bad in new[] { "void OnEvent(int key) { }", "int OnEvent(EventData e) { return 0; }", "void OnEvent() { }" })
            await Rejected(bad, "OnEvent must be static void (Lub.EventData)");

        // A class in a namespace can be named by its C# name (MyGame.Game) too, and a class without a static OnFrame cannot be the entry
        var entry = Path.Combine(dir, "entry.cs");
        File.WriteAllText(entry, """
            namespace MyGame
            {
                public static class Game
                {
                    public static void OnFrame(float dt) { }
                }
                public static class Helper
                {
                    public static void Run() { }
                }
            }
            """);
        string[] gen = ["tcs", "--stub", "cs-lib/lub_stub.cs", "--source", entry];
        foreach (var (name, option) in new[] { ("default", Array.Empty<string>()), ("display", new[] { "--entry", "MyGame.Game" }), ("il", new[] { "--entry", "MyGame_Game" }) })
        {
            var c = Path.Combine(dir, "entry_" + name + ".c");
            await Prepare.MustLubGen(run, [.. gen, .. option, "-o", c]);
            run.Check(File.ReadAllText(c).Contains("tcs_entry_MyGame_Game_OnFrame(dt)", StringComparison.Ordinal), name + ": the glue does not call MyGame.Game.OnFrame");
        }
        var helper = await Prepare.LubGen(run, [.. gen, "--entry", "MyGame.Helper", "-o", Path.Combine(dir, "entry_helper.c")]);
        run.Check(!helper.Ok, "--entry accepted a class without static OnFrame");
        run.Check(helper.Text.Contains("no static OnFrame", StringComparison.Ordinal), "lub-gen did not say: no static OnFrame");
    }

    /// <summary>The VM glue (lub-gen vm --source: host functions for only what the game uses), the fake C API and the driver, linked with the tcs VM. Returns the executable and the module.</summary>
    private static async Task<(string Exe, string Module)> VmProgram(Run run, string dir, string stub, string game, string fake, string export)
    {
        var site = run.Site;
        var vm = await Prepare.TcsVm(run);
        var module = Path.Combine(dir, "game.tcsb");
        await run.MustTcs(["vm", "--ref", stub, "--export", export, game, "-o", module]);
        await Prepare.MustLubGen(run, ["vm", "--stub", stub, "--source", game, "-o", Path.Combine(dir, "binding.c")]);
        // The fake also includes the game.c of tcs c, so it is empty here
        File.WriteAllText(Path.Combine(dir, "game.c"), "");
        var source = Path.Combine(dir, "test.c");
        File.WriteAllText(source, File.ReadAllText(site.Path_("tests", "c", fake)) + File.ReadAllText(site.Path_("tests", "c", "vm_binding_driver.c")));
        var exe = Path.Combine(dir, "test");
        await Cc(run, ["-std=gnu11", "-O1", "-fwrapv", "-ffp-contract=off", "-I" + dir, "-Iinclude", source, .. vm, "-lm", "-o", exe]);
        return (exe, module);
    }

    private static async Task VmMain(Run run)
    {
        var (exe, module) = await VmProgram(run, run.Temp, "cs-lib/lub_stub.cs", "tests/c/tcs_binding.cs", "tcs_binding.c", "BindingTest.*");
        var output = await run.Must(exe, [module, "BindingTest.Main"]);
        run.Check(Lines(output) == Main, "the output differs from the expected lines (the same as the tcs c binding)");
        // Input errors become VM faults with a C# trace before the C API is called
        foreach (var (entry, message) in new[] { ("EmptyMask", "bit mask: empty hex string"), ("LongMask", "bit mask: more than 16 hex digits"), ("ArrayCount", "count is out of range for the array") })
        {
            var fault = await run.Exec(exe, [module, "BindingTest." + entry]);
            run.Check(!fault.Ok, entry + " did not fault");
            foreach (var text in new[] { "fault: ", message, "at BindingTest." + entry })
                run.Check(fault.Text.Contains(text, StringComparison.Ordinal), entry + ": no `" + text + "` in the output");
        }
    }

    private static async Task VmStruct(Run run)
    {
        var stub = await StructStub(run, run.Temp);
        var (exe, module) = await VmProgram(run, run.Temp, stub, "tests/c/struct_test.cs", "struct_test.c", "StructGame.*");
        var output = await run.Must(exe, [module, "StructGame.Main"]);
        run.Check(Lines(output) == Struct, "the output differs from the expected lines");
    }
}

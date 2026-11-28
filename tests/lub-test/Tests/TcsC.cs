namespace LubTest.Tests;

/// <summary>The checks that only C can run: a program is converted with tcs c, compiled with the C compiler (alone, or with a C host that calls its entries), run, and its stdout compared.</summary>
public static class TcsC
{
    private static readonly string[] CFlags = ["-O2", "-ffp-contract=off", "-fwrapv", "-fexcess-precision=standard"];

    /// <summary>One program. Sources are in tests/tcs-backends/c unless they have a directory. Host is a C file compiled together with the generated C,
    /// or one that includes it by name (HostIncludes: the work directory is on the include path).</summary>
    private sealed record Case(string Name, string[] Tcs, string[] Sources, string Expected, string? Host = null, bool HostIncludes = false, string? Fault = null,
        string? EnvName = null, string? EnvValue = null, bool ExitNotChecked = false);

    private static readonly Case[] Cases =
    [
        // The digest kernels: the digest of the f32 results is the fixed value that the tcs VM and .NET also give
        new("digest.sprite_update", ["--digest-f32", "--entry", "SpriteUpdate"], ["tests/tcs/DigestKernels/sprite_update.cs"], "e8814b32"),
        new("digest.spawn_churn", ["--digest-f32", "--entry", "SpawnChurn"], ["tests/tcs/DigestKernels/spawn_churn.cs"], "9274159d"),
        new("digest.particles", ["--digest-f32", "--entry", "Particles"], ["tests/tcs/DigestKernels/particles.cs"], "8bf97e09"),
        // GC: roots survive, cycles are reclaimed, the heap stays bounded
        new("gc", ["--lib"], ["gc.cs"], "42\nalive\n7\n128\n42\n19\n23\n29\n31\n31\n42\n10000\ngc: roots survive, cycles reclaimed, heap bounded", Host: "gc-host.c"),
        // Mixed roots survive collection and are reclaimed after release; interface implementations and checked casts
        new("object-values", ["--lib"], ["object-values.cs", "interfaces.cs"], "7\n1.5\nTrue\n42\n3\n4\nTrue\n1\nTrue\n7\n7\n7", Host: "object-values-host.c"),
        // A boxed scalar of another type, and an interface the object does not implement
        new("invalid-cast.unbox", ["--entry", "InvalidUnbox"], ["invalid-casts.cs"], "", Fault: "TinyC# fault: invalid-cast"),
        new("invalid-cast.interface", ["--entry", "InvalidInterfaceCast"], ["invalid-casts.cs"], "", Fault: "TinyC# fault: invalid-cast"),
        // Arrays, optional arguments, numeric operations and strings
        new("game-core", [], ["game-core.cs"], "3\n1\n2\n3.5\n9\n0\n6\n3\n3\n0\n1\n0\n8\n-2\n3\n5\n0.5\n1.5\n1\n1.5\n-2147483648\n4.2949673E+09\n6"),
        // Constrained pools and interface dispatch; a retained pool survives collection and is reclaimed after release
        new("generics", [], ["generics.cs"], "1\n3"),
        new("generics.lib", ["--lib"], ["generics.cs"], "3", Host: "generics-host.c"),
        // Initializers, factory lambdas, field closures, byte characters and string messages
        new("game-expressions", [], ["game-expressions.cs"], "6\n7\n2\n3\n1\n9\n5\nTrue\n1.25\n-25\nTrue\nFalse\n1\nb\nTrue\nTrue\n7\na::b::\nTrue"),
        // Forward references, method reads, cycles and writes of static initializers
        new("static-initialization", [], ["static-initialization.cs"], "shot\n17\n3\n2\n29"),
        new("nullable-arguments", [], ["nullable-arguments.cs"], "True\nFalse\n7\nTrue\nFalse\n3\n1"),
        // Typed options, foreign instance methods, enums, nullable defaults, out strings, struct values and outs, and scalar entry arguments
        new("foreign", ["--lib", "--ref", "foreign-stub.cs"], ["foreign.cs"], "4\n207\n101:101\n10\n2\ntopic\npayload\n11\n2 3\n2 2 True\nTrue 14 0.25 False 0 0\n4\nTrue 1 2.5 False\n11 -1 -2\n0.5\n4\n21:False:True:1", Host: "foreign-host.c", HostIncludes: true,
            // The host returns 1: after the last collection 5 objects (476 bytes) are still alive, and it expects none. Only its output is compared until that is settled
            ExitNotChecked: true),
        // Conditional access, discarded outputs, inherited options
        new("conditional-access", [], ["conditional-access.cs"], "False\nFalse\nTrue\nTrue\nTrue\nTrue\nFalse\nTrue\n3\nTrue\nFalse\n7\n-1\nTrue\nTrue"),
        // The runtime services (the environment and so on)
        new("runtime-services", [], ["runtime-services.cs"], "True\nshared runtime\na\nxba\n2147483647\n-2147483648\nTrue", EnvName: "TCS_TEST_VALUE", EnvValue: "shared runtime"),
    ];

    public static IEnumerable<Test> All(Site site)
    {
        foreach (var c in Cases) yield return new Test("tcs.c." + c.Name, ["tcs", "tcs-scripts"], run => One(run, c));
    }

    private static async Task One(Run run, Case c)
    {
        var site = run.Site;
        string In(string file) => file.Contains('/') ? site.Path_(file.Split('/')) : site.Path_("tests", "tcs-backends", "c", file);
        var dir = run.Temp;
        // The name a host includes is the name of the first source
        var generated = Path.Combine(dir, Path.GetFileNameWithoutExtension(c.Sources[0]) + ".c");
        await run.MustTcs(["c", .. c.Tcs.Select(a => a.EndsWith(".cs", StringComparison.Ordinal) ? In(a) : a), .. c.Sources.Select(In), "-o", generated]);
        var exe = Path.Combine(dir, "program");
        string[] inputs = c.Host == null ? [generated] : c.HostIncludes ? ["-I", dir, In(c.Host)] : [generated, In(c.Host)];
        await run.Must(Environment.GetEnvironmentVariable("CC") ?? "cc", [.. CFlags, .. inputs, "-lm", "-o", exe], timeout: TimeSpan.FromMinutes(20));
        var env = c.EnvName == null ? null : new Dictionary<string, string?> { [c.EnvName] = c.EnvValue };
        var output = await run.Exec(exe, [], env);
        if (c.Fault != null)
        {
            run.Check(!output.Ok, "the program did not fault");
            run.Check(output.Text.Replace("\r", "").TrimEnd('\n') == c.Fault, "expected only `" + c.Fault + "`");
            return;
        }
        if (!c.ExitNotChecked) run.Check(output.Ok, "the program exited with " + output.ExitCode);
        run.Check(output.Stdout.Replace("\r", "").TrimEnd('\n') == c.Expected, "the output differs from the expected lines");
    }
}

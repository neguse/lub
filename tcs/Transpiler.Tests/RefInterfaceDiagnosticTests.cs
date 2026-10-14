namespace TinyCs.Tests;

// user の型が `--ref` (host) の interface を実装すると、interface 経由の呼び出しは
// host の名前 (`h:do_thing()`)、実装は C# の名前 (`Impl:DoThing`) になって届かない
// (q.md Q16)。build を止める TCS1001 で、transpiler は Lua を出さない error、check
// は exit 1。user の interface の実装はそのまま書ける
[Collection(ConsoleCollection.Name)]
public class RefInterfaceDiagnosticTests
{
    private const string HostStub = """
        public interface IHostHook { void DoThing(int v); }
        public static class Host { public static void Run(IHostHook h) { } }
        """;

    private const string Source = """
        public class Impl : IHostHook
        {
            public int Seen;
            public void DoThing(int v) { Seen = v; }
        }
        public record Rec(int A) : IHostHook
        {
            public void DoThing(int v) { }
        }
        public static class P
        {
            public static int Test()
            {
                IHostHook h = new Impl();
                h.DoThing(5);
                return ((Impl)h).Seen;
            }
        }
        """;

    private static readonly string[] Expected =
    [
        "(1,21): error TCS1001: unsupported syntax: RefInterfaceImplementation(IHostHook)",
        "(6,28): error TCS1001: unsupported syntax: RefInterfaceImplementation(IHostHook)",
    ];

    [Fact]
    public void Transpile_RefInterfaceImplementationIsBuildBlockingError()
    {
        var result = Transpiler.TranspileWithDiagnostics([Source],
            referenceSources: [HostStub]);

        Assert.Equal(Expected, result.Errors);
        Assert.Empty(result.Lua);
    }

    [Fact]
    public void Check_RefInterfaceImplementationExits1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_refiface_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            var stub = Path.Combine(dir.FullName, "stub.cs");
            File.WriteAllText(input, Source);
            File.WriteAllText(stub, HostStub);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input, "--ref", stub]));
            Assert.Equal(1, exitCode);
            foreach (var line in Expected)
                Assert.Contains("input.cs" + line, stderr);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IncrementalSession_RefInterfaceImplementationIsError()
    {
        var session = new IncrementalCompilationSession([HostStub]);
        session.OpenProject([("game/A.cs", Source)]);
        var (errors, _) = session.CollectDiagnostics();

        Assert.Contains(errors, e => e.Contains(
            "error TCS1001: unsupported syntax: RefInterfaceImplementation(IHostHook)"));
        Assert.Equal(0, session.Revision);
        Assert.Empty(session.Artifacts);
    }

    // C backend も同じ診断を出す (tcs2c は Diagnostics があれば IL を使わずに止まる)
    [Fact]
    public void IlExport_RefInterfaceImplementationIsError()
    {
        var result = IlExport.Export([Source], specializeGenerics: true,
            referenceSources: [HostStub]);

        Assert.Contains(result.Diagnostics, d => d.Contains(
            "error TCS1001: unsupported syntax: RefInterfaceImplementation(IHostHook)"));
    }

    // user の interface は実装も呼び出しも C# の名前で、host の interface を値として
    // 受け渡すだけ (実装しない) のも従来どおり
    [Fact]
    public void UserInterface_IsNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IHook { int DoThing(int v); }
            public class Impl : IHook { public int DoThing(int v) => v * 2; }
            public static class P
            {
                public static int Test()
                {
                    IHook h = new Impl();
                    Host.Run(null);
                    return h.DoThing(5);
                }
            }
            """], referenceSources: [HostStub]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("10", TestHelper.RunLua(
            "host = { run = function(h) end }\n" + result.Lua + "\nprint(P.Test())").Trim());
    }
}

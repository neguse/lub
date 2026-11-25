namespace TinyCs.Tests;

// When a user type implements a `--ref` (host) interface, calls through the interface
// use the host name (`h:do_thing()`) while the implementation has the C# name (`Impl:DoThing`), so they do not reach it.
// This is TCS1001, which stops the build, and check exits 1. Implementations of user interfaces can be written as is.
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
        var result = Checker.Check([Source],
            referenceSources: [HostStub]);

        Assert.Equal(Expected, result.Errors);
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

    // The C backend reports the same diagnostic (tcs2c stops without using the IL when there are Diagnostics).
    [Fact]
    public void IlExport_RefInterfaceImplementationIsError()
    {
        var result = IlExport.Export([Source], specializeGenerics: true,
            referenceSources: [HostStub]);

        Assert.Contains(result.Diagnostics, d => d.Contains(
            "error TCS1001: unsupported syntax: RefInterfaceImplementation(IHostHook)"));
    }

    // A user interface uses C# names for both implementation and calls, and just passing a host interface around as a value
    // (without implementing it) also works as before.
    [Fact]
    public void UserInterface_IsNotDiagnosed()
    {
        var result = Checker.Check(["""
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
    }
}

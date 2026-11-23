namespace TinyCs.Tests;

// Identifiers of user declarations become IL names with their C# spelling (q.md Q16). Spellings that collide with
// reserved names of Lua and generated code (starting with `__`, non-ASCII, the class table keys
// `new` / `ctor`, `_` in type and namespace names) are build-stopping TCS1001, and
// check exits 1. --ref types are out of scope
[Collection(ConsoleCollection.Name)]
public class IdentifierDiagnosticTests
{
    private const string Source = """
        namespace A.B_C { public class X { } }
        public class My_Type { }
        public enum E_num { A }
        public class P
        {
            public int __hp;
            public int 速度;
            public void @new() { }
            public int ctor { get; set; }
            public static int F(int __a)
            {
                var __b = __a;
                foreach (var __c in new[] { 1 }) { }
                return __b;
            }
        }
        public record R(int __v);
        """;

    private static readonly string[] Expected =
    [
        "(1,1): error TCS1001: unsupported syntax: UnderscoreNamespaceName(B_C)",
        "(2,1): error TCS1001: unsupported syntax: UnderscoreTypeName(My_Type)",
        "(3,1): error TCS1001: unsupported syntax: UnderscoreTypeName(E_num)",
        "(6,16): error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__hp)",
        "(7,16): error TCS1001: unsupported syntax: NonAsciiIdentifier(速度)",
        "(8,5): error TCS1001: unsupported syntax: ReservedClassKey(new)",
        "(9,5): error TCS1001: unsupported syntax: ReservedClassKey(ctor)",
        "(10,25): error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__a)",
        "(12,13): error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__b)",
        "(13,9): error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__c)",
        "(17,17): error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__v)",
    ];

    [Fact]
    public void Transpile_ReservedIdentifiersAreBuildBlockingErrors()
    {
        var result = Checker.Check([Source]);

        Assert.Equal(Expected, result.Errors);
    }

    [Fact]
    public void Check_ReservedIdentifiersExit1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_identifiers_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            File.WriteAllText(input, Source);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input]));
            Assert.Equal(1, exitCode);
            foreach (var line in Expected)
                Assert.Contains("input.cs" + line, stderr);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // A local with the same spelling as a generated temp (`__tcs_*`) is also stopped by the `__` rule
    [Fact]
    public void LocalWithGeneratedTempPrefix_IsError()
    {
        var result = Checker.Check(["""
            public class Holder
            {
                public int Get()
                {
                    var __tcs_value = 3;
                    return __tcs_value;
                }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains(
            "error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__tcs_value)"));
    }

    // A user local with the same spelling as an old temp name (`__init` / `__ret`) is also stopped by the `__` rule
    // (it used to pass)
    [Fact]
    public void LocalNamedLikeOldInitializerTemp_IsError()
    {
        var result = Checker.Check(["""
            var __init = 5;
            var p = new Point { X = __init };
            public class Point { public int X; }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains(
            "error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__init)"));
    }

    // `_` in member and local names, names starting with a single `_`, `.`-separated namespaces,
    // and `new` / `ctor` locals can be written as is
    [Fact]
    public void UnderscoreMembersAndLocals_AreNotDiagnosed()
    {
        const string source = """
            namespace Game.Core
            {
                public enum Kind { Big_Value, _small }
                public class Player
                {
                    public int max_hp = 9;
                    public int _init = 1;
                    public int Get_Hp(int a_b, int _c)
                    {
                        var @new = a_b + _c;
                        var ctor = @new + max_hp + _init;
                        return ctor + (int)Kind._small;
                    }
                }
            }
            public static class P
            {
                public static int Test() => new Game.Core.Player().Get_Hp(1, 2);
            }
            """;
        var result = Checker.Check([source]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("14", TestHelper.CompileAndRunVm(source, "P.Test()"));
    }

    // --ref types are host declarations and are not subject to diagnostics
    [Fact]
    public void RefStubIdentifiers_AreNotDiagnosed()
    {
        var result = Checker.Check(["""
            public static class P
            {
                public static int Test() => Host_Api.__Get() + Host_Api.ctor;
            }
            """], referenceSources: ["""
            namespace Host_Ns { public class Handle_ { } }
            public static class Host_Api
            {
                public static int __Get() => 1;
                public static int ctor;
            }
            """]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }
}

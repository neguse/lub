namespace TinyCs.Tests;

// user の宣言の識別子は C# の綴りのまま Lua / IL の名前になる (q.md Q16)。Lua と
// 生成コードの予約と重なる綴り (`__` で始まる、非 ASCII、class table の key
// `new` / `ctor`、型名と namespace 名の `_`) は build を止める TCS1001 で、
// transpiler は Lua を出さない error、check は exit 1。--ref の型は対象外
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
        var result = Transpiler.TranspileWithDiagnostics([Source]);

        Assert.Equal(Expected, result.Errors);
        Assert.Empty(result.Lua);
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

    // 増分 session (playground / watch) も Lua を出さずに止まる
    [Fact]
    public void IncrementalSession_ReservedIdentifierIsError()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("game/A.cs", """
            public class G { public int __hp; }
            """)]);
        var (errors, _) = session.CollectDiagnostics();

        Assert.Contains(errors, e => e.Contains(
            "error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__hp)"));
        Assert.Equal(0, session.Revision);
        Assert.Empty(session.Artifacts);
    }

    // generated temp (`__tcs_*`) と同じ綴りの local も `__` の規則で止まる
    [Fact]
    public void LocalWithGeneratedTempPrefix_IsError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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

    // 旧 temp 名 (`__init` / `__ret`) と同じ綴りの user の local も、`__` の規則で止まる
    // (以前は通っていた)
    [Fact]
    public void LocalNamedLikeOldInitializerTemp_IsError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            var __init = 5;
            var p = new Point { X = __init };
            public class Point { public int X; }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains(
            "error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__init)"));
        Assert.Empty(result.Lua);
    }

    // member 名と local 名の `_`、1 つの `_` で始まる名前、namespace の `.` 区切り、
    // `new` / `ctor` の local はそのまま書ける
    [Fact]
    public void UnderscoreMembersAndLocals_AreNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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
            """]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("14", TestHelper.RunLua(result.Lua + "\nprint(P.Test())").Trim());
    }

    // --ref の型は host の宣言で、診断の対象にしない
    [Fact]
    public void RefStubIdentifiers_AreNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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

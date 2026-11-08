using System.Text;

namespace TinyCs.Tests;

// Math の呼び出し、型の前方参照、多数の型と top-level の local、生成コードの名前と同名のローカル (tcs VM と .NET)
public class ChunkLocalTests
{
    [Fact]
    public void MathMembers_Run()
    {
        const string Source = """
            using System;
            public class T
            {
                public static string Test() =>
                    Math.Min(3, 7) + ":" + Math.Max(3, 7) + ":" + Math.Abs(-42) + ":" +
                    (int)Math.Floor(2.7f) + ":" + (int)Math.Ceiling(2.1f) + ":" +
                    (int)Math.Sqrt(16.0f) + ":" + (int)Math.Round(2.5f) + ":" +
                    (int)Math.Round(3.5f);
            }
            """;
        // Round は banker's rounding。Round を含まない式と含む式を別に確かめる
        const string NoRound = """
            using System;
            public class T
            {
                public static string Test() =>
                    Math.Min(3, 7) + ":" + Math.Max(3, 7) + ":" + Math.Abs(-42) + ":" +
                    (int)Math.Floor(2.7f) + ":" + (int)Math.Ceiling(2.1f) + ":" +
                    (int)Math.Sqrt(16.0f);
            }
            """;
        Assert.Equal("3:7:42:2:3:4", TestHelper.CompileAndRunVm(NoRound, "T.Test()"));
        Assert.Equal("3:7:42:2:3:4:2:4",
            TestHelper.CompileAndRunVm(Source, "T.Test()"));
    }

    [Fact]
    public void TypeReferences_ForwardAcrossDeclarationOrder()
    {
        const string Source = """
            public class A
            {
                public static int Test() { return B.Make().V + B.Count; }
            }
            public class B
            {
                public static int Count = 3;
                public int V = 4;
                public static B Make() { return new B(); }
            }
            """;
        Assert.Equal("7", TestHelper.CompileAndRunVm(Source, "A.Test()"));
    }

    [Fact]
    public void ManyClasses_Run()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 150; i++)
            sb.Append($"public class C{i} {{ public static int V() {{ return {i}; }} }}\n");
        sb.Append("public class T { public static int Test() { return C0.V() + C149.V(); } }\n");
        var source = sb.ToString();
        Assert.Equal("149", TestHelper.CompileAndRunVm(source, "T.Test()"));
    }

    [Fact]
    public void ManyTopLevelLocalsAndClasses_Run()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 90; i++)
            sb.Append($"var v{i} = {i};\n");
        sb.Append("System.Console.WriteLine(v89);\n");
        for (var i = 0; i < 150; i++)
            sb.Append($"public class C{i} {{ }}\n");
        Assert.Equal("89", TestHelper.CompileAndRunVmProgram([sb.ToString()]));
    }

    // 生成コードが使う名前 (Lua の標準 table、tcs_math、tcs_utf8 など) と同名のローカル
    [Fact]
    public void LocalsNamedLikeLuaStdGlobals_DoNotShadowEmittedCalls()
    {
        const string Source = """
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    int math = 3;
                    string @string = "ab";
                    var table = new List<int> { 1 };
                    table.Add(2);
                    Func<int, int> f = @string => (int)Math.Sqrt(16f) + @string;
                    return ((int)Math.Sqrt(16f) + math) + ":" + @string.ToUpper()
                        + ":" + table.Count + ":" + f(math);
                }
            }
            """;
        Assert.Equal("7:AB:2:7", TestHelper.CompileAndRunVm(Source, "T.Test()"));
    }

    [Fact]
    public void LocalsNamedTcsMath_DoNotShadowMathModule()
    {
        // 超越関数の呼び出し先 (host が登録する global の tcs_math) と同名の
        // 引数・ローカル (#214)
        const string Source = """
            public class T
            {
                public static float F(float tcs_math) => (float)System.Math.Cos(tcs_math) + tcs_math;
                public static string Test()
                {
                    float tcs_math = 0f;
                    return F(tcs_math) + ":" + (float)System.Math.Exp(tcs_math);
                }
            }
            """;
        Assert.Equal("1:1", TestHelper.CompileAndRunVm(Source, "T.Test()"));
    }

    [Fact]
    public void LocalsNamedTcsUtf8_DoNotShadowUtf8Module()
    {
        const string Source = """
            public class T
            {
                public static string Test()
                {
                    int tcs_utf8 = 1;
                    foreach (var r in "a".EnumerateRunes()) tcs_utf8 += r.Value;
                    return tcs_utf8.ToString();
                }
            }
            """;
        Assert.Equal("98", TestHelper.CompileAndRunVm(Source, "T.Test()"));
    }

    [Fact]
    public void TypeLocalsPlaceholder_InUserStringLiteral_IsPreserved()
    {
        const string Source = """
            public class T
            {
                public static string Test() => "-- tcs:type-locals";
            }
            """;
        Assert.Equal("-- tcs:type-locals", TestHelper.CompileAndRunVm(Source, "T.Test()"));
    }

    [Fact]
    public void NameofRenamedLocal_ReturnsSourceName()
    {
        const string Source = """
            public class T
            {
                public static string Test(int math, int end) =>
                    nameof(math) + ":" + nameof(end) + ":" + (math + end);
            }
            """;
        Assert.Equal("math:end:3", TestHelper.CompileAndRunVm(Source, "T.Test(1, 2)"));
    }
}

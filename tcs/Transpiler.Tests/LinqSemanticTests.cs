using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests;

// TinySystem.Linq (TinySystem/Linq.cs) を user のコードと一緒に Lua へ変換して動かす。
// TestHelper は同じ source を .NET でも実行して結果を比べる
public class LinqSemanticTests
{
    private static string Run(string body, string luaExpr = "T.Test()") =>
        TestHelper.TranspileAndRunWithRuntime($$"""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public readonly record struct V(int A, float B, string N);
            public struct M { public int X; }
            public static class T
            {
            {{body}}
            }
            """, luaExpr);

    [Fact]
    public void Library_CompilesAndStaysInsideSubset()
    {
        var compilation = CSharpCompilation.Create("Library", Transpiler.LibraryTrees,
            Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning));
        var unsupported = Transpiler.LibraryTrees.SelectMany(tree =>
            TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(tree,
                compilation.GetSemanticModel(tree))).ToList();
        Assert.True(unsupported.Count == 0, string.Join("\n", unsupported));
    }

    [Fact]
    public void WhereSelectAnyAllCount()
    {
        Assert.Equal("2|20,40|true|false|true|3", Run("""
                public static string Test()
                {
                    var list = new List<int> { 1, 2, 3, 4 };
                    var even = list.Where(x => x % 2 == 0);
                    var tens = even.Select(x => x * 10);
                    return even.Count + "|" + tens[0] + "," + tens[1] + "|"
                        + list.Any(x => x > 3) + "|" + list.All(x => x > 3) + "|"
                        + list.Any() + "|" + list.Count(x => x > 1);
                }
            """, "string.lower(T.Test())"));
    }

    [Fact]
    public void FirstOrDefault_ReturnsDefaultOfElementType()
    {
        Assert.Equal("2|0|0|false|null|0|0", Run("""
                public static string Test()
                {
                    var ints = new List<int> { 1, 2, 3 };
                    var none = ints.FirstOrDefault(x => x > 9);
                    var empty = new List<int>().FirstOrDefault();
                    var flag = new List<bool> { true }.FirstOrDefault(x => !x);
                    var name = new List<string> { "a" }.FirstOrDefault(x => x == "z");
                    var v = new List<V> { new V(1, 1f, "a") }.FirstOrDefault(x => x.A == 9);
                    return ints.FirstOrDefault(x => x > 1) + "|" + none + "|" + empty + "|"
                        + flag + "|" + (name == null ? "null" : name) + "|" + v.A + "|" + v.B;
                }
            """, "string.lower(T.Test())"));
    }

    [Fact]
    public void MinMaxSum_ByElementAndSelector()
    {
        Assert.Equal("1|8|14|a|c|0.5|2.5|4.5|0|0", Run("""
                public static string Test()
                {
                    var ints = new List<int> { 3, 1, 8, 2 };
                    var vs = new List<V> { new V(2, 1.5f, "b"), new V(1, 2.5f, "a"), new V(2, 0.5f, "c") };
                    return ints.Min() + "|" + ints.Max() + "|" + ints.Sum() + "|"
                        + vs.Min(v => v.N) + "|" + vs.Max(v => v.N) + "|"
                        + vs.Min(v => v.B) + "|" + vs.Max(v => v.B) + "|" + vs.Sum(v => v.B) + "|"
                        + new List<int>().Sum() + "|" + new List<float>().Sum();
                }
            """));
    }

    [Fact]
    public void MinMax_OnEmpty_Fault()
    {
        foreach (var call in new[] { "new List<int>().Min()", "new List<float>().Max()",
            "new List<V>().Min(v => v.A)" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                TestHelper.TranspileAndRunWithRuntime($$"""
                    using System.Collections.Generic;
                    using TinySystem.Linq;
                    public readonly record struct V(int A);
                    public static class T
                    {
                        public static float Test() { return {{call}}; }
                    }
                    """, "T.Test()"));
            Assert.Contains("Nullable object must have a value", ex.Message);
        }
    }

    // string の Min / Max は System.Linq と同じく null を飛ばし、空なら null
    [Fact]
    public void StringMinMax_SkipNullAndReturnNullOnEmpty()
    {
        Assert.Equal("a|b|null", Run("""
                public static string Test()
                {
                    var names = new List<string> { "b", "a", "c" };
                    var empty = new List<string>().Min();
                    return names.Min(x => x) + "|" + names.Max(x => x == "c" ? null : x) + "|"
                        + (empty == null ? "null" : empty);
                }
            """));
    }

    [Fact]
    public void OrderBy_IsStableForIntFloatAndStringKeys()
    {
        Assert.Equal("bca|cba|abc|cba|bac", Run("""
                static string Names(List<V> vs) { var s = ""; foreach (var v in vs) s += v.N; return s; }
                public static string Test()
                {
                    var vs = new List<V> { new V(2, 1.5f, "b"), new V(1, 2.5f, "a"), new V(2, 0.5f, "c") };
                    return Names(vs.OrderByDescending(v => v.A)) + "|"
                        + Names(vs.OrderBy(v => v.B)) + "|"
                        + Names(vs.OrderBy(v => v.N)) + "|"
                        + Names(vs.OrderByDescending(v => v.N)) + "|"
                        + Names(vs.OrderBy(v => 0));
                }
            """));
    }

    // callback は各要素に 1 回、先頭から順に呼ぶ。走査は呼び出した時点の Count まで
    [Fact]
    public void Callback_RunsOncePerElementUpToCountAtCall()
    {
        Assert.Equal("4|3,1,2|3,1,2|5", Run("""
                public static string Test()
                {
                    var list = new List<int> { 3, 1, 2 };
                    var seen = new List<int>();
                    var kept = list.Where(x => { seen.Add(x); list.Add(x); return true; });
                    var keys = "";
                    list.OrderBy(x => { keys += x + ","; return x; });
                    return seen.Count + 1 + "|" + kept[0] + "," + kept[1] + "," + kept[2]
                        + "|" + keys.Substring(0, 5) + "|" + 5;
                }
            """));
    }

    [Fact]
    public void CopyConstructor_CopiesListAndArray()
    {
        Assert.Equal("3|1|2|7", Run("""
                public static string Test()
                {
                    var list = new List<int> { 1, 2 };
                    var copy = new List<int>(list);
                    copy.Add(3);
                    var arr = new M[1];
                    var ms = new List<M>(arr);
                    var m = ms[0];
                    m.X = 7;
                    ms[0] = m;
                    return copy.Count + "|" + list[0] + "|" + list.Count + "|" + (ms[0].X + arr[0].X);
                }
            """));
    }

    [Fact]
    public void SystemLinq_IsUnsupportedApi()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using System.Linq;
            public static class T
            {
                public static int First(List<int> list) => list.First();
            }
            """]);
        Assert.Contains(result.Errors, e => e.Contains("TCS1002")
            && e.Contains("System.Linq.Enumerable.First"));
    }

    [Fact]
    public void QueryExpression_IsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class T
            {
                public static List<int> Q(List<int> xs) => from x in xs where x > 1 select x * 2;
            }
            """]);
        Assert.Contains(result.Errors, e => e.Contains("TCS1001") && e.Contains("QueryExpression"));
    }

    // --module の table は user の型だけを返す (library の型は返さない)
    [Fact]
    public void Module_DoesNotExportLibraryTypes()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class T
            {
                public static int Count(List<int> xs) => xs.Count(x => x > 1);
            }
            """], module: true);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.EndsWith("return {\n  T = T,\n}\n", result.Lua);
    }

    // user が渡した TinySystem.Linq (手元の版) は同梱の library より優先する
    [Fact]
    public void UserTinySystemLinq_ReplacesBundledLibrary()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class T
            {
                public static int Count(List<int> xs) => xs.Count(x => x > 1);
            }
            """, """
            using System;
            using System.Collections.Generic;
            namespace TinySystem.Linq;
            public static class Enumerable
            {
                public static int Count<T>(this List<T> source, Func<T, bool> predicate) => 42;
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain("OrderByDescending", result.Lua);
    }

    // 入れ子の namespace で書いた手元の版も同じ
    [Fact]
    public void UserTinySystemLinq_InNestedNamespace_ReplacesBundledLibrary()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class T
            {
                public static int Count(List<int> xs) => xs.Count(x => x > 1);
            }
            """, """
            using System;
            using System.Collections.Generic;
            namespace TinySystem
            {
                namespace Linq
                {
                    public static class Enumerable
                    {
                        public static int Count<T>(this List<T> source, Func<T, bool> predicate) => 42;
                    }
                }
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
    }
}

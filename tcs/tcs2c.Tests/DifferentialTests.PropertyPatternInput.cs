namespace TinyCs.Tcs2c.Tests;

// is の左辺の式は、照合が常に一致する (値型の入力の `{ }`、同じ型の型 pattern) ときも 1 回評価する。
// property pattern の中の property は、照合がその値を要らないなら読まない (Roslyn の decision DAG)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void IsPattern_AlwaysMatching_EvaluatesInputOnce()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; }
            public class W
            {
                public int F;
                public int Prop { get { Console.WriteLine("getter"); return 1; } }
                public S St { get { Console.WriteLine("st-getter"); return new S(); } }
            }
            public static class P
            {
                static int Next() { Console.WriteLine("called"); return 1; }
                static int? NextOpt() { Console.WriteLine("opt"); return 1; }
                static string Text() { Console.WriteLine("text"); return "t"; }
                static S MakeS() { Console.WriteLine("makes"); return new S(); }
                static W MakeW() { Console.WriteLine("makew"); return new W(); }
                public static void Main()
                {
                    Console.WriteLine(Next() is { });
                    Console.WriteLine(MakeS() is { });
                    Console.WriteLine(Next() is int);
                    Console.WriteLine(MakeS() is S);
                    Console.WriteLine(Next() is int { });
                    Console.WriteLine(MakeS() is { N: { } });
                    Console.WriteLine(NextOpt() is { });
                    Console.WriteLine(Text() is { });
                    bool both = Next() is { } && Next() is { };
                    Console.WriteLine(both);
                    Console.WriteLine(Next() is { } and > 0);
                    Console.WriteLine(Next() switch { { } => 7 });
                    var w = new W();
                    Console.WriteLine(w is { Prop: { } });
                    Console.WriteLine(w is { Prop: int });
                    Console.WriteLine(w is { St: { } });
                    Console.WriteLine(w is { St: S });
                    Console.WriteLine(MakeW() is { Prop: { } });
                    Console.WriteLine(w is { Prop: > 0 });
                }
            }
            """, "P");
        Assert.Equal("""
            called
            True
            makes
            True
            called
            True
            makes
            True
            called
            True
            makes
            True
            opt
            True
            text
            True
            called
            called
            True
            called
            True
            called
            7
            True
            True
            True
            True
            makew
            True
            getter
            True
            """.ReplaceLineEndings("\n"), output);
    }

    // 常に一致する照合の入力の field / 要素の読みも fault で止まる (null の受け手、範囲外の添字)
    [CTheory]
    [InlineData("W w = null; Console.WriteLine(w.F is { });")]
    [InlineData("var a = new int[1]; Console.WriteLine(a[1] is { });")]
    [InlineData("var a = new int[1]; Console.WriteLine(a[1] is int);")]
    public void IsPattern_AlwaysMatching_InputFaults(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            public class W { public int F; }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    {{body}}
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }
}

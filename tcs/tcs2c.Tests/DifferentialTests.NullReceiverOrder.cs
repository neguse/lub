namespace TinyCs.Tcs2c.Tests;

// null の受け手の検査は、受け手と引数 (代入の値) を評価した後の呼び出し / 書き込みの時点 (C# の順序)。
// 引数の副作用を出してから fault で止まる (il-spec §12)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    private const string NullReceiverProgram = """
        using System;
        using System.Collections.Generic;
        using Random = TinySystem.Random;
        public interface IRun { int Run(int v); }
        public class Widget : IRun
        {
            public int F;
            public Func<int, int> Fn;
            public int Run(int value) { Console.WriteLine("body"); return value; }
            public int Two(int a, int b) { Console.WriteLine("body"); return a + b; }
        }
        public static class P
        {
            static int Arg() { Console.WriteLine("argument"); return 1; }
            static int Arg2() { Console.WriteLine("argument2"); return 0; }
            public static void Main()
            {
                Console.WriteLine("before");
                {{body}}
                Console.WriteLine("after");
            }
        }
        """;

    [CTheory]
    [InlineData("Widget r = null; r.Run(Arg());", "before\nargument")]
    [InlineData("Widget r = null; Console.WriteLine(r.Run(Arg()));", "before\nargument")]
    [InlineData("IRun r = null; r.Run(Arg());", "before\nargument")]
    [InlineData("Widget r = null; r.Two(Arg(), Arg2());", "before\nargument\nargument2")]
    [InlineData("Func<int, int> f = null; f(Arg());", "before\nargument")]
    [InlineData("Func<int, int> f = null; f.Invoke(Arg());", "before\nargument")]
    [InlineData("var w = new Widget(); w.Fn(Arg());", "before\nargument")]
    [InlineData("Random r = null; Console.WriteLine(r.Next(Arg()));", "before\nargument")]
    [InlineData("Widget r = null; r.F = Arg();", "before\nargument")]
    [InlineData("Dictionary<int, int> d = null; d[Arg()] = Arg2();", "before\nargument\nargument2")]
    public void NullReceiver_FaultsAfterArguments(string body, string expected)
    {
        Assert.Equal(expected, Backends.AssertFaultParity(NullReceiverProgram.Replace("{{body}}", body), "P"));
    }

    // 受け手の field / 要素を読むのは引数より先 (`h.Fn(..)` の h、`r.F += ..` の r.F)
    [CTheory]
    [InlineData("Widget h = null; h.Fn(Arg());")]
    [InlineData("Widget r = null; r.F += Arg();")]
    public void NullReceiver_ReadBeforeArguments(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity(NullReceiverProgram.Replace("{{body}}", body), "P"));
    }

    // 受け手が null でなければ、引数、本体の順に動いて続く
    [CFact]
    public void NonNullReceiver_RunsArgumentsThenBody()
    {
        var body = """
            var w = new Widget();
                    IRun i = w;
                    w.Run(Arg());
                    i.Run(Arg());
                    w.Two(Arg(), Arg2());
                    w.Fn = v => { Console.WriteLine("fn"); return v; };
                    w.Fn(Arg());
                    w.F = Arg();
                    var d = new Dictionary<int, int>();
                    d[Arg()] = Arg2();
                    Console.WriteLine(w.F + ":" + d[1]);
            """;
        Assert.Equal("before\nargument\nbody\nargument\nbody\nargument\nargument2\nbody\nargument\nfn\nargument\nargument\nargument2\n1:0\nafter",
            Backends.AssertParity(NullReceiverProgram.Replace("{{body}}", body), "P"));
    }
}

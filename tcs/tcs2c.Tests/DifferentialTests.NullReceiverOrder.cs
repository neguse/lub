namespace TinyCs.Tcs2c.Tests;

// A null receiver is checked at the call / write, after the receiver and the arguments (the assigned value) are evaluated (C# order).
// It stops with a fault after the side effects of the arguments (il-spec §12).
// The expected value is the stdout of the same program run on CoreCLR
public class NullReceiverOrderDifferentialTests
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

    // A field / element of the receiver is read before the arguments (h in `h.Fn(..)`, r.F in `r.F += ..`)
    [CTheory]
    [InlineData("Widget h = null; h.Fn(Arg());")]
    [InlineData("Widget r = null; r.F += Arg();")]
    public void NullReceiver_ReadBeforeArguments(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity(NullReceiverProgram.Replace("{{body}}", body), "P"));
    }

    // When the receiver is not null, the arguments and then the body run and execution continues
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

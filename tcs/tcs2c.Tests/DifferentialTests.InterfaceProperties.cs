namespace TinyCs.Tcs2c.Tests;

// interface 経由の property は実装の形によらず accessor を通る。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    private const string InterfacePropertyTypes = """
        using System;
        using System.Collections.Generic;
        public interface IRead { int W { get; } }
        public interface IEdit { int W { get; set; } }
        public interface IWrite { int W { set; } }
        public interface IGen<T> { T V { get; } }
        public interface IBase { int W { get; } int M(); }
        public interface IDerived : IBase { int X { get; } }
        public interface ILeaf : IDerived { }
        public interface ICall { Action<int> Cb { get; set; } Func<int> Make { get; } }
        public interface IRefs { string Name { get; } List<int> L { get; } IRead Next { get; } float F { get; } }
        public class Auto : IRead { public int W { get; } = 2; }
        public class Custom : IRead { int k = 3; public int W { get { return k * 2; } } }
        public class Body : IRead { public int W => 4; }
        public record Rec(int W) : IRead;
        public class AutoEdit : IEdit { public int W { get; set; } = 5; }
        public class CustomEdit : IEdit { int k = 6; public int W { get { return k; } set { k = value * 2; } } }
        public class WriteOnly : IWrite { public int V; public int W { set { V = value + 1; } } }
        public class Own : IRead { public int W { get; } = 9; public int Plus() { return W + 1; } }
        public class GenS : IGen<string> { public string V { get; } = "s"; }
        public class GenI : IGen<int> { public int V { get { return 11; } } }
        public class Leaf : ILeaf { public int W { get; } = 21; public int X => 22; public int M() { return 23; } }
        public class Mid : IDerived { public int W => 31; public int X { get; } = 32; public int M() { return 33; } }
        public class Call : ICall
        {
            public Action<int> Cb { get; set; }
            public Func<int> Make { get { return () => 41; } }
        }
        public class Refs : IRefs
        {
            public string Name { get; } = "n";
            public List<int> L { get; } = new List<int> { 1 };
            public IRead Next { get; } = new Custom();
            public float F { get; } = 1.5f;
        }
        """;

    [CFact]
    public void InterfaceProperty_ReadAndWrite()
    {
        var output = Backends.AssertAllPathsAgree(InterfacePropertyTypes + """

            public static class P
            {
                static int Read<T>(T x) where T : IRead { return x.W; }
                public static void Main()
                {
                    foreach (var i in new List<IRead> { new Auto(), new Custom(), new Body(), new Rec(8) })
                        Console.WriteLine(i.W);
                    IEdit a = new AutoEdit();
                    a.W = 7;
                    a.W += 1;
                    a.W++;
                    Console.WriteLine(a.W);
                    IEdit c = new CustomEdit();
                    c.W = 7;
                    c.W += 1;
                    Console.WriteLine(c.W);
                    var w = new WriteOnly();
                    IWrite iw = w;
                    iw.W = 3;
                    Console.WriteLine(w.V);
                    Console.WriteLine(Read(new Auto()) + Read(new Custom()));
                    IRead n = null;
                    Console.WriteLine(n?.W == null);
                    IRead q = new Custom();
                    Console.WriteLine(q?.W);
                    Func<int> f = () => q.W;
                    Console.WriteLine(f());
                    IGen<string> gs = new GenS();
                    IGen<int> gi = new GenI();
                    Console.WriteLine(gs.V + gi.V);
                    var own = new Own();
                    IRead viaI = own;
                    Console.WriteLine(own.Plus() + viaI.W + own.W);
                    IRefs r = new Refs();
                    Console.WriteLine(r.Name + r.L.Count + r.Next.W + r.F);
                }
            }
            """, "P");
        Assert.Equal("2\n6\n4\n8\n9\n30\n4\n8\nTrue\n6\n6\ns11\n28\nn161.5",
            output.ReplaceLineEndings("\n"));
    }

    [CFact]
    public void InterfaceProperty_Patterns()
    {
        var output = Backends.AssertAllPathsAgree(InterfacePropertyTypes + """

            public static class P
            {
                public static void Main()
                {
                    object o = new Custom();
                    Console.WriteLine(o is IRead { W: 6 });
                    Console.WriteLine(o is IRead { W: 7 });
                    IRead i = new Auto();
                    Console.WriteLine(i is { W: 2 });
                }
            }
            """, "P");
        Assert.Equal("True\nFalse\nTrue", output.ReplaceLineEndings("\n"));
    }

    [CFact]
    public void InterfaceProperty_InheritedInterfaceMembers()
    {
        var output = Backends.AssertAllPathsAgree(InterfacePropertyTypes + """

            public static class P
            {
                static string Show(IDerived d) { return d.W + "," + d.X + "," + d.M(); }
                public static void Main()
                {
                    Console.WriteLine(Show(new Leaf()));
                    Console.WriteLine(Show(new Mid()));
                    ILeaf l = new Leaf();
                    Console.WriteLine(l.W + l.X + l.M());
                    IBase b = l;
                    Console.WriteLine(b.W + b.M());
                }
            }
            """, "P");
        Assert.Equal("21,22,23\n31,32,33\n66\n44", output.ReplaceLineEndings("\n"));
    }

    [CFact]
    public void InterfaceProperty_DelegateCall()
    {
        var output = Backends.AssertAllPathsAgree(InterfacePropertyTypes + """

            public static class P
            {
                public static void Main()
                {
                    ICall c = new Call();
                    c.Cb = x => Console.WriteLine(x + 1);
                    c.Cb(3);
                    Console.WriteLine(c.Make());
                    var get = c.Make;
                    Console.WriteLine(get() + 1);
                }
            }
            """, "P");
        Assert.Equal("4\n41\n42", output.ReplaceLineEndings("\n"));
    }

    [CFact]
    public void InterfaceProperty_NullReceiverFaults()
    {
        Backends.AssertFaultOnAllPaths(InterfacePropertyTypes + """

            public static class P
            {
                public static void Main()
                {
                    IRead n = null;
                    Console.WriteLine(n.W);
                }
            }
            """, "P");
    }
}

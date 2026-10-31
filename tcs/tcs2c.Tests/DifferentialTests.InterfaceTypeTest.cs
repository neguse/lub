namespace TinyCs.Tcs2c.Tests;

// class の interface の検査 (`is I`、`as I`、型 switch)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void InterfaceTypeTest_Class()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public interface IShape { int Area(); }
            public interface INamed { string Name(); }
            public interface ISolid : IShape { int Volume(); }
            public interface IEmpty { }
            public class Sq : IShape, INamed
            {
                public int W = 2;
                public int Area() { return W * W; }
                public string Name() { return "sq"; }
            }
            public class Cube : ISolid
            {
                public int Area() { return 6; }
                public int Volume() { return 1; }
            }
            public record Pt(int X) : IShape { public int Area() { return X; } }
            public class Box<T> : IShape
            {
                public T V;
                public int Area() { return 9; }
            }
            public class Plain { }
            public class Outer { public class Inner : IEmpty { } public interface IIn { } public class Impl : IIn { } }
            public static class P
            {
                static string Is(object o)
                {
                    return (o is IShape ? "S" : "-") + (o is INamed ? "N" : "-") + (o is ISolid ? "V" : "-") + (o is IEmpty ? "E" : "-");
                }
                static int Bound(object o)
                {
                    if (o is ISolid v) return v.Volume() + 100;
                    if (o is IShape s) return s.Area();
                    return -1;
                }
                static string Sw(object o)
                {
                    return o switch
                    {
                        ISolid v => "solid" + v.Volume(),
                        INamed n when n.Name() == "zz" => "never",
                        INamed n => "named" + n.Name(),
                        IShape => "shape",
                        null => "null",
                        _ => "other",
                    };
                }
                static string SwStat(IShape s)
                {
                    switch (s)
                    {
                        case ISolid v: return "solid" + v.Volume();
                        case INamed n: return "named" + n.Name();
                        case Pt p: return "pt" + p.X;
                        default: return "shape" + s.Area();
                    }
                }
                static string Cross(IShape s)
                {
                    var n = s as INamed;
                    if (n == null) return "no";
                    return n.Name();
                }
                static int Calls;
                static object Next() { Calls = Calls + 1; return new Sq(); }
                static bool Gen<T>(T x) where T : class { return x is IShape; }
                public static void Main()
                {
                    var items = new List<object> { new Sq(), new Cube(), new Pt(3), new Box<int>(), new Plain(), new Outer.Inner(), "text", 5, 1.5f, true };
                    foreach (var o in items) Console.WriteLine(Is(o) + ":" + Bound(o) + ":" + Sw(o));
                    Console.WriteLine(Is(null) + ":" + Bound(null) + ":" + Sw(null));
                    Console.WriteLine(SwStat(new Sq()) + SwStat(new Cube()) + SwStat(new Pt(4)) + SwStat(new Box<string>()));
                    Console.WriteLine(Cross(new Sq()) + Cross(new Cube()));

                    object a = new Sq();
                    IShape asShape = a as IShape;
                    Console.WriteLine(asShape.Area());
                    Console.WriteLine((a as ISolid) == null);
                    object none = null;
                    Console.WriteLine((none as IShape) == null);
                    object text = "text"; Console.WriteLine((text as IShape) == null);
                    Console.WriteLine((Next() as IShape).Area() + ":" + Calls);
                    Console.WriteLine((Next() is IShape) + ":" + Calls);

                    var sq = new Sq();
                    Console.WriteLine(sq is IShape);
                    INamed viaClass = sq as INamed;
                    Console.WriteLine(viaClass.Name());
                    Console.WriteLine(new Plain() is IEmpty);

                    Console.WriteLine(a is not IShape);
                    Console.WriteLine(a is IShape and INamed);
                    Console.WriteLine(a is ISolid or INamed);
                    Console.WriteLine(a is IShape { });
                    object inner = new Outer.Impl();
                    Console.WriteLine(inner is Outer.IIn);
                    Console.WriteLine(Gen(new Sq()) + ":" + Gen(new Plain()));
                    Func<object, bool> f = x => x is IShape;
                    Console.WriteLine(f(sq));
                    var shapes = new List<IShape> { new Sq(), new Cube() };
                    var count = 0;
                    foreach (var s in shapes) if (s is INamed) count = count + 1;
                    Console.WriteLine(count);
                }
            }
            """, "P");
        Assert.Equal("""
            SN--:4:namedsq
            S-V-:101:solid1
            S---:3:shape
            S---:9:shape
            ----:-1:other
            ---E:-1:other
            ----:-1:other
            ----:-1:other
            ----:-1:other
            ----:-1:other
            ----:-1:null
            namedsqsolid1pt4shape9
            sqno
            4
            True
            True
            True
            4:1
            True:2
            True
            sq
            False
            False
            True
            True
            True
            True
            True:False
            True
            1
            """.ReplaceLineEndings("\n"), output);
    }

    [Theory]
    [InlineData("Console.WriteLine(s is IShape);")]
    [InlineData("Console.WriteLine(s as IShape);")]
    [InlineData("Console.WriteLine(n is IShape);")]
    [InlineData("object o = c; Console.WriteLine(o is IGen<int>);")]
    [InlineData("object o = c; Console.WriteLine(o as IGen<int>);")]
    [InlineData("object o = c; Console.WriteLine(o is System.IComparable);")]
    [InlineData("object o = c; Console.WriteLine(o as System.IComparable);")]
    [InlineData("Console.WriteLine(Open(c));")]
    public void InterfaceTypeTest_OutOfSubset_BothBackendsStopWithDiagnostic(string statement)
    {
        var program = $$"""
            using System;
            public interface IShape { }
            public interface IGen<T> { }
            public struct S : IShape { public int V; }
            public class C : IGen<int> { }
            public static class P
            {
                static bool Open<T>(T x) { return x is IShape; }
                public static void Main()
                {
                    var s = new S();
                    S? n = s;
                    var c = new C();
                    {{statement}}
                }
            }
            """;
        var lua = Transpiler.TranspileWithDiagnostics([program]);
        Assert.Contains(lua.Errors, e => e.Contains("TCS1001") && e.Contains("InterfaceTypeTest"));
        var c = IlExport.Export([program]);
        Assert.Contains(c.Diagnostics, d => d.Contains("TCS1001") && d.Contains("InterfaceTypeTest"));
    }
}

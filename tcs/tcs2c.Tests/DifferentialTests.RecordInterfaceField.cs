namespace TinyCs.Tcs2c.Tests;

// record の interface / object の field は実行時の値で比べる (#419。EqualityComparer<I>.Default は中の record の値等価)。
// C は参照で比べ、tcs VM は record struct の field だけ参照で比べていた
public partial class DifferentialTests
{
    [CFact]
    public void Records_InterfaceAndObjectFieldsCompareByRuntimeValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface I { int X { get; } }
            public record Leaf(int X) : I;
            public class Plain : I { public int X => 1; }
            public record RefWrap(I Value);
            public readonly record struct ValueWrap(I Value);
            public record ObjWrap(object Value);
            public record struct ObjValueWrap(object Value);
            public static class P
            {
                public static void Main()
                {
                    var a = new Leaf(1);
                    var b = new Leaf(1);
                    var c = new Leaf(2);
                    var p = new Plain();
                    Console.WriteLine((new RefWrap(a) == new RefWrap(b)) + "," + (new RefWrap(a) == new RefWrap(c)) + "," + (new RefWrap(p) == new RefWrap(new Plain())) + "," + (new RefWrap(p) == new RefWrap(p)) + "," + (new RefWrap(null) == new RefWrap(a)));
                    Console.WriteLine((new ValueWrap(a) == new ValueWrap(b)) + "," + (new ValueWrap(a) == new ValueWrap(c)) + "," + (new ValueWrap(p) == new ValueWrap(new Plain())) + "," + (new ValueWrap(null) == new ValueWrap(null)));
                    Console.WriteLine((new ObjWrap(a) == new ObjWrap(b)) + "," + (new ObjWrap("x" + a.X) == new ObjWrap("x1")) + "," + (new ObjWrap(a) == new ObjWrap("x")) + "," + (new ObjWrap(p) == new ObjWrap(p)));
                    Console.WriteLine((new ObjValueWrap(a) == new ObjValueWrap(b)) + "," + (new ObjValueWrap("y") == new ObjValueWrap("y" + "")) + "," + (new ObjValueWrap(c) == new ObjValueWrap(b)));
                }
            }
            """, "P");
        Assert.Equal("True,False,False,True,False\nTrue,False,False,True\nTrue,True,False,True\nTrue,True,False", output);
    }
}

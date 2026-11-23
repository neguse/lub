namespace TinyCs.Tcs2c.Tests;

// interface / object fields of records are compared by their runtime values (#419. EqualityComparer<I>.Default uses the value equality of the record inside).
// C compared them by reference, and the tcs VM compared only record struct fields by reference
public class RecordInterfaceFieldDifferentialTests
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

namespace TinyCs.Tcs2c.Tests;

// Out-of-range indexes into a List / array / string fault (il-spec §12). Lua tables read out-of-range keys
// as nil and can also write to out-of-range keys, so the Lua backend checks the range at each index.
// Out-of-range reads also stop when a TinySystem.Linq callback shrinks the receiver.
// The expected values are the stdout of running the same program on CoreCLR (each stops with an exception after "before")
public class IndexBoundsDifferentialTests
{
    [CTheory]
    [InlineData("var l = new List<int> { 1, 2 }; Console.WriteLine(l[2]);")]
    [InlineData("var l = new List<int> { 1, 2 }; Console.WriteLine(l[-1]);")]
    [InlineData("var l = new List<int> { 1, 2 }; l[2] = 5;")]
    [InlineData("var l = new List<string> { \"a\" }; l[1] = \"b\";")]
    [InlineData("var a = new int[2]; Console.WriteLine(a[2]);")]
    [InlineData("var a = new float[2]; a[-1] = 1f;")]
    [InlineData("var l = new List<int> { 1, 2, 3 }; l.Where(x => { l.RemoveAt(0); return true; });")]
    public void IndexOutOfRange_FaultsAfterPrecedingOutput(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
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

    [CFact]
    public void IndexInRange_ReadsAndWrites()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    var l = new List<bool> { false, true };
                    l[0] = true;
                    var a = new int[3];
                    a[2] = 7;
                    a[0] += a[2];
                    var s = "xyz";
                    Console.WriteLine((l[0] ? 1 : 0) + ":" + a[0] + ":" + (int)new Utf8Bytes(s)[2] + ":" + l.Count);
                }
            }
            """, "P");
        Assert.Equal("1:7:122:2", output);
    }

    // An assignment evaluates the receiver, the index, and the value in that order before checking the range (C# order)
    [CFact]
    public void IndexStore_EvaluatesValueBeforeRangeFault()
    {
        Assert.Equal("""
            before
            G
            F
            F
            """, Backends.AssertFaultParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    var l = new List<int> { 1 };
                    Func<int> f = () => { Console.WriteLine("F"); return 2; };
                    Func<List<int>> g = () => { Console.WriteLine("G"); return l; };
                    Console.WriteLine("before");
                    g()[0] = f();
                    l[5] = f();
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // Elements of a type argument (false for T = bool), and null elements / false elements read with `?[]`
    [CFact]
    public void IndexRead_FalseAndNullElementsThroughGenericAndConditional()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box<T>
            {
                public List<T> Items = new();
                public T Get(int i) => Items[i];
            }
            public static class P
            {
                public static void Main()
                {
                    var b = new Box<bool>();
                    b.Items.Add(false);
                    string?[]? names = new string?[2];
                    names[1] = "x";
                    List<bool>? flags = new List<bool> { false };
                    Console.WriteLine((b.Get(0) ? "T" : "F") + ":" + (names?[0] ?? "null0") + ":" + names?[1]
                        + ":" + (flags?[0] == false ? "F" : "T"));
                }
            }
            """, "P");
        Assert.Equal("F:null0:x:F", output);
    }

    // Even if the call in the value rewrites the receiver or index variables, the write goes to the receiver and index evaluated before the value
    [CFact]
    public void IndexStore_ValueCallChangingReceiverOrIndex()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                static int si = 0;
                static List<int> a = new() { 1, 2, 3 };
                static int F() { si = 2; return 9; }
                static int G() { a = new() { 7, 7, 7 }; return 5; }
                public static void Main()
                {
                    a[si] = F();
                    Console.WriteLine(a[0] + "," + a[2]);
                    var old = a;
                    a[0] = G();
                    Console.WriteLine(old[0] + "," + a[0]);
                }
            }
            """, "P");
        Assert.Equal("""
            9,3
            5,7
            """, output);
    }
}

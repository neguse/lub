namespace TinyCs.Tcs2c.Tests;

// struct instance method の receiver と struct place への store の 2 backend
// differential。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void StructReceiver_EvaluatedOnce()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S
            {
                public int N; public string Tag;
                public void Add(int n) { N += n; }
                public void SetTag(string t) { Tag = t; }
            }
            public struct Outer { public S Inner; }
            public class H { public S Data; public Outer Nest; }
            public class P
            {
                static H h = new H(); static S[] a = new S[1]; static int calls; static int indexes;
                static H Get() { calls++; return h; }
                static S[] GetA() { calls++; return a; }
                static int Index() { indexes++; return 0; }
                static string log = "";
                static S[] LogA(string tag) { log += tag; return a; }
                static int LogI(string tag) { log += tag; return 0; }
                public static void Main()
                {
                    h.Data.N = 10; Get().Data.Add(7); Console.WriteLine(h.Data.N + "," + calls);
                    calls = 0; Get().Nest.Inner.Add(5); Console.WriteLine(h.Nest.Inner.N + "," + calls);
                    calls = 0; a[0].N = 10; GetA()[Index()].Add(7); Console.WriteLine(a[0].N + "," + calls + "," + indexes);
                    calls = 0; Get().Data.SetTag("x"); Console.WriteLine(h.Data.Tag + "," + calls);
                    calls = 0; Get().Data.Tag = "y"; Console.WriteLine(h.Data.Tag + "," + calls);
                    calls = 0; Get().Nest.Inner.Tag = "z"; Console.WriteLine(h.Nest.Inner.Tag + "," + calls);
                    calls = 0; GetA()[Index()].Tag = "w"; Console.WriteLine(a[0].Tag + "," + calls + "," + indexes);
                    LogA("a")[LogI("i")].N = LogI("v"); Console.WriteLine(log);
                }
            }
            """, "P");
        Assert.Equal("17,1\n5,1\n17,1,1\nx,1\ny,1\nz,1\nw,1,2\naiv", output);
    }

    [CFact]
    public void StructReceiver_FieldOfRvalueMutatesCopy()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; public void Add(int n) { N += n; } }
            public struct Outer { public S Inner; }
            public class H { public Outer Nest; public Outer Prop { get { return Nest; } } }
            public class P
            {
                static H h = new H(); static int calls;
                static H Get() { calls++; return h; }
                public static void Main()
                {
                    h.Nest.Inner.N = 3;
                    h.Prop.Inner.Add(1); Console.WriteLine(h.Nest.Inner.N);
                    Get().Prop.Inner.Add(1); Console.WriteLine(h.Nest.Inner.N + "," + calls);
                    h.Nest.Inner.Add(1); Console.WriteLine(h.Nest.Inner.N);
                }
            }
            """, "P");
        Assert.Equal("3\n3,1\n4", output);
    }
}

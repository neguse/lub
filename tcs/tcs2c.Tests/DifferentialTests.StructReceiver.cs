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

    // auto-property の先の struct の field、readonly field は C# の「値」なので、method は copy を書き換える (#74 #414)
    [CFact]
    public void StructReceiver_AutoPropertyAndReadonlyFieldMutateCopy()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int Value; public void Increment() { Value++; } }
            public struct Outer { public S Inner; public readonly S RoInner; public Outer(int v) { Inner = new S(); RoInner = new S { Value = v }; RoInner.Increment(); } }
            public class Holder
            {
                public Outer Value { get; set; }
                public readonly S Counter = new S { Value = 1 };
                public static readonly S Shared = new S { Value = 1 };
                public S Mutable;
                public readonly Outer RoOuter = new Outer(1);
                public void Bump() { Counter.Increment(); }
                public Holder() { Counter.Increment(); }
            }
            public static class P
            {
                public static void Main()
                {
                    var h = new Holder { Value = new Outer { Inner = new S { Value = 1 } } };
                    h.Value.Inner.Increment();
                    Console.WriteLine(h.Value.Inner.Value);
                    h.Counter.Increment();
                    h.Bump();
                    Console.WriteLine(h.Counter.Value);
                    Holder.Shared.Increment();
                    Console.WriteLine(Holder.Shared.Value);
                    h.RoOuter.Inner.Increment();
                    Console.WriteLine(h.RoOuter.Inner.Value + "," + h.RoOuter.RoInner.Value);
                    h.Mutable.Increment();
                    Console.WriteLine(h.Mutable.Value);
                    var o = new Outer(5);
                    o.Inner.Increment(); o.RoInner.Increment();
                    Console.WriteLine(o.Inner.Value + "," + o.RoInner.Value);
                }
            }
            """, "P");
        Assert.Equal("1\n2\n1\n0,2\n1\n1,6", output);
    }

    // 変数の受け手は参照で渡るので、引数が要素を書き換えたら、書き換えた後の要素に呼ぶ (#431)
    [CFact]
    public void StructReceiver_ArgumentRewritesElement()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; public int M; public void Add(int n) { N += n; } }
            public static class P
            {
                static S[] values;
                static S held;
                static int Replace() { values[0] = new S { N = 100, M = 200 }; return 7; }
                static int Touch() { values[0].N = 50; values[0].M = 60; return 3; }
                static int ReplaceStatic() { held = new S { N = 10, M = 20 }; return 1; }
                public static void Main()
                {
                    values = new S[] { new S { N = 1, M = 2 } };
                    values[0].Add(Replace());
                    Console.WriteLine(values[0].N + ":" + values[0].M);
                    values[0].Add(Touch());
                    Console.WriteLine(values[0].N + ":" + values[0].M);
                    held.Add(ReplaceStatic());
                    Console.WriteLine(held.N + ":" + held.M);
                    var s = new S { N = 1 };
                    s.Add(s.N + 1);
                    Console.WriteLine(s.N);
                }
            }
            """, "P");
        Assert.Equal("107:200\n53:60\n11:20\n3", output);
    }
}

namespace TinyCs.Tcs2c.Tests;

// A built-in method whose receiver is a value-type variable (a local / field / array element and the fields of that struct) (CompareTo of int / float, GetValueOrDefault(default) of Nullable) fixes the storage location of the receiver first, evaluates the arguments, and then calls with the current value of that variable (C# §12.6.6).
// A property / List index receiver is a copy of the value read before the arguments.
// The expected value is the stdout of the same program run on CoreCLR
public class ValueReceiverOrderDifferentialTests
{
    [CFact]
    public void ValueReceiver_ReadAfterArguments()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct In { public int N; }
            public class Box { public int Value; public int Auto { get; set; } int _p; public int Prop { get { return _p; } set { _p = value; } } public In I; public int? Opt; }
            public class P
            {
                static int slot;
                static float fslot;
                static int? nslot;
                static float? fopt;
                static Box B = new Box();
                static Box Old;
                static int picks;
                static int[] A = new int[] { 1, 2 };
                static List<int> L = new List<int> { 1, 2 };
                static In SI;
                int mine;
                static Box Pick() { picks++; return B; }
                static int Update() { slot = 9; fslot = 9f; nslot = 9; fopt = 9f; Old = B; B.Value = 9; B.Auto = 9; B.Prop = 9; B.I.N = 9; B.Opt = 9; B = new Box { Value = 5 }; A[0] = 9; L[0] = 9; SI.N = 9; return 5; }
                static int Clear() { nslot = null; fopt = null; return 7; }
                int UpdateMine() { mine = 9; return 5; }
                void Reset() { slot = 1; fslot = 1f; nslot = 1; fopt = 1.5f; B = new Box { Value = 1, Auto = 1, Prop = 1 }; B.I.N = 1; B.Opt = 1; picks = 0; A = new int[] { 1, 2 }; L = new List<int> { 1, 2 }; SI.N = 1; mine = 1; }
                public static void Main() { new P().Run(); }
                void Run()
                {
                    Reset(); Console.WriteLine("static:" + slot.CompareTo(Update()) + "," + slot);
                    Reset(); Console.WriteLine("float:" + fslot.CompareTo(Update()));
                    Reset(); int n = 1; Console.WriteLine("local:" + n.CompareTo(n = 9));
                    Reset(); float fn = 1f; Console.WriteLine("flocal:" + fn.CompareTo(fn = 9f));
                    Reset(); int c = 1; Func<int> setc = () => { c = 9; return 5; }; Console.WriteLine("captured:" + c.CompareTo(setc()));
                    Reset(); Console.WriteLine("pick:" + Pick().Value.CompareTo(Update()) + "," + Old.Value + "," + B.Value + "," + picks);
                    Reset(); Console.WriteLine("auto:" + B.Auto.CompareTo(Update()));
                    Reset(); Console.WriteLine("prop:" + B.Prop.CompareTo(Update()));
                    Reset(); Console.WriteLine("nested:" + B.I.N.CompareTo(Update()));
                    Reset(); Console.WriteLine("array:" + A[0].CompareTo(Update()));
                    Reset(); Console.WriteLine("list:" + L[0].CompareTo(Update()));
                    Reset(); Console.WriteLine("static-struct:" + SI.N.CompareTo(Update()));
                    Reset(); Console.WriteLine("this:" + mine.CompareTo(UpdateMine()));
                    Reset(); Console.WriteLine("this-member:" + this.mine.CompareTo(UpdateMine()));
                    Reset(); int u = Update(); Console.WriteLine("arg-first:" + slot.CompareTo(u));
                    Reset(); int copy = slot; Console.WriteLine("copy:" + copy.CompareTo(Update()));
                    Reset(); Console.WriteLine("gvd:" + nslot.GetValueOrDefault(Update()) + "," + nslot.GetValueOrDefault());
                    Reset(); nslot = null; Console.WriteLine("gvd-null:" + nslot.GetValueOrDefault(Update()));
                    Reset(); Console.WriteLine("gvd-clear:" + nslot.GetValueOrDefault(Clear()));
                    Reset(); fopt = null; Console.WriteLine("gvd-float:" + fopt.GetValueOrDefault(Update()));
                    Reset(); Console.WriteLine("gvd-float-clear:" + fopt.GetValueOrDefault(Clear()));
                    Reset(); Console.WriteLine("gvd-pick:" + Pick().Opt.GetValueOrDefault(Update()) + "," + Old.Opt + "," + B.Opt + "," + picks);
                    Reset(); int? ln = 1; Console.WriteLine("gvd-local:" + ln.GetValueOrDefault((ln = 9).Value));
                    Reset(); int? lc = nslot; Console.WriteLine("gvd-copy:" + lc.GetValueOrDefault(Update()));
                    Reset(); int[] arr = new int[] { 1 }; int i = 0; Func<int> seti = () => { i = 1; arr = new int[] { 7, 8 }; return 5; }; Console.WriteLine("array-captured:" + arr[i].CompareTo(seti()));
                }
            }
            """, "P");
        Assert.Equal("""
            static:1,9
            float:1
            local:0
            flocal:0
            captured:1
            pick:1,9,5,1
            auto:-1
            prop:-1
            nested:1
            array:1
            list:-1
            static-struct:1
            this:1
            this-member:1
            arg-first:1
            copy:-1
            gvd:9,9
            gvd-null:9
            gvd-clear:7
            gvd-float:9
            gvd-float-clear:7
            gvd-pick:9,9,,1
            gvd-local:9
            gvd-copy:1
            array-captured:-1
            """.ReplaceLineEndings("\n"), output);
    }

    // A fault in the storage location of the receiver (a field of a null object, an element of an out-of-range / null array) comes before the arguments
    [CTheory]
    [InlineData("Box b = null; Console.WriteLine(b.Value.CompareTo(Arg()));")]
    [InlineData("var a = new int[1]; Console.WriteLine(a[1].CompareTo(Arg()));")]
    [InlineData("int[] a = null; Console.WriteLine(a[0].CompareTo(Arg()));")]
    [InlineData("var a = new float[1]; Console.WriteLine(a[1].CompareTo(Arg()));")]
    [InlineData("int?[] a = null; Console.WriteLine(a[0].GetValueOrDefault(Arg()));")]
    [InlineData("Box b = null; Console.WriteLine(b.Opt.GetValueOrDefault(Arg()));")]
    public void ValueReceiver_StorageFaultsBeforeArguments(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            public class Box { public int Value; public int? Opt; }
            public static class P
            {
                static int Arg() { Console.WriteLine("argument"); return 1; }
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

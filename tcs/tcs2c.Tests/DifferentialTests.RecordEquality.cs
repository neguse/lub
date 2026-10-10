namespace TinyCs.Tcs2c.Tests;

// record class の == / != (C# の合成 Equals: EqualityContract の一致 +
// field ごとの EqualityComparer<T>.Default)。期待値は同じ program を
// CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Records_StructFieldsCompareByValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; }
            public struct Nested { public S Item; public int K; }
            public class Ref { public int N; }
            public record Leaf(int n);
            public struct Holder { public Leaf L; }
            public record ValueRow(S item, int tag);
            public record NestedRow(Nested item);
            public record RefRow(Ref item);
            public record HolderRow(Holder h);
            public record struct ValueControl(S item, int tag);
            public record RecordControl(Leaf item);
            public class P
            {
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    var a = new ValueRow(new S { N = 2 }, 4);
                    var b = new ValueRow(new S { N = 2 }, 4);
                    var c = new ValueRow(new S { N = 3 }, 4);
                    var d = new ValueRow(new S { N = 2 }, 5);
                    Console.WriteLine("value:" + B(a == b) + ":" + B(a != b) + ":" + B(a == c) + ":" + B(a != c) + ":" + B(a == d));
                    var na = new NestedRow(new Nested { Item = new S { N = 2 }, K = 1 });
                    var nb = new NestedRow(new Nested { Item = new S { N = 2 }, K = 1 });
                    var nc = new NestedRow(new Nested { Item = new S { N = 9 }, K = 1 });
                    Console.WriteLine("nested:" + B(na == nb) + ":" + B(na == nc));
                    var shared = new Ref { N = 2 };
                    Console.WriteLine("reference:" + B(new RefRow(shared) == new RefRow(shared)) + ":" + B(new RefRow(shared) == new RefRow(new Ref { N = 2 })));
                    var ha = new HolderRow(new Holder { L = new Leaf(1) });
                    var hb = new HolderRow(new Holder { L = new Leaf(1) });
                    var hc = new HolderRow(new Holder { L = new Leaf(2) });
                    Console.WriteLine("struct-record:" + B(ha == hb) + ":" + B(ha == hc));
                    Console.WriteLine("record-struct:" + B(new ValueControl(new S { N = 2 }, 4) == new ValueControl(new S { N = 2 }, 4)));
                    Console.WriteLine("record-field:" + B(new RecordControl(new Leaf(2)) == new RecordControl(new Leaf(2))));
                }
            }
            """, "P");
        Assert.Equal("""
            value:1:0:0:1:0
            nested:1:0
            reference:1:0
            struct-record:1:0
            record-struct:1
            record-field:1
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Records_DifferentRuntimeTypesAreNotEqual()
    {
        var output = Backends.AssertParity("""
            using System;
            public record Root(int tag);
            public record Left(int tag, int value) : Root(tag);
            public record Right(int tag, int value) : Root(tag);
            public class P
            {
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    Root root = new Root(3);
                    Root rootEqual = new Root(3);
                    Root left = new Left(3, 5);
                    Root leftEqual = new Left(3, 5);
                    Root leftDifferent = new Left(3, 6);
                    Root right = new Right(3, 5);
                    Root none = null;
                    Console.WriteLine("base-derived:" + B(root == left) + ":" + B(left == root));
                    Console.WriteLine("siblings:" + B(left == right) + ":" + B(right == left));
                    Console.WriteLine("same-type:" + B(root == rootEqual) + ":" + B(left == leftEqual) + ":" + B(left == leftDifferent));
                    Console.WriteLine("not-equal:" + B(root != left) + ":" + B(left != right) + ":" + B(left != leftEqual));
                    Console.WriteLine("null:" + B(root == none) + ":" + B(none == root) + ":" + B(none == null));
                }
            }
            """, "P");
        Assert.Equal("""
            base-derived:0:0
            siblings:0:0
            same-type:1:1:0
            not-equal:1:1:0
            null:0:0:1
            """.ReplaceLineEndings("\n"), output);
    }
}

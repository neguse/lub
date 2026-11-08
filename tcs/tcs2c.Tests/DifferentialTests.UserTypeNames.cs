namespace TinyCs.Tcs2c.Tests;

// user の型の名前が TinySystem の facade の名前 (Random) や C# の型の keyword (@float) と同じでも、user の型を指す
public partial class DifferentialTests
{
    // global namespace の user の class Random は TinySystem.Random を隠す (C# の名前の引き方と同じ)
    [CFact]
    public void UserClassNamedRandom_ShadowsTinySystemRandom()
    {
        var output = Backends.AssertParity("""
            using System.Collections.Generic;
            public class Random
            {
                public int Seed;
                public int Next() => -1;
                public int Next(int max) => max + Seed;
            }
            public static class P
            {
                static Random Make(int seed) => new Random { Seed = seed };
                public static void Main()
                {
                    var r = new Random();
                    Random held = Make(3);
                    var list = new List<Random> { held };
                    System.Console.WriteLine(r.Next() + "," + held.Next(10) + "," + list[0].Seed + "," + (r is Random));
                    System.Console.WriteLine(TinySystem.Random.Shared.Next(1) + "," + new TinySystem.Random(3).Next(1));
                }
            }
            """, "P");
        Assert.Equal("-1,13,3,True\n0,0", output);
    }

    // TinySystem.Text.Utf8Bytes も同じ (user の class Utf8Bytes が隠す)
    [CFact]
    public void UserClassNamedUtf8Bytes_ShadowsTinySystemText()
    {
        var output = Backends.AssertParity("""
            public class Utf8Bytes { public int N = 3; }
            public static class P
            {
                public static void Main()
                {
                    var u = new Utf8Bytes();
                    Utf8Bytes v = u;
                    var t = new TinySystem.Text.Utf8Bytes("ab");
                    System.Console.WriteLine((u.N + v.N) + "," + t.Length);
                }
            }
            """, "P");
        Assert.Equal("6,2", output);
    }

    // verbatim identifier の class @float は float ではない。new と型の test と switch の arm
    [CFact]
    public void UserClassNamedFloat_IsTheClass()
    {
        var output = Backends.AssertParity("""
            public class @float { public int N = 4; }
            public static class P
            {
                static int Classify(object o) => o switch { @float => 1, _ => 0 };
                public static void Main()
                {
                    object s = new @float();
                    float g = 1.5f;
                    System.Console.WriteLine((s is @float) + "," + Classify(s) + "," + Classify("x") + "," + Classify(g) + "," + g);
                }
            }
            """, "P");
        Assert.Equal("True,1,0,0,1.5", output);
    }

    // @float を変数、引数、戻り値、List の要素の型にする形。C backend は表示名の `@float` を float に読むので VM と .NET で見る
    [CFact]
    public void UserClassNamedFloat_AsDeclaredType_VmMatchesDotnet()
    {
        const string source = """
            using System.Collections.Generic;
            public class @float { public int N = 4; }
            public static class P
            {
                static @float Make(int n) => new @float { N = n };
                static int Read(@float f) => f.N;
                public static void Main()
                {
                    @float a = Make(7);
                    var list = new List<@float> { a, new @float() };
                    object o = a;
                    var n = o is @float b ? b.N : -1;
                    float g = 2.5f;
                    System.Console.WriteLine(Read(a) + "," + list[1].N + "," + n + "," + g);
                }
            }
            """;
        Assert.Equal("7,4,7,2.5", Backends.RunDotnet(source, "P"));
        Assert.Equal("7,4,7,2.5", Backends.RunVm([source], "P"));
    }
}

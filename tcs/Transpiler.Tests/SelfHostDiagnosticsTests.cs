namespace TinyCs.Tests;

/// <summary>
/// セルフホストの compiler (SelfHost/) が参照実装と同じく拒否する検査 (準拠検査、フロー解析) と、
/// 拒否してはならない形。参照実装との比較は SelfHostDifferential (TCS_SELFHOST_DIFF=1) が母集団の全体で行い、
/// ここでは検査ごとの代表を固定する。
/// </summary>
public class SelfHostDiagnosticsTests
{
    private static List<string> Errors(string source)
    {
        var result = TcsSelf.Compiler.Compile(["input.cs"], [source], [], [], "");
        return result.Diagnostics.Where(d => d.Contains(" error ") || d.Contains(" warning TCS")).ToList();
    }

    [Theory]
    [InlineData("CS0161", "public static class P { public static int F(bool c) { if (c) return 1; } }")]
    [InlineData("CS1643", "public static class P { public static void F() { System.Func<int> f = () => { }; } }")]
    [InlineData("CS0165", "public static class P { public static int F() { int x; return x; } }")]
    [InlineData("CS0220", "public static class P { public static int F() { const int m = 2147483647; return m + 1; } }")]
    [InlineData("CS0236", "public class H { public int A = 1; public int B = A; }")]
    [InlineData("CS0523", "public struct Loop { public Loop? Next; }")]
    [InlineData("CS1612", "public struct S { public int X; } public class H { public S P { get; set; } public void M() { P.X = 3; } }")]
    [InlineData("CS0111", "public class C { public void M(int a) { } public void M(int b) { } }")]
    [InlineData("CS0150", "public static class P { public static int[] F(int n) => new int[n] { 1, 2 }; }")]
    [InlineData("CS0847", "public static class P { public static int[] F() => new int[3] { 1, 2 }; }")]
    [InlineData("CS1032", "public class C { }\n#define X\n")]
    [InlineData("TCS1001", "public abstract class A { }")]
    [InlineData("TCS1001", "public class Has_Underscore { }")]
    [InlineData("TCS1001", "public class C { private int __x; }")]
    [InlineData("TCS1001", "public class C { public C(int a) : this() { } public C() { } }")]
    [InlineData("TCS1001", "public class C { long x; }")]
    [InlineData("TCS1001", "public static class P { public static int[] F() { int[] a = { 1, 2 }; return a; } }")]
    [InlineData("TCS1001", "public interface I<T> { } public interface J : I<string[,]> { }")]
    [InlineData("TCS1001", "public class C { [System.Diagnostics.Conditional(\"DEBUG\")] public static void M() { } }")]
    [InlineData("TCS1001", "public record R(int X) { public int Y { get; init; } }")]
    [InlineData("TCS1002", "using System; public static class P { static Random r = new Random(); }")]
    [InlineData("TCS1003", "public static class P { public static string[] F(int n) => new string[n]; }")]
    public void RejectsLikeReference(string id, string source)
    {
        var diagnostics = Errors(source);
        Assert.True(diagnostics.Any(d => d.Contains(" " + id + ":")),
            $"expected {id}, got:\n{string.Join("\n", diagnostics)}");
    }

    // C# が受理する形 (到達できない場所の変数、定数の条件、field ごとの struct の代入、Color Color、using static、#if)
    [Theory]
    [InlineData("public static class P { public static int F() { while (true) { } } }")]
    [InlineData("public static class P { public static int F(int k) { switch (k) { case 1: return 1; default: return 2; } } }")]
    [InlineData("public static class P { public static int F() { do { return 1; } while (true); } }")]
    [InlineData("public static class P { public static int F() { return unchecked(2147483647 + 1); } }")]
    [InlineData("public static class P { public static int F(bool c) { int x; if (c) x = 1; else x = 2; return x; } }")]
    [InlineData("public struct S { public int X; public int Y; } public static class P { public static int F() { S s; s.X = 1; s.Y = 2; return s.X; } }")]
    [InlineData("public static class P { public static int F(int n) { int x; int t = 0; for (int i = 0; i < n; i = x) { x = i + 1; t += x; } return t; } }")]
    [InlineData("public static class P { public static void F() { int x; if (true) { } else { System.Console.WriteLine(x); } } }")]
    [InlineData("public static class P { public static void F() { bool b; switch (1) { case 2 when b: break; } } }")]
    [InlineData("public struct S { public int X; } public static class P { public static void F() { var a = new S[2]; a[0].X = 3; } }")]
    [InlineData("public struct Color { public static readonly Color Black = new Color(); } public class A { public Color Color; public void F() { Color = Color.Black; } }")]
    [InlineData("namespace N1 { public static class A { public static int M() => 1; } } namespace N2 { using static N1.A; public static class C { public static int F() => M(); } }")]
    [InlineData("#define X\npublic static class P {\n#if X && !Y\npublic static int F() => 1;\n#else\n/* not closed\n#endif\n}")]
    [InlineData("public static class P { public static int[][] F() => new int[0][]; }")]
    public void AcceptsLikeReference(string source)
    {
        var diagnostics = Errors(source);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics));
    }
}

namespace TinyCs.Tcs2c.Tests;

// object / interface / 型引数からの cast と型 test。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    private const string CastTypes = """
        using System;
        using System.Collections.Generic;
        public interface I { int M(); }
        public interface J { int N(); }
        public class A : I, J { public int M() { return 1; } public int N() { return 2; } }
        public class B : I { public int M() { return 3; } }
        public class C { public int V = 7; }
        public record R(int V) : I { public int M() { return V; } }
        public class Bx<T> { public T V; }
        public enum E { P, Q }
        public static class P
        {
            static object O(object x) { return x; }
            static C AsC<T>(T x) where T : class { return (C)(object)x; }
            static I AsI<T>(T x) { return (I)(object)x; }
            static J ToJ<T>(T x) where T : I { return (J)x; }
            public static void Main()
            {

        """;

    private static string CastProgram(string body) => CastTypes + body + "\n    }\n}\n";

    [CFact]
    public void Casts_Matching()
    {
        var output = Backends.AssertAllPathsAgree(CastProgram("""
            Console.WriteLine(((I)O(new A())).M());
            I i = new A();
            Console.WriteLine(((J)i).N());
            Console.WriteLine(((A)i).N());
            Console.WriteLine(((C)O(new C())).V);
            I r = new R(4);
            Console.WriteLine(((R)r).V);
            Console.WriteLine((string)O("s"));
            Console.WriteLine((int)O(5));
            Console.WriteLine((float)O(2f));
            Console.WriteLine((float)O(1.5f));
            Console.WriteLine((bool)O(true));
            Console.WriteLine((C)O(null) == null);
            Console.WriteLine((I)O(null) == null);
            Console.WriteLine((string)O(null) == null);
            Console.WriteLine(AsC(new C()).V);
            Console.WriteLine(AsI(new B()).M());
            Console.WriteLine(ToJ(new A()).N());
            Console.WriteLine((int)E.Q + (int)1.5f);
            Console.WriteLine((E)1 == E.Q);
            object up = new A();
            Console.WriteLine(((I)up).M());
            var list = new List<I> { new A() };
            Console.WriteLine(((J)list[0]).N());
            """), "P");
        Assert.Equal(
            "1\n2\n2\n7\n4\ns\n5\n2\n1.5\nTrue\nTrue\nTrue\nTrue\n7\n3\n2\n2\nTrue\n1\n2",
            output.ReplaceLineEndings("\n"));
    }

    [CTheory]
    [InlineData("I x = (I)O(new C());")]
    [InlineData("I i = new B(); J x = (J)i;")]
    [InlineData("C c = new C(); I x = (I)c;")]
    [InlineData("C x = (C)O(new A());")]
    [InlineData("I i = new B(); A x = (A)i;")]
    [InlineData("R x = (R)O(new C());")]
    [InlineData("string x = (string)O(1);")]
    [InlineData("int x = (int)O(\"s\");")]
    [InlineData("int x = (int)O(2f);")]
    [InlineData("int x = (int)O(null);")]
    [InlineData("float x = (float)O(2);")]
    [InlineData("float x = (float)O(null);")]
    [InlineData("bool x = (bool)O(1);")]
    [InlineData("bool x = (bool)O(null);")]
    [InlineData("C x = AsC(new A());")]
    [InlineData("I x = AsI(new C());")]
    [InlineData("J x = ToJ(new B());")]
    public void Casts_MismatchFaults(string body)
    {
        Backends.AssertFaultOnAllPaths(CastProgram(body + "\nConsole.WriteLine(\"after\");"), "P");
    }

    [CTheory]
    [InlineData("var x = (List<int>)O(new List<int>());", "UnverifiableCast(System.Collections.Generic.List<int>)")]
    [InlineData("var x = (Dictionary<int, int>)O(null);", "UnverifiableCast(System.Collections.Generic.Dictionary<int, int>)")]
    [InlineData("var x = (Queue<int>)O(null);", "UnverifiableCast(System.Collections.Generic.Queue<int>)")]
    [InlineData("var x = (int[])O(null);", "UnverifiableCast(int[])")]
    [InlineData("var x = (Action)O(null);", "UnverifiableCast(System.Action)")]
    [InlineData("var x = (Bx<int>)O(null);", "UnverifiableCast(Bx<int>)")]
    [InlineData("var x = (E)O(1);", "UnverifiableCast(E)")]
    [InlineData("var x = (char)O(1);", "UnverifiableCast(char)")]
    [InlineData("var x = (byte)O(1);", "UnverifiableCast(byte)")]
    [InlineData("var x = (int?)O(1);", "UnverifiableCast(int?)")]
    [InlineData("var x = O(1) is List<int>;", "UnverifiableTypeTest(System.Collections.Generic.List<int>)")]
    [InlineData("var x = O(1) is int[];", "UnverifiableTypeTest(int[])")]
    [InlineData("var x = O(1) is Bx<int>;", "UnverifiableTypeTest(Bx<int>)")]
    [InlineData("var x = O(1) is E;", "UnverifiableTypeTest(E)")]
    [InlineData("var x = O(1) is char;", "UnverifiableTypeTest(char)")]
    [InlineData("object x = 'a';", "ValueBoxing(char)")]
    [InlineData("object x = E.Q;", "ValueBoxing(E)")]
    [InlineData("byte b = 1; object x = b;", "ValueBoxing(byte)")]
    [InlineData("O('a');", "ValueBoxing(char)")]
    [InlineData("var l = new List<object> { E.Q };", "ValueBoxing(E)")]
    public void Casts_Unverifiable_BothBackendsStopWithDiagnostic(string body, string syntaxName)
    {
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([CastProgram(body)], "P"),
                     () => Backends.EmitC([CastProgram(body)], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1001: unsupported syntax: {syntaxName}", ex.Message);
        }
    }

    [CFact]
    public void Casts_TypeParameter_BothBackendsStopWithDiagnostic()
    {
        const string program = """
            public static class P
            {
                static T Get<T>(object o) { return (T)o; }
                static bool Is<T>(object o) { return o is T; }
                public static void Main() { }
            }
            """;
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("UnverifiableCast(T)", ex.Message);
            Assert.Contains("UnverifiableTypeTest(T)", ex.Message);
        }
    }

    // 文字列化と同じ型への照合は object の値にならない
    [CFact]
    public void Casts_StringifiedValuesStayAllowed()
    {
        var output = Backends.AssertAllPathsAgree(CastProgram("""
            char c = 'a';
            E e = E.Q;
            byte b = 2;
            Console.WriteLine("x" + c + e + b);
            Console.WriteLine($"{c}{e}{b}");
            Console.WriteLine(e);
            Console.WriteLine(c);
            Console.WriteLine(b);
            var bx = new Bx<int>();
            Console.WriteLine(bx is Bx<int>);
            var l = new List<int>();
            Console.WriteLine(l is List<int>);
            """), "P");
        Assert.Equal("xaQ2\naQ2\nQ\na\n2\nTrue\nTrue", output.ReplaceLineEndings("\n"));
    }
}

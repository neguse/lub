namespace TinyCs.Tcs2c.Tests;

// 構造型 (List<T> / 配列 / Dictionary<K,V>) への is。C は box 時に付く構造型
// id で判定し、期待値は同じ program を CoreCLR で実行した stdout。Lua の table
// は構造型の tag を持たないので TCS1001 で止める
public partial class DifferentialTests
{
    private const string StructuralIsProgram = """
            using System;
            using System.Collections.Generic;
            public class P
            {
                static int calls;
                static object Get(object o) { calls++; return o; }
                static string Kind(object o) => o switch
                {
                    List<float> => "lf",
                    List<int> => "li",
                    float[] => "af",
                    Dictionary<string, object> => "d",
                    null => "null",
                    _ => "other",
                };
                static string Stmt(object o)
                {
                    switch (o)
                    {
                        case int[]: return "ai";
                        case List<float>: return "lf";
                        default: return "other";
                    }
                }
                public static void Main()
                {
                    object list = new List<float> { 1f, 2f };
                    object ints = new List<int> { 3 };
                    object arr = new float[] { 1f, 2f, 3f };
                    object iarr = new int[] { 4 };
                    object dict = new Dictionary<string, object> { { "a", 1 } };
                    object none = null;
                    object box = 5;
                    Console.WriteLine((list is List<float> ? 1 : 0) + "," + (ints is List<float> ? 1 : 0) + "," + (arr is List<float> ? 1 : 0) + "," + (none is List<float> ? 1 : 0) + "," + (box is List<float> ? 1 : 0));
                    Console.WriteLine((arr is float[] ? 1 : 0) + "," + (list is float[] ? 1 : 0) + "," + (ints is List<int> ? 1 : 0) + "," + (dict is Dictionary<string, object> ? 1 : 0));
                    Console.WriteLine((list is not List<float> ? 1 : 0) + "," + (arr is not List<float> ? 1 : 0));
                    Console.WriteLine(Kind(list) + "," + Kind(ints) + "," + Kind(arr) + "," + Kind(dict) + "," + Kind(none) + "," + Kind(box));
                    Console.WriteLine(Stmt(iarr) + "," + Stmt(list) + "," + Stmt(arr));
                    Console.WriteLine((Get(list) is List<float> ? 1 : 0) + ":" + calls);
                    var typed = new List<float>();
                    List<float> nullList = null;
                    Console.WriteLine((typed is List<float> ? 1 : 0) + "," + (nullList is List<float> ? 1 : 0));
                }
            }
        """;

    [CFact]
    public void StructuralIs_C_MatchesDotnet()
    {
        const string expected = "1,0,0,0,0\n1,0,1,1\n0,1\nlf,li,af,d,null,other\nai,lf,other\n1:1\n1,0";
        Assert.Equal(expected, Backends.RunC([StructuralIsProgram], "P"));
        Assert.Equal(expected, Backends.RunC([StructuralIsProgram], "P", stress: true));
    }

    [Fact]
    public void StructuralIs_Lua_ReportsUnsupported()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            public class P
            {
                public static void Main()
                {
                    object o = new List<float> { 1f };
                    Console.WriteLine(o is List<float> ? "list" : "other");
                }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([source]);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("error TCS1001")
            && e.Contains("type test against System.Collections.Generic.List<float>"));
    }

    // field 初期化子でも同じ
    [Fact]
    public void StructuralIs_LuaFieldInitializer_ReportsUnsupported()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            public class P
            {
                static object o = new List<float>();
                static bool b = o is List<float>;
                public static void Main() { Console.WriteLine(b); }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([source]);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("error TCS1001")
            && e.Contains("type test against"));
    }
}

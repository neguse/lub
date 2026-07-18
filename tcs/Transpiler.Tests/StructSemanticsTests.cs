namespace TinyCs.Tests;

// T219 (M5 v1): データ struct の値意味論 (il-spec §10 の copy 地点)
public class StructSemanticsTests
{
    private const string Vec = """
        public struct Vec2
        {
            public float X;
            public float Y;
        }
        """;

    [Fact]
    public void Assignment_Copies()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class T
            {
                public static string Test()
                {
                    var a = new Vec2();
                    a.X = 1.0f;
                    var b = a;
                    b.X = 99.0f;
                    return $"{a.X}|{b.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|99", result);
    }

    [Fact]
    public void ArgumentAndReturn_Copy()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class T
            {
                static Vec2 Bump(Vec2 v)
                {
                    v.X = v.X + 10.0f;
                    return v;
                }
                public static string Test()
                {
                    var a = new Vec2();
                    a.X = 1.0f;
                    var b = Bump(a);
                    return $"{a.X}|{b.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("1|11", result);
    }

    [Fact]
    public void ArrayElement_InPlaceWrite_AndReadCopies()
    {
        var result = TestHelper.TranspileAndRun(Vec + """
            public class T
            {
                public static string Test()
                {
                    var arr = new Vec2[2];
                    arr[0] = new Vec2();
                    arr[0].X = 5.0f;
                    var p = arr[0];
                    p.X = 42.0f;
                    return $"{arr[0].X}|{p.X}";
                }
            }
            """, "T.Test()", differential: false);
        Assert.Equal("5|42", result);
    }

    [Fact]
    public void StructMethod_ReportsDiagnostic()
    {
        var result = Transpiler.TranspileWithDiagnostics([Vec.Replace(
            "public float Y;",
            "public float Y; public float Len2() { return X * X + Y * Y; }")]);
        Assert.Contains(result.Warnings,
            w => w.Contains("StructMember"));
    }
}

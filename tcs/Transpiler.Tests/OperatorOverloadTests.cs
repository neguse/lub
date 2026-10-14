namespace TinyCs.Tests;

// ユーザー定義演算子オーバーロード (binary + - * / % / unary -) の
// Lua metamethod (__add/__sub/__mul/__div/__mod/__unm) 写像を固定する。
// 同一演算子の複数 overload は metamethod 内の実行時型分岐で dispatch する。
public class OperatorOverloadTests
{
    private const string Vec2Source = """
        public class Vec2
        {
            public float X;
            public float Y;

            public Vec2(float x, float y)
            {
                X = x;
                Y = y;
            }

            public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
            public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
            public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
            public static Vec2 operator *(Vec2 a, Vec2 b) => new Vec2(a.X * b.X, a.Y * b.Y);
            public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
            public static Vec2 operator *(float s, Vec2 a) => new Vec2(s * a.X, s * a.Y);
            public static Vec2 operator /(Vec2 a, Vec2 b) => new Vec2(a.X / b.X, a.Y / b.Y);
            public static Vec2 operator /(Vec2 a, float s) => new Vec2(a.X / s, a.Y / s);
        }
        """;

    [Fact]
    public void BinaryAdd_SingleOverload()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var v = new Vec2(1, 2) + new Vec2(10, 20);
                    return v.X * 100 + v.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("1122.0", result);
    }

    [Fact]
    public void BinarySubtract_SingleOverload()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var v = new Vec2(10, 20) - new Vec2(1, 2);
                    return v.X * 100 + v.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("918.0", result);
    }

    [Fact]
    public void UnaryMinus()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var v = -new Vec2(3, -4);
                    return v.X * 100 + v.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("-296.0", result);
    }

    [Fact]
    public void MultiplyOverloads_DispatchOnOperandTypes()
    {
        // Vec2*Vec2 / Vec2*float / float*Vec2 が同じ __mul に共存し、
        // 実行時の operand 型で正しい overload に分岐する。
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var vv = new Vec2(2, 3) * new Vec2(4, 5);   // (8, 15)
                    var vs = new Vec2(2, 3) * 10.0f;            // (20, 30)
                    var sv = 100.0f * new Vec2(2, 3);           // (200, 300)
                    return vv.X + vv.Y + vs.X + vs.Y + sv.X + sv.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("573.0", result);
    }

    [Fact]
    public void DivideOverloads_DispatchOnOperandTypes()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var vv = new Vec2(8, 15) / new Vec2(4, 5);  // (2, 3)
                    var vs = new Vec2(20, 30) / 10.0f;          // (2, 3)
                    return vv.X + vv.Y + vs.X + vs.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("10.0", result);
    }

    [Fact]
    public void Modulo_SingleOverload()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Wrap
            {
                public int Value;

                public Wrap(int value)
                {
                    Value = value;
                }

                public static Wrap operator %(Wrap a, Wrap b) => new Wrap(a.Value % b.Value);
            }

            public class T
            {
                public static int Test()
                {
                    return (new Wrap(17) % new Wrap(5)).Value;
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void CompoundAssignment_UsesOperatorOverload()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var v = new Vec2(1, 2);
                    v += new Vec2(10, 20);
                    v *= 2.0f;
                    return v.X * 100 + v.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("2244.0", result);
    }

    [Fact]
    public void ChainedExpression_MixesOverloadsAndPrecedence()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var a = new Vec2(1, 2);
                    var b = new Vec2(3, 4);
                    var v = a + b * 2.0f - a;  // b * 2 = (6, 8)
                    return v.X * 100 + v.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("608.0", result);
    }

    [Fact]
    public void OperatorWithStatementBody()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Acc
            {
                public int Total;

                public Acc(int total)
                {
                    Total = total;
                }

                public static Acc operator +(Acc a, Acc b)
                {
                    var sum = a.Total + b.Total;
                    return new Acc(sum);
                }
            }

            public class T
            {
                public static int Test() => (new Acc(40) + new Acc(2)).Total;
            }
            """, "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void RecordClass_OperatorOverload()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(float X, float Y)
            {
                public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
            }

            public class T
            {
                public static float Test()
                {
                    var p = new Point(1, 2) + new Point(10, 20);
                    return p.X * 100 + p.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("1122.0", result);
    }

    [Fact]
    public void InstanceMembersAndOperators_Coexist()
    {
        var result = TestHelper.TranspileAndRun(Vec2Source + """
            public class T
            {
                public static float Test()
                {
                    var v = new Vec2(3, 4) + new Vec2(0, 0);
                    return Dot(v, v);
                }

                private static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
            }
            """, "T.Test()");
        Assert.Equal("25.0", result);
    }

    [Fact]
    public void EqualityOperators_ReportUnsupportedSyntax()
    {
        // 通常 class の operator == / != はスコープ外 (record __eq のみ対応)。
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Id
            {
                public int Value;

                public static bool operator ==(Id a, Id b) => a.Value == b.Value;
                public static bool operator !=(Id a, Id b) => a.Value != b.Value;
            }
            """]);
        Assert.False(result.Success);
        Assert.Equal(2, result.Errors.Count(e =>
            e.Contains(TinyCsDiagnosticIds.UnsupportedSyntax)
            && e.Contains("OperatorDeclaration")));
        Assert.Contains(result.Errors, e => e.Contains("OperatorDeclaration(==)"));
        Assert.Contains(result.Errors, e => e.Contains("OperatorDeclaration(!=)"));
    }

    [Fact]
    public void ConversionOperator_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Meters
            {
                public float Value;

                public static implicit operator float(Meters m) => m.Value;
            }
            """]);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains(TinyCsDiagnosticIds.UnsupportedSyntax)
            && e.Contains("ConversionOperatorDeclaration"));
    }

    [Fact]
    public void SupportedOperators_ProduceNoWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics([Vec2Source]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("OperatorDeclaration"));
    }

    // operator を持つ class。呼び出し箇所で operator を直接呼ぶ (#13 / #23)
    private const string OperatorClassSource = """
        public class V
        {
            public int X;
            public V(int x) { X = x; }
            public static V operator +(V a, V b) => new V(a.X + b.X);
            public static V operator -(V a) => new V(-a.X);
            public static V operator *(V a, V b) => new V(a.X * b.X);
            public static V operator *(V a, int s) => new V(a.X * s * 10);
        }
        """;

    [Fact]
    public void CompoundAssignment_OnFieldAndProperty_UsesOperator()
    {
        var result = TestHelper.TranspileAndRun(OperatorClassSource + """
            public class Holder
            {
                public V Pos = new V(1);
                private V _p = new V(10);
                public V P { get { return _p; } set { _p = value; } }
            }
            public class T
            {
                public static int Test()
                {
                    V v = new V(1);
                    v += new V(2);
                    v *= 2;
                    var h = new Holder();
                    h.Pos += new V(5);
                    h.P += new V(20);
                    return v.X * 10000 + h.Pos.X * 100 + h.P.X;
                }
            }
            """, "T.Test()");
        Assert.Equal("600630", result);
    }

    // overload は C# が静的に選ぶ。実行時型だけでは区別できない (interface 型の
    // operand が実体は同じ class) 場合も宣言どおりの overload を呼ぶ
    [Fact]
    public void Overloads_AreChosenByStaticOperandType_NotRuntimeType()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface IHasX { int X { get; } }
            public class V : IHasX
            {
                public int X { get; set; }
                public V(int x) { X = x; }
                public static V operator +(V a, V b) => new V(a.X + b.X);
                public static V operator +(V a, IHasX b) => new V(a.X + b.X * 100);
            }
            public class T
            {
                public static int Test()
                {
                    V a = new V(1);
                    V b = new V(2);
                    IHasX i = b;
                    return (a + b).X * 1000 + (a + i).X;
                }
            }
            """, "T.Test()");
        Assert.Equal("3201", result);
    }

    // operator の結果型が int / 引数が string でも、組み込みの整数除算
    // (__tcs_idiv) や文字列連結 (..) に化けず operator を呼ぶ
    [Fact]
    public void Operators_WithPrimitiveResultOrOperand_CallOperator()
    {
        var result = TestHelper.TranspileAndRun("""
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static int operator /(V a, V b) => a.X / b.X;
                public static int operator %(V a, V b) => a.X % b.X;
                public static V operator +(V a, string s) => new V(a.X + s.Length);
                public static string operator -(V a, string s) => s + a.X;
            }
            public class T
            {
                public static string Test()
                {
                    var a = new V(7);
                    var b = new V(2);
                    return (a / b) + "|" + (a % b) + "|" + (a + "abc").X + "|" + (a - "n");
                }
            }
            """, "T.Test()");
        Assert.Equal("3|1|10|n7", result);
    }

    // ==/!= は operator 宣言が無ければ参照比較のまま (null 比較を含む)
    [Fact]
    public void EqualityOnOperatorClass_StaysReferenceIdentity()
    {
        var result = TestHelper.TranspileAndRun(OperatorClassSource + """
            public class T
            {
                static string B(bool v) => v ? "T" : "F";
                public static string Test()
                {
                    V a = new V(1);
                    V b = new V(1);
                    V c = a;
                    V n = null;
                    return B(a == b) + B(a == c) + B(a != b) + B(a == null)
                        + B(n == null) + B(null != a);
                }
            }
            """, "T.Test()");
        Assert.Equal("FTTFTT", result);
    }

    // interface の static abstract operator は subset 外 (TCS1001)
    private const string InterfaceOperatorSource = """
        public interface IAdd<T> where T : IAdd<T> { static abstract T operator +(T a, T b); }
        public class V : IAdd<V>
        {
            public int X;
            public V(int x) { X = x; }
            public static V operator +(V a, V b) => new V(a.X + b.X);
        }
        public class T
        {
            static U Sum<U>(U a, U b) where U : IAdd<U> => a + b;
            public static int Test() => Sum(new V(1), new V(2)).X;
        }
        """;

    [Fact]
    public void InterfaceStaticAbstractOperator_IsDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics([InterfaceOperatorSource]);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("TCS1001")
            && e.Contains("InterfaceOperatorDeclaration"));
    }
}

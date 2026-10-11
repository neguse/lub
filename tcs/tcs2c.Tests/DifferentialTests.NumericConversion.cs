namespace TinyCs.Tcs2c.Tests;

// int ↔ float の数値変換 (il-spec §5 / §6) と int の Sum の overflow (§12 / §13:
// 規範は TinySystem dotnet facade = checked な Enumerable.Sum)。期待値は同じ
// program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void NumericConversion_IntToFloatAndBoundaries_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum E { Min = -2147483648, Next }
            public class Box { public float F; }
            public class P
            {
                public static float Ret(int x) => x;
                public static float Id(float f) => f;
                public static int I(float f) => (int)f;
                public static void Main()
                {
                    int value = 16777217;
                    int neg = -16777217;
                    float rounded = value;
                    Console.WriteLine("assign " + I(rounded) + " " + I(neg));
                    Console.WriteLine("cmp " + (value == 16777216f ? 1 : 0) + " " + (value <= 16777216f ? 1 : 0) + " " + (neg == -16777216f ? 1 : 0) + " " + (16777216f != value ? 1 : 0));
                    Console.WriteLine("call " + I(Ret(value)) + " " + I(Id(value)) + " " + I(Id(neg)));
                    Console.WriteLine("cast " + I((float)value) + " " + ((float)value == 16777216f ? 1 : 0));
                    var box = new Box();
                    box.F = value;
                    float acc = 0f;
                    acc = value;
                    var arr = new float[1];
                    arr[0] = value;
                    var list = new List<float> { value };
                    list.Add(neg);
                    Console.WriteLine("store " + I(box.F) + " " + I(acc) + " " + I(arr[0]) + " " + I(list[0]) + " " + I(list[1]));
                    float? maybe = value;
                    Console.WriteLine("nullable " + I(maybe.Value));
                    float big = 100000;
                    float c = 16777217;
                    Console.WriteLine("const " + I(big * big / 1000000f) + " " + I(c) + " " + (c == 16777216f ? 1 : 0));
                    float mix = value * 1f;
                    float sum = 0.5f;
                    sum += value;
                    Console.WriteLine("arith " + I(mix) + " " + I(sum) + " " + I(value + 0f));
                    var fresh = new Box();
                    var zeros = new float[1];
                    float viaField = fresh.F + value;
                    float viaArray = zeros[0] + value;
                    Console.WriteLine("default " + I(viaField) + " " + (viaField == 16777216f ? 1 : 0) + " " + I(viaArray) + " " + (viaArray == 16777216f ? 1 : 0));
                    char ch = 'A';
                    float fc = ch;
                    Console.WriteLine("char " + I(fc * 2f));
                    int min = (int)E.Min;
                    Console.WriteLine("enum " + (min + 1 == -2147483647 ? 1 : 0) + " " + ((int)E.Next) + " " + (E.Min < E.Next ? 1 : 0));
                    float lo = -2147483648f;
                    float hi = 2147483520f;
                    Console.WriteLine("trunc " + (int)lo + " " + (int)hi + " " + (int)-2147483520f);
                }
            }
            """, "P");
        Assert.Equal("""
            assign 16777216 -16777216
            cmp 1 1 1 0
            call 16777216 16777216 -16777216
            cast 16777216 1
            store 16777216 16777216 16777216 16777216 -16777216
            nullable 16777216
            const 10000 16777216 1
            arith 16777216 16777216 16777216
            default 16777216 1 16777216 1
            char 130
            enum 1 -2147483647 1
            trunc -2147483648 2147483520 -2147483520
            """.ReplaceLineEndings("\n"), output);
    }

    // runtime が返す float (Parse / TryParse / 空の Sum) も Lua で float なので、
    // 組み込みの float 算術で int operand の変換を省いても f32 に丸まる。
    // uint / short / byte → float も int と同じ変換
    [CFact]
    public void NumericConversion_RuntimeFloatsAndOtherIntegrals_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class P
            {
                public static int I(float f) => (int)f;
                public static string B(bool b) => b ? ":1" : ":0";
                public static void Main()
                {
                    int value = 16777217;
                    float parsed = float.Parse("16777217");
                    float.TryParse("16777217", out var tried);
                    float.TryParse("x", out var failed);
                    var empty = new List<float>();
                    float viaParse = parsed + value;
                    float viaTry = tried + value;
                    float viaFailed = failed + value;
                    float viaSum = empty.Sum() + value;
                    float viaSelector = empty.Sum(x => x) + value;
                    Console.WriteLine("runtime " + I(parsed) + B(parsed == 16777216f) + " " + I(tried) + B(tried == 16777216f) + " " + I(viaParse) + B(viaParse == 33554432f) + " " + I(viaTry) + B(viaTry == 33554432f) + " " + I(viaFailed) + B(viaFailed == 16777216f) + " " + I(viaSum) + B(viaSum == 16777216f) + " " + I(viaSelector) + B(viaSelector == 16777216f));
                    var big = new List<float> { float.Parse("2000000000"), float.Parse("2000000000") };
                    Console.WriteLine("float-sum " + I(big.Sum() / 1000f) + " " + I(big.Sum(x => x) / 1000f));
                    var items = new List<string> { "a", "bb" };
                    Console.WriteLine("int-sum-selector " + items.Sum(s => s.Length) + " " + I(items.Sum(s => s.Length * 0.5f) * 10f));
                    uint u = 16777217u;
                    float fu = u;
                    short sh = -7;
                    float fs = sh;
                    byte by = 200;
                    float fb = by;
                    Console.WriteLine("integral " + I(fu) + B(fu == 16777216f) + " " + B(u == 16777216f) + " " + I(fs * 2f) + " " + I(fb * 2f));
                }
            }
            """, "P");
        Assert.Equal("""
            runtime 16777216:1 16777216:1 33554432:1 33554432:1 16777216:1 16777216:1 16777216:1
            float-sum 4000000 4000000
            int-sum-selector 3 15
            integral 16777216:1 :1 -14 400
            """.ReplaceLineEndings("\n"), output);
    }

    private static string TruncProgram(string init) => $$"""
        using System;
        public class P
        {
            public static void Main()
            {
                float zero = 0f;
                float value = {{init}};
                Console.WriteLine("before");
                Console.WriteLine((int)value);
            }
        }
        """;

    // f32 → i32 の範囲は [-2^31, 2^31) (il-spec §5)。範囲外と NaN は fault
    [CTheory]
    [InlineData("-2147483904f")]
    [InlineData("2147483648f")]
    [InlineData("zero / zero")]
    [InlineData("1f / zero")]
    [InlineData("-1f / zero")]
    public void FloatToInt_OutOfRange_FaultsInC(string init)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Backends.RunC([TruncProgram(init)], "P"));
        Assert.Contains("float-to-int", error.Message);
        Assert.Contains("stdout:\nbefore", error.Message.ReplaceLineEndings("\n"));
    }

    [CTheory]
    [InlineData("-2147483648f", "-2147483648")]
    [InlineData("-2147483648.5f", "-2147483648")]
    [InlineData("2147483520f", "2147483520")]
    public void FloatToInt_Boundary_ConvertsInC(string init, string expected)
    {
        Assert.Equal("before\n" + expected, Backends.RunC([TruncProgram(init)], "P"));
    }

    private static string SumProgram(string sum) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        public class P
        {
            public static void Main()
            {
                Console.WriteLine("before");
                Console.WriteLine({{sum}});
                Console.WriteLine("after");
            }
        }
        """;

    // CoreCLR は "before" の後に OverflowException で停止する
    [CTheory]
    [InlineData("new List<int> { 2147483647, 1 }.Sum()")]
    [InlineData("new List<int> { -2147483647 - 1, -1 }.Sum()")]
    [InlineData("new List<int> { 2147483647, 1, -1 }.Sum(x => x)")]
    public void IntSum_Overflow_FaultsAfterPrecedingOutput(string sum)
    {
        Assert.Equal("before", Backends.AssertFaultParity(SumProgram(sum), "P"));
    }

    [CFact]
    public void IntSum_WithinRange_MatchDotnet()
    {
        var output = Backends.AssertParity(SumProgram(
            "new List<int> { 2147483647, -1, 1 }.Sum() + \" \" + " +
            "new List<int> { -2147483647 - 1, 1 }.Sum(x => x) + \" \" + " +
            "new List<int>().Sum() + \" \" + new List<float> { 1.5f, 2f }.Sum() + \" \" + " +
            "new List<int> { 2147483647, 2147483647 }.Sum(x => x / 2)"), "P");
        Assert.Equal("before\n2147483647 -2147483647 0 3.5 2147483646\nafter", output);
    }
}

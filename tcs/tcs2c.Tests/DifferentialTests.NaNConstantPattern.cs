namespace TinyCs.Tcs2c.Tests;

// float の NaN の定数のパターンは NaN に一致する (C# は定数のパターンを object.Equals で比べる)。
// is、switch 式、switch 文、not / or、property パターンの中、float? の入力で同じ。== は NaN どうしでも false のまま
public partial class DifferentialTests
{
    [CFact]
    public void Pattern_NaNConstantMatchesNaN()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box { public float V; }
            public class P
            {
                const float N = 0f / 0f;
                static int calls;

                static float Next()
                {
                    calls++;
                    return float.NaN;
                }

                static string Arm(float x) => x switch { N => "nan", 0f => "zero", _ => "other" };

                static string Stat(float x)
                {
                    switch (x)
                    {
                        case N: return "nan";
                        case 1f: return "one";
                        default: return "other";
                    }
                }

                static string NullableArm(float? x) => x switch { N => "nan", null => "null", _ => "other" };

                public static void Main()
                {
                    float x = float.NaN;
                    float y = 1.5f;
                    float? nx = float.NaN;
                    float? none = null;
                    Console.WriteLine(x is (0f / 0f));
                    Console.WriteLine(x is N);
                    Console.WriteLine(y is N);
                    Console.WriteLine(x is not N);
                    Console.WriteLine(y is not (0f / 0f));
                    Console.WriteLine(x is 1f or N);
                    Console.WriteLine(x == N);
                    Console.WriteLine(nx is N);
                    Console.WriteLine(none is N);
                    Console.WriteLine(new Box { V = float.NaN } is { V: N });
                    Console.WriteLine(new Box { V = 2f } is { V: N });
                    Console.WriteLine(Next() is N);
                    Console.WriteLine(calls);
                    Console.WriteLine(Arm(x) + " " + Arm(0f) + " " + Arm(-0f) + " " + Arm(y));
                    Console.WriteLine(Stat(x) + " " + Stat(1f) + " " + Stat(y));
                    Console.WriteLine(NullableArm(nx) + " " + NullableArm(none) + " " + NullableArm(y));
                    int k;
                    switch (N)
                    {
                        case N: k = 1; break;
                    }
                    Console.WriteLine(k);
                }
            }
            """, "P");
        Assert.Equal(string.Join("\n",
            "True", "True", "False", "False", "True", "True", "False", "True", "False", "True", "False", "True", "1",
            "nan zero zero other", "nan one other", "nan null other", "1"), output);
    }
}

// Checks that the results of Math.* are bit-for-bit identical across OSes.
// Hashes the bit patterns of the results for a fixed input sequence per function, and compares them with the expected values.
// There is one set of expected values, not one per OS.
// Asin / Acos / Atan are missing from TinySystem and tcs cannot compile them, so they are not checked.
// For the tcs2c path, tests/c/tcs_math.cs computes C# Math with the same input sequence and hash, and scripts/verify-tcs-binding.sh compares it with this table.
// For the .NET path, the test runner (tests/lub-test) runs this test as a runtime test of .NET execution (-p:Tests=true of dotnet/SampleRunner).
using System;
using Lub;

public static class TestMathDeterminism
{
    const int points = 2000;
    static int state;

    // "name = hash", one function per line.
    // scripts/verify-tcs-binding.sh can read it with sed -nE 's/^ *"([a-z0-9]+ = -?[0-9]+)",$/\1/p'
    static readonly string[] expected = new string[]
    {
        "sin = 720166134",
        "cos = 1865121772",
        "tan = 282405857",
        "atan2 = 1882406979",
        "exp = 153003627",
        "log = -625845371",
        "log2 = 146272290",
        "log10 = -686972693",
        "logb = 1222978674",
        "pow = -16876670",
        "pow2 = -884350922",
    };

    // Makes a value in [lo, hi) from the upper 24 bits of a linear congruential generator (wrapping at 32 bits)
    static float Next(float lo, float hi)
    {
        state = state * 1664525 + 1013904223;
        return lo + (hi - lo) * (((state >> 8) & 0xffffff) / 16777216f);
    }

    // The bit pattern of IEEE-754 binary32.
    // NaN differs by CPU, so all NaNs are folded into one.
    // Doubling and halving need no rounding, so shift until the mantissa is in [2^23, 2^24) and count the exponent.
    static int Bits(float x)
    {
        // Only NaN is not equal to itself (TinyC# has no float.IsNaN)
#pragma warning disable CS1718
        if (x != x) return 0x7fc00000;
#pragma warning restore CS1718
        int sign = x < 0 || (x == 0 && 1 / x < 0) ? -2147483647 - 1 : 0;
        float a = Math.Abs(x);
        if (a == 0) return sign;
        if (a > 3.4028235e38f) return sign | 0x7f800000;
        int e = 150;
        while (a >= 16777216f) { a *= 0.5f; e++; }
        while (a < 8388608f && e > 1) { a *= 2f; e--; }
        if (a < 8388608f) return sign | (int)a;
        return sign | (e << 23) | ((int)a - 8388608);
    }

    static float Apply(string name, float a, float b)
    {
        switch (name)
        {
            case "sin": return (float)Math.Sin(a);
            case "cos": return (float)Math.Cos(a);
            case "tan": return (float)Math.Tan(a);
            case "atan2": return (float)Math.Atan2(a, b);
            case "exp": return (float)Math.Exp(a);
            case "log": return (float)Math.Log(a);
            case "log2": return (float)Math.Log(a, 2f);
            case "log10": return (float)Math.Log(a, 10f);
            case "logb": return (float)Math.Log(a, 3f);
            case "pow2": return (float)Math.Pow(a, 2f);
            default: return (float)Math.Pow(a, b);
        }
    }

    // 1-argument functions list the range of a, and 2-argument functions the ranges of a and b
    static void Digest(string name, float[] ranges, bool binary)
    {
        state = 1;
        int h = -2128831035;
        int step = binary ? 4 : 2;
        for (int r = 0; r < ranges.Length; r += step)
        {
            for (int i = 0; i < points; i++)
            {
                float a = Next(ranges[r], ranges[r + 1]);
                float b = binary ? Next(ranges[r + 2], ranges[r + 3]) : 0f;
                h = (h ^ Bits(Apply(name, a, b))) * 16777619;
            }
        }
        var got = name + " = " + h;
        var found = false;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] == got) found = true;
        }
        Check.That(found, "math digest differs: " + got);
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var trig = new float[] { -4f, 4f, -1e4f, 1e4f, -1e30f, 1e30f };
        var logs = new float[] { 0f, 4f, 0f, 1e30f };
        Digest("sin", trig, false);
        Digest("cos", trig, false);
        Digest("tan", trig, false);
        Digest("atan2", new float[] { -4f, 4f, -4f, 4f, -1e6f, 1e6f, -1f, 1f }, true);
        Digest("exp", new float[] { -1f, 1f, -110f, 110f }, false);
        Digest("log", logs, false);
        Digest("log2", logs, false);
        Digest("log10", logs, false);
        Digest("logb", logs, false);
        Digest("pow", new float[] { 0f, 4f, -30f, 30f, -4f, 4f, -4f, 4f }, true);
        Digest("pow2", new float[] { -1e-19f, 1e-19f }, false);
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

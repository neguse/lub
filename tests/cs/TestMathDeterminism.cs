// Math.* の結果が OS をまたいでビット単位で同じことを検査する。
// 決まった入力列の結果の bit 列を関数ごとに hash し、期待値と比べる。期待値は 1 つで、OS ごとに分けない。
// TinySystem に無い Asin / Acos / Atan は tcs が変換できないので Lua の経路では見ない。
// tcs2c の経路は tests/c/tcs_math.cs が同じ入力列と hash で C# の Math を計算し、scripts/verify-tcs-binding.sh がこの表と比べる。
using System;
using Lub;

public static class TestMathDeterminism
{
    const int points = 2000;
    static int state;

    // 1 行 1 関数の "名前 = hash"。scripts/verify-tcs-binding.sh は
    // sed -nE 's/^ *"([a-z0-9]+ = -?[0-9]+)",$/\1/p' で読める
    static readonly string[] expected = new string[]
    {
        "sin = -2045485879",
        "cos = 1865121772",
        "tan = 1184300740",
        "atan2 = -1928894225",
        "exp = 153003627",
        "log = -625845371",
        "log2 = 146272290",
        "log10 = 306092410",
        "logb = 1222978674",
        "pow = -16876670",
        "pow2 = -884350922",
    };

    // 線形合同法 (32bit で wrap する) の上位 24 bit から [lo, hi) の値を作る
    static float Next(float lo, float hi)
    {
        state = state * 1664525 + 1013904223;
        return lo + (hi - lo) * (((state >> 8) & 0xffffff) / 16777216f);
    }

    // IEEE-754 binary32 の bit 列。NaN は CPU で違うので 1 つにまとめる。
    // 2 倍と半分は丸めずに済むので、仮数が [2^23, 2^24) に入るまで寄せて指数を数える。
    static int Bits(float x)
    {
        if (x != x) return 0x7fc00000;
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

    // 1 引数の関数は a の範囲、2 引数の関数は a と b の範囲を並べる
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

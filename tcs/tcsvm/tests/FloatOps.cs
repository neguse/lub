using System;

// f32 の演算と比較 (il-spec §6): 演算ごとの丸め、-0、比較、int との混在
public class FloatOps
{
    static float Half(float x)
    {
        return x / 2.0f;
    }

    public static void Main()
    {
        float a = 0.1f;
        float b = 0.2f;
        Console.WriteLine(a + b);
        Console.WriteLine(a * b - 0.02f);
        Console.WriteLine(1.0f / 3.0f);
        Console.WriteLine(-0.0f);
        Console.WriteLine(1e20f * 1e20f);
        Console.WriteLine(Half(7));
        int n = 3;
        Console.WriteLine(n * 0.5f);
        Console.WriteLine(n / 2);
        Console.WriteLine(a < b);
        Console.WriteLine(a >= b);
        Console.WriteLine(a == 0.1f);
        Console.WriteLine(a != b);
        float s = 0.0f;
        for (int i = 1; i <= 1000; i++)
            s = s + 1.0f / i;
        Console.WriteLine(s);
        Console.WriteLine(123456789.0f);
        Console.WriteLine(0.0001f);
    }
}

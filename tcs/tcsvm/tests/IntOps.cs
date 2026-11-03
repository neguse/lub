using System;

// i32 の演算 (il-spec §5): wrap、0 方向の除算と剰余、シフトの count & 31、算術右シフト、bit 演算
public class IntOps
{
    static int Mix(int a, int b)
    {
        return a * 31 + b;
    }

    public static void Main()
    {
        int big = 2147483647;
        Console.WriteLine(big + 1);
        Console.WriteLine(-big - 2);
        Console.WriteLine(big * 3);
        Console.WriteLine(-7 / 2);
        Console.WriteLine(-7 % 2);
        Console.WriteLine(7 % -3);
        Console.WriteLine(-8 >> 1);
        Console.WriteLine(1 << 33);
        Console.WriteLine(5 & 3);
        Console.WriteLine(5 | 3);
        Console.WriteLine(5 ^ 3);
        Console.WriteLine(~5);
        int h = 17;
        for (int i = 0; i < 100; i++)
            h = Mix(h, i) ^ (h >> 7);
        Console.WriteLine(h);
        float f = -3.75f;
        Console.WriteLine((int)f);
        Console.WriteLine((int)(f * -2.0f));
    }
}

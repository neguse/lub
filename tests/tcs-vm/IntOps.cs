using System;

// i32 operations (il-spec §5): wrap, division and remainder toward 0, shift count & 31, arithmetic right shift, bit operations
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

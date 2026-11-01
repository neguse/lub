using System;

// 制御: if/else 連鎖、while、do-while、break / continue、論理演算の短絡、再帰、bool
public class Control
{
    static int calls = 0;

    static bool Touch(bool v)
    {
        calls = calls + 1;
        return v;
    }

    static int Fib(int n)
    {
        if (n < 2) return n;
        return Fib(n - 1) + Fib(n - 2);
    }

    static int Classify(int x)
    {
        if (x < 0) return -1;
        else if (x == 0) return 0;
        else if (x < 10) return 1;
        return 2;
    }

    public static void Main()
    {
        Console.WriteLine(Fib(20));
        Console.WriteLine(Classify(-5) + Classify(0) * 10 + Classify(3) * 100 + Classify(42) * 1000);
        int i = 0;
        int sum = 0;
        while (true)
        {
            i = i + 1;
            if (i % 3 == 0) continue;
            if (i > 20) break;
            sum = sum + i;
        }
        Console.WriteLine(sum);
        int k = 10;
        do
        {
            k = k - 3;
        } while (k > 0);
        Console.WriteLine(k);
        bool t = Touch(false) && Touch(true);
        bool u = Touch(true) || Touch(false);
        Console.WriteLine(t);
        Console.WriteLine(u);
        Console.WriteLine(calls);
        bool flag = !t && (u || t);
        Console.WriteLine(flag);
        for (int j = 0; j < 5; j++)
        {
            j = j + 1;
            Console.WriteLine(j);
        }
    }
}

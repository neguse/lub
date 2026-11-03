using System;

// 再帰呼び出しの速さ。fib(N) を素朴な二重再帰で計算する
public class Fib
{
    static int Calc(int n)
    {
        if (n < 2) return n;
        return Calc(n - 1) + Calc(n - 2);
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 30 : int.Parse(s);
        Console.WriteLine("fib(" + n + ") = " + Calc(n));
    }
}

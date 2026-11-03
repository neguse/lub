using System;

// 再帰呼び出しの速さ。fib(N) を素朴な二重再帰で計算する
public class Fib
{
    static int Calc(int n)
    {
        if (n < 2) return n;
        return Calc(n - 1) + Calc(n - 2);
    }

    static int Run(int n, bool print)
    {
        int f = Calc(n);
        if (print) Console.WriteLine("fib(" + n + ") = " + f);
        return f;
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 30 : int.Parse(s);
        // BENCH_REPS 回くり返す (run.py が 1 回あたりの時間を出すのに使う)。
        // 次の回の入力を前の回の結果に依存させ、compiler が回を省いたり外に出したりできないようにする (結果が -1 になることは無い)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}

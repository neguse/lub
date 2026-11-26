using System;

// Speed of recursive calls. Computes fib(N) with naive double recursion
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
        // Repeats BENCH_REPS times (run.py uses this to compute the time per repetition).
        // Each repetition's input depends on the previous result so the compiler cannot skip repetitions or hoist them out (the result is never -1)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}

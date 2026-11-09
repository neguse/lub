using System;

// 内側のループの float の演算と分岐。The Computer Language Benchmarks Game の mandelbrot と同じ範囲と反復 (50 回) を float (f32) で数える
public class Mandelbrot
{
    static int Run(int n, bool print)
    {
        int inside = 0;
        for (int y = 0; y < n; y++)
        {
            float ci = 2.0f * y / n - 1.0f;
            for (int x = 0; x < n; x++)
            {
                float cr = 2.0f * x / n - 1.5f;
                float zr = 0.0f;
                float zi = 0.0f;
                float tr = 0.0f;
                float ti = 0.0f;
                int i = 0;
                while (i < 50 && tr + ti <= 4.0f)
                {
                    zi = 2.0f * zr * zi + ci;
                    zr = tr - ti + cr;
                    tr = zr * zr;
                    ti = zi * zi;
                    i++;
                }
                if (tr + ti <= 4.0f) inside++;
            }
        }
        if (print) Console.WriteLine("inside: " + inside + " of " + n * n);
        return inside;
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 200 : int.Parse(s);
        // BENCH_REPS 回くり返す (run.py が 1 回あたりの時間を出すのに使う)。
        // 次の回の入力を前の回の結果に依存させ、compiler が回を省いたり外に出したりできないようにする (結果が -1 になることは無い)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}

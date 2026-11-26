using System;

// Float arithmetic and branches in the inner loop. Counts over the same range and iterations (50) as mandelbrot of The Computer Language Benchmarks Game, in float (f32)
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
        // Repeats BENCH_REPS times (run.py uses this to compute the time per repetition).
        // Each repetition's input depends on the previous result so the compiler cannot skip repetitions or hoist them out (the result is never -1)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}

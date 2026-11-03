using System;

// 内側のループの float の演算と分岐。The Computer Language Benchmarks Game の mandelbrot と同じ範囲と反復 (50 回) を float (f32) で数える
public class Mandelbrot
{
    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 200 : int.Parse(s);
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
        Console.WriteLine("inside: " + inside + " of " + n * n);
    }
}

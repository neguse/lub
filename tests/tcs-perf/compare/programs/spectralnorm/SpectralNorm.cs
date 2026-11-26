using System;

// Float multiply-add in a double loop and int division. Computes spectral-norm of The Computer Language Benchmarks Game in float (f32)
public class SpectralNorm
{
    static float A(int i, int j)
    {
        int ij = i + j;
        return 1.0f / (ij * (ij + 1) / 2 + i + 1);
    }

    static void MultiplyAv(int n, float[] v, float[] av)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = 0.0f;
            for (int j = 0; j < n; j++) sum = sum + A(i, j) * v[j];
            av[i] = sum;
        }
    }

    static void MultiplyAtv(int n, float[] v, float[] atv)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = 0.0f;
            for (int j = 0; j < n; j++) sum = sum + A(j, i) * v[j];
            atv[i] = sum;
        }
    }

    static void MultiplyAtAv(int n, float[] v, float[] tmp, float[] atav)
    {
        MultiplyAv(n, v, tmp);
        MultiplyAtv(n, tmp, atav);
    }

    static int Run(int n, bool print)
    {
        var u = new float[n];
        var v = new float[n];
        var tmp = new float[n];
        for (int i = 0; i < n; i++) u[i] = 1.0f;
        for (int k = 0; k < 10; k++)
        {
            MultiplyAtAv(n, u, tmp, v);
            MultiplyAtAv(n, v, tmp, u);
        }
        float vBv = 0.0f;
        float vv = 0.0f;
        for (int i = 0; i < n; i++)
        {
            vBv = vBv + u[i] * v[i];
            vv = vv + v[i] * v[i];
        }
        int result = (int)((float)Math.Sqrt(vBv / vv) * 1000000.0f);
        if (print) Console.WriteLine("spectral norm x 1e6: " + result);
        return result;
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 100 : int.Parse(s);
        // Repeats BENCH_REPS times (run.py uses this to compute the time per repetition).
        // Each repetition's input depends on the previous result so the compiler cannot skip repetitions or hoist them out (the result is never -1)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}

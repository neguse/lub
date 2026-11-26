using System;

// A typical game workload: applies gravity, bouncing, and a pull toward the center to particles in an array of structs. BENCH_N is the number of frames
public struct Particle
{
    public float Px, Py, Vx, Vy;
}

public class Particles
{
    static int state = 12345;

    static int Lcg()
    {
        state = (state * 1103515245 + 12345) & 0x3FFFFFFF;
        return state;
    }

    static float Frand(float lo, float hi)
    {
        return lo + (Lcg() % 1000) * (1.0f / 1000.0f) * (hi - lo);
    }

    static int Run(int frames, bool print)
    {
        state = 12345;
        int n = 4096;
        float dt = 0.02f;

        var ps = new Particle[n];
        for (int i = 0; i < n; i++)
        {
            ps[i].Px = Frand(100.0f, 300.0f);
            ps[i].Py = Frand(50.0f, 200.0f);
            ps[i].Vx = Frand(-40.0f, 40.0f);
            ps[i].Vy = Frand(-40.0f, 40.0f);
        }

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < n; i++)
            {
                ps[i].Vy = ps[i].Vy + 98.0f * dt;
                ps[i].Px = ps[i].Px + ps[i].Vx * dt;
                ps[i].Py = ps[i].Py + ps[i].Vy * dt;
                if (ps[i].Px < 0.0f) { ps[i].Px = 0.0f; ps[i].Vx = -ps[i].Vx * 0.9f; }
                if (ps[i].Px > 400.0f) { ps[i].Px = 400.0f; ps[i].Vx = -ps[i].Vx * 0.9f; }
                if (ps[i].Py < 0.0f) { ps[i].Py = 0.0f; ps[i].Vy = -ps[i].Vy * 0.9f; }
                if (ps[i].Py > 240.0f) { ps[i].Py = 240.0f; ps[i].Vy = -ps[i].Vy * 0.9f; }
                float dx = ps[i].Px - 200.0f;
                float dy = ps[i].Py - 120.0f;
                float d2 = dx * dx + dy * dy;
                if (d2 < 400.0f)
                {
                    ps[i].Px = ps[i].Px + dx * 0.1f;
                    ps[i].Py = ps[i].Py + dy * 0.1f;
                }
            }
        }

        float sx = 0.0f;
        float sy = 0.0f;
        for (int i = 0; i < n; i++)
        {
            sx = sx + ps[i].Px;
            sy = sy + ps[i].Py;
        }
        if (print) Console.WriteLine("sum x: " + (int)sx + ", sum y: " + (int)sy);
        return (int)sx + (int)sy;
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

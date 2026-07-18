using System;

// digest kernel: particles の struct 版 (M5 / T219)。演算列は particles.cs
// (SoA 配列版) と同一で、digest も一致すべき。
public struct Particle
{
    public float Px;
    public float Py;
    public float Vx;
    public float Vy;
}

public class ParticlesStruct
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

    public static void Main()
    {
        int n = 4096;
        int frames = 1000;
        float dt = 1.0f / 50.0f;

        var ps = new Particle[n];
        for (int i = 0; i < n; i++)
        {
            ps[i] = new Particle();
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

        for (int i = 0; i < n; i++)
        {
            Console.WriteLine(ps[i].Px);
            Console.WriteLine(ps[i].Py);
            Console.WriteLine(ps[i].Vx);
            Console.WriteLine(ps[i].Vy);
        }
    }
}

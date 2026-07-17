using System;

// digest kernel: particles (../luo/spike/CONTRACT.md kernel 3)。
// struct は M5 まで未対応のため class + 配列フィールドの SoA 形で書く
// (演算列は CONTRACT と同一)。
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

    public static void Main()
    {
        int n = 4096;
        int frames = 1000;
        float dt = 1.0f / 50.0f;

        var px = new float[n];
        var py = new float[n];
        var vx = new float[n];
        var vy = new float[n];
        for (int i = 0; i < n; i++)
        {
            px[i] = Frand(100.0f, 300.0f);
            py[i] = Frand(50.0f, 200.0f);
            vx[i] = Frand(-40.0f, 40.0f);
            vy[i] = Frand(-40.0f, 40.0f);
        }

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < n; i++)
            {
                vy[i] = vy[i] + 98.0f * dt;
                px[i] = px[i] + vx[i] * dt;
                py[i] = py[i] + vy[i] * dt;
                if (px[i] < 0.0f) { px[i] = 0.0f; vx[i] = -vx[i] * 0.9f; }
                if (px[i] > 400.0f) { px[i] = 400.0f; vx[i] = -vx[i] * 0.9f; }
                if (py[i] < 0.0f) { py[i] = 0.0f; vy[i] = -vy[i] * 0.9f; }
                if (py[i] > 240.0f) { py[i] = 240.0f; vy[i] = -vy[i] * 0.9f; }
                float dx = px[i] - 200.0f;
                float dy = py[i] - 120.0f;
                float d2 = dx * dx + dy * dy;
                if (d2 < 400.0f)
                {
                    px[i] = px[i] + dx * 0.1f;
                    py[i] = py[i] + dy * 0.1f;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            Console.WriteLine(px[i]);
            Console.WriteLine(py[i]);
            Console.WriteLine(vx[i]);
            Console.WriteLine(vy[i]);
        }
    }
}

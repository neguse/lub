using System;

// digest kernel: sprite_update (perf/CONTRACT.md kernel 1)
// 演算列・LCG・定数は CONTRACT と bit 一致必須。digest 対象値を 1 行 1 値で出力。
public class SpriteUpdate
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
        int n = 256;
        int frames = 1000;
        float dt = 1.0f / 50.0f;

        var x = new float[n];
        var y = new float[n];
        var vx = new float[n];
        var vy = new float[n];
        var frame = new int[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = Frand(0.0f, 400.0f);
            y[i] = Frand(0.0f, 240.0f);
            vx[i] = Frand(-60.0f, 60.0f);
            vy[i] = Frand(-60.0f, 60.0f);
            frame[i] = Lcg() % 8;
        }

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < n; i++)
            {
                x[i] = x[i] + vx[i] * dt;
                y[i] = y[i] + vy[i] * dt;
                if (x[i] < 0.0f) { x[i] = 0.0f; vx[i] = -vx[i]; }
                if (x[i] > 400.0f) { x[i] = 400.0f; vx[i] = -vx[i]; }
                if (y[i] < 0.0f) { y[i] = 0.0f; vy[i] = -vy[i]; }
                if (y[i] > 240.0f) { y[i] = 240.0f; vy[i] = -vy[i]; }
                frame[i] = (frame[i] + 1) % 8;
            }
        }

        for (int i = 0; i < n; i++)
        {
            Console.WriteLine(x[i]);
            Console.WriteLine(y[i]);
            Console.WriteLine(vx[i]);
            Console.WriteLine(vy[i]);
        }
    }
}

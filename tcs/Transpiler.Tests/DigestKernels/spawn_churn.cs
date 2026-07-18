using System;

// digest kernel: spawn_churn (perf/CONTRACT.md kernel 2)。
// 解釈 (perf 実装と統一): 開始時に n 体を充填し、毎フレーム
// 「spawn 32 (最古を上書き) → 生存全体を最古→最新順に更新」。
public class Entity
{
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
}

public class SpawnChurn
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
        int n = 1024;
        int frames = 1000;
        int spawn = 32;
        float dt = 1.0f / 50.0f;

        var ring = new Entity[n];
        int head = 0;  // 最古の位置
        int count = 0;
        for (int p = 0; p < n; p++)
        {
            var e0 = new Entity();
            e0.X = Frand(0.0f, 400.0f);
            e0.Y = Frand(0.0f, 240.0f);
            e0.Vx = Frand(-30.0f, 30.0f);
            e0.Vy = Frand(-30.0f, 30.0f);
            ring[p] = e0;
            count = count + 1;
        }

        for (int f = 0; f < frames; f++)
        {
            for (int s = 0; s < spawn; s++)
            {
                var e = new Entity();
                e.X = Frand(0.0f, 400.0f);
                e.Y = Frand(0.0f, 240.0f);
                e.Vx = Frand(-30.0f, 30.0f);
                e.Vy = Frand(-30.0f, 30.0f);
                if (count < n)
                {
                    ring[(head + count) % n] = e;
                    count = count + 1;
                }
                else
                {
                    ring[head] = e;  // 最古を破棄して上書き
                    head = (head + 1) % n;
                }
            }
            for (int k = 0; k < count; k++)
            {
                var e = ring[(head + k) % n];
                e.X = e.X + e.Vx * dt;
                e.Y = e.Y + e.Vy * dt;
                if (e.X < 0.0f) { e.X = 0.0f; e.Vx = -e.Vx; }
                if (e.X > 400.0f) { e.X = 400.0f; e.Vx = -e.Vx; }
                if (e.Y < 0.0f) { e.Y = 0.0f; e.Vy = -e.Vy; }
                if (e.Y > 240.0f) { e.Y = 240.0f; e.Vy = -e.Vy; }
            }
        }

        for (int k = 0; k < count; k++)
        {
            var e = ring[(head + k) % n];
            Console.WriteLine(e.X);
            Console.WriteLine(e.Y);
            Console.WriteLine(e.Vx);
            Console.WriteLine(e.Vy);
        }
    }
}

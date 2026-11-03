using System;
using System.Collections.Generic;

// ゲームの典型: class の sprite を List に持ち、毎フレーム動かして画面端で跳ね返す。BENCH_N はフレーム数
public class Sprite
{
    public float X, Y, Vx, Vy;
    public int Frame;
}

public class Sprites
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

    static void Update(Sprite s, float dt)
    {
        s.X = s.X + s.Vx * dt;
        s.Y = s.Y + s.Vy * dt;
        if (s.X < 0.0f) { s.X = 0.0f; s.Vx = -s.Vx; }
        if (s.X > 400.0f) { s.X = 400.0f; s.Vx = -s.Vx; }
        if (s.Y < 0.0f) { s.Y = 0.0f; s.Vy = -s.Vy; }
        if (s.Y > 240.0f) { s.Y = 240.0f; s.Vy = -s.Vy; }
        s.Frame = (s.Frame + 1) % 8;
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int frames = s == null ? 100 : int.Parse(s);
        int count = 1024;
        float dt = 0.02f;

        var sprites = new List<Sprite>();
        for (int i = 0; i < count; i++)
        {
            var sp = new Sprite();
            sp.X = Frand(0.0f, 400.0f);
            sp.Y = Frand(0.0f, 240.0f);
            sp.Vx = Frand(-60.0f, 60.0f);
            sp.Vy = Frand(-60.0f, 60.0f);
            sp.Frame = Lcg() % 8;
            sprites.Add(sp);
        }

        for (int f = 0; f < frames; f++)
        {
            foreach (var sp in sprites) Update(sp, dt);
        }

        float sx = 0.0f;
        float sy = 0.0f;
        int sf = 0;
        foreach (var sp in sprites)
        {
            sx = sx + sp.X;
            sy = sy + sp.Y;
            sf = sf + sp.Frame;
        }
        Console.WriteLine("sum x: " + (int)sx + ", sum y: " + (int)sy + ", sum frame: " + sf);
    }
}

using Lub;
using Lubx;

// key の違う SpriteBatch を 2 つ描いて終わる。
// 中身の同じ shader を batch ごとに作らないことを、終わりの LUB_GPU_STATS の行の shader の数で見る (expect.txt)。
public static class Game
{
    static SpriteBatch? a;
    static SpriteBatch? b;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        a = new SpriteBatch("vm_sprite_a", 64, 64);
        b = new SpriteBatch("vm_sprite_b", 64, 64);
    }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        a!.Begin();
        a.Rect(0, 0, 8, 8);
        a.Flush();
        b!.Begin();
        b.Rect(8, 8, 8, 8);
        b.Flush();
        Gfx.EndPass();
        frames++;
        if (frames == 3)
            App.Quit();
    }
}

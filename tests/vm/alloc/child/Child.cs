using Lub;
using Lubx;

public static class Child
{
    public static Color Tint;
    public static int[]? Keep;
    static SpriteBatch? sb;
    static Text? text;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        sb ??= new SpriteBatch("alloc_sb", 64, 64);
        text ??= new Text("alloc_text", "samples/data/fonts/MPLUS1p-subset.ttf", 16);
        Keep = new int[8];
        Tint = Color.Rgb(1, 0.5f, 0.25f);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        sb.Begin();
        text.Draw(sb, "abcdefghij", 0, 20, Tint);
        text.Draw(sb, "klmnopqrst", 0, 40, Tint);
        sb.Rect(0, 0, 4, 4, Tint);
        sb.Rect(4, 0, 4, 4, Tint);
        sb.Flush();
        Gfx.EndPass();
        Spin = Quat.Identity();
        Area = new Rect(0, 0, 8, 8);
    }

    public static Quat Spin;
    public static Rect Area;
}

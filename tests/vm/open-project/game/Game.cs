// App.Use で .csproj を開く検査: 開いた app の build を待つ間も開いた側は止まらず、build が終わった frame から app が描く。
// 開いた app のソースを書き換えて保存すると、開いた app だけが hot reload で新しい色を描き、static (数えた frame) は残る。
using System;
using Lub;

public static class Game
{
    static Readback? rb;
    static int frames;
    static bool edited;
    static int blueAtEdit;

    static string ChildDir()
    {
        // GAME_CS は <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/");
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        rb = Gfx.Readback("child");
    }

    public static void OnFrame(float dt)
    {
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        if (rt == null) return;
        var child = App.Use("child", ChildDir() + "Child.csproj");
        App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Gfx.ReadTexture(rb!, rt, frames, out var st, out var bytes, out _, out _, out _, out _, out _, out _, out _);
        frames++;
        if (Sys.Seconds() > 120)
        {
            Console.WriteLine("FAIL the opened app did not draw the edited color");
            App.Quit(1);
            return;
        }
        // 開いた app が描くまで、texture の alpha は 255 にならない
        if (st != Gfx.ReadbackStatus.Ready || bytes!.Get(3) != 255) return;
        int red = bytes.Get(0), green = bytes.Get(1), blue = bytes.Get(2);
        if (!edited && red == 255 && green == 0 && blue >= 3)
        {
            // 置き換える文字列は分けて書き、この行は書き換えない
            var path = ChildDir() + "Child.cs";
            Io.LoadText(path, out var text, out _, out _, out _);
            if (text == null)
            {
                Console.WriteLine("FAIL cannot read " + path);
                App.Quit(1);
                return;
            }
            Io.SaveText(path, text.Replace("{ 1.0f, " + "0.0f, blue", "{ 0.0f, " + "1.0f, blue"));
            edited = true;
            blueAtEdit = blue;
        }
        if (edited && red == 0 && green == 255)
        {
            if (blue < blueAtEdit)
            {
                Console.WriteLine("FAIL the opened app started again: blue " + blue + " after " + blueAtEdit);
                App.Quit(1);
                return;
            }
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }
}

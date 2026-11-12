// lubx の描く API が毎 frame の呼び出しで確保しないことの検査 (Sys.ProfileLines の行ごとの確保で見る)。
// ../child/Child.cs を App.Use で開いて見る。
// 開いた app の OnFrame は毎 frame、Child.cs:20 で配列を作り (確保が付く対照)、Child.cs:21 で Color.Rgb を作り、Child.cs:24 と Child.cs:25 で Text.Draw で 10 文字ずつ描き、Child.cs:26 と Child.cs:27 で SpriteBatch.Rect を描き、Child.cs:30 で Quat、Child.cs:31 で Rect を作る。
// 数え始めてから 4 frame の後に、Child.cs:20 に確保が付き、Child.cs:21、Child.cs:25、Child.cs:27、Child.cs:30、Child.cs:31 には付かないことを見る (glyph と kerning は最初の frame で作り終える)。
// Child.cs:24 と Child.cs:26 は、SpriteBatch が frame の最初にその atlas の texture を宣言し直す分 (Atlas.Ensure) を含むので見ない。
using System;
using Lub;

public static class Game
{
    static int frame;

    static string ChildCsproj()
    {
        // GAME_CS は <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/Child.csproj");
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var rt = Gfx.UseTexture("child", 64, 64, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (rt == null) return;
        var child = App.Use("child", ChildCsproj());
        var ok = App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (Sys.Seconds() > 120)
        {
            Console.WriteLine("FAIL timeout after " + frame + " frames of the opened app");
            App.Quit(1);
            return;
        }
        // build を待つ間は Step が false
        if (!ok) return;
        frame++;
        if (frame == 2)
        {
            Sys.ProfileLines(true, child);
            return;
        }
        if (frame < 6)
            return;
        int control = 0, color = 0, text = 0, rect = 0, quat = 0, area = 0;
        foreach (var p in Sys.TakeProfile(child))
        {
            if (p.Site.EndsWith("Child.cs:20"))
                control = p.Allocs;
            if (p.Site.EndsWith("Child.cs:21"))
                color = p.Allocs;
            if (p.Site.EndsWith("Child.cs:25"))
                text = p.Allocs;
            if (p.Site.EndsWith("Child.cs:27"))
                rect = p.Allocs;
            if (p.Site.EndsWith("Child.cs:30"))
                quat = p.Allocs;
            if (p.Site.EndsWith("Child.cs:31"))
                area = p.Allocs;
        }
        Sys.ProfileLines(false, child);
        var got = "control " + control + ", Color.Rgb " + color + ", Text.Draw " + text + ", SpriteBatch.Rect " + rect
            + ", Quat " + quat + ", Rect " + area;
        if (control == 0 || color != 0 || text != 0 || rect != 0 || quat != 0 || area != 0)
        {
            Console.WriteLine("FAIL allocations over 4 frames: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS " + got);
        App.Quit(0);
    }
}

// lubx の描く API が毎 frame の呼び出しで確保しないことの検査 (Sys.ProfileLines の行ごとの確保で見る)。
// ゲームの OnFrame は毎 frame、Game.cs:20 で配列を作り (確保が付く対照)、Game.cs:21 で Color.Rgb を作り、Game.cs:24 と Game.cs:25 で Text.Draw で 10 文字ずつ描く。
// 数え始めてから 4 frame の後に、Game.cs:20 に確保が付き、Game.cs:21 と Game.cs:25 には付かないことを見る (glyph と kerning は最初の frame で作り終える)。
// Game.cs:24 は、SpriteBatch が frame の最初にその atlas の texture を宣言し直す分 (Atlas.Ensure) を含むので見ない。
using System;
using Lub;

public static class Tool
{
    static int frame;

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame == 2)
        {
            Sys.ProfileLines(true);
            return;
        }
        if (frame < 6)
            return;
        int control = 0, color = 0, text = 0;
        foreach (var p in Sys.TakeProfile())
        {
            if (p.Site.EndsWith("Game.cs:20"))
                control = p.Allocs;
            if (p.Site.EndsWith("Game.cs:21"))
                color = p.Allocs;
            if (p.Site.EndsWith("Game.cs:25"))
                text = p.Allocs;
        }
        Sys.ProfileLines(false);
        var got = "control " + control + ", Color.Rgb " + color + ", Text.Draw " + text;
        if (control == 0 || color != 0 || text != 0)
        {
            Console.WriteLine("FAIL allocations over 4 frames: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS " + got);
        App.Quit(0);
    }
}

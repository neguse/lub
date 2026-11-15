// ゲームの物を指すと、それを描いたコードが What に出る。画面の座標は MainTex の画素に直して引く (web の高 DPI)。
// 物はゲーム (game/Game.cs) が画面の真ん中に描く四角。
//   - 画素の比率が 2 なら、ゲームは画面の 2 倍の画素の texture に描き、画面の真ん中はその texture の真ん中を引く (比率を見なければ 1/4 の所を引いて外れる)
//   - 物を指すと What に「メソッド (file:行)」が内側から並ぶ。何も無い所では空
//   - 中 click するとその行を開く
// frame はゲームが動き始めてから数える。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestWhatis
{
    static int frames;
    static int stage;
    static int nextAt = 25;
    static bool failed;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    static void TestFrame(float dt)
    {
        // 比率は、ゲームの texture が作り直されるよう、指す前に変える
        Lider.PixelRatio = 2;
        // ゲームの build を待つ
        if (failed || GameApp.Frames == 0)
            return;
        frames++;
        if (frames > 400)
        {
            Check(false, "stage " + stage + " did not finish: " + string.Join(" / ", Lider.What));
            return;
        }
        if (frames < nextAt)
            return;
        Gfx.Size(out var w, out var h);
        // ゲームの絵の真ん中 (配置によって、絵は画面の一部になる)
        var picture = Lider.Screens[0].Windows[0].ContentRect;
        var at = picture != null ? new Vec2(picture.X + picture.W * 0.5f, picture.Y + picture.H * 0.5f) : new Vec2(w / 2, h / 2);
        var what = string.Join(" / ", Lider.What);
        if (stage == 0)
        {
            // 指した物のコードを調べたり開いたりできるのは、ゲームを止めている間だけ
            Lider.TogglePause();
            Lider.HoverAt(at.X, at.Y);
            stage = 1;
        }
        else if (stage == 1 && what != "")
        {
            Check(what.StartsWith("Game.Quad (game/Game.cs:"), "what: " + what);
            Check(what.Contains(" / ← Game.OnFrame (game/Game.cs:"), "the caller is missing: " + what);
            // 絵の左上の隅 (物の無い所)
            Lider.HoverAt(at.X - (picture != null ? picture.W : w) * 0.5f + 1, at.Y - (picture != null ? picture.H : h) * 0.5f + 1);
            stage = 2;
            nextAt = frames + 10;
        }
        else if (stage == 2)
        {
            Check(what == "", "the empty place has a what: " + what);
            // 足あとの板が前にあるので、奥のゲームの物を引く中 click にする
            Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 2, X = at.X, Y = at.Y });
            stage = 3;
        }
        else if (stage == 3 && Lider.Work!.Focus != null)
        {
            Check(Lider.Work.Focus.Path.EndsWith("game/Game.cs"), "the click opened " + Lider.Work.Focus.Path);
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

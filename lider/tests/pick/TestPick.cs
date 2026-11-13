// ゲームの物を click すると、それを描いたコードの行を物の横に開くことを確かめる。
// ゲーム (game/Game.cs) は画面の中央に板を 1 枚描く (DrawLine の行の Gfx.Draw)。
// click は lub から届く event と同じに Lider.OnEvent へ流す。
// lider を隠して中央を click し、その click がゲームのもので lider は何も開かないことを見る。
// lider を出し、中央を左 click して、拡大した file が game/Game.cs の DrawLine の行になるまで待つ。
// 板は Quad の個体 (Game.Shown) が自分の Draw で描くので、開くとインスペクタがその個体に絞り込む。
// 次に開いた板を正面 (板が板の奥を隠す所) に置き、中 click で板の奥の物をもう一度引けるか見る (左 click なら板の上のカーソルが動く)。
// 行番号は game/Game.cs の中の位置なので、そこに行を足したら DrawLine を合わせる。
// frame はゲームが動き始めてから数える。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestPick
{
    const int DrawLine = 11;
    static int frames;
    static float waited;
    static int phase;
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
        // ゲームの build を待つ
        if (GameApp.Frames == 0 || failed)
            return;
        frames++;
        Gfx.Size(out var w, out var h);
        if (frames == 3)
        {
            Lider.SetActive(false);
            Click(1, w / 2, h / 2);
        }
        if (frames == 13)
            Lider.SetActive(true);
        if (frames < 23)
        {
            Check(Lider.Work?.Focus == null, "a click while lider was hidden opened " + Lider.Work?.Focus?.Path);
            return;
        }
        if (frames == 23)
            Click(1, w / 2, h / 2);
        if (frames <= 23)
            return;
        waited += dt;
        var focus = Lider.Work?.Focus;
        if (focus == null || !focus.Path.EndsWith("Game.cs"))
        {
            Check(waited < 30, "the drawing code was not opened in 30 seconds");
            return;
        }
        var w0 = Lider.Work!;
        if (phase == 1 && focus.Line == 0)
        {
            Check(waited < 30, "the middle click did not pick through the panel in 30 seconds");
            return;
        }
        Check(focus.Line == DrawLine - 1, "opened line " + (focus.Line + 1) + ", want " + DrawLine);
        if (failed)
            return;
        if (phase == 0)
        {
            var shown = "";
            foreach (var n in GameApp.Inspect("Game", 0, 10))
                if (n.Name == "Shown")
                    shown = n.Id;
            Check(shown != "" && Lider.Inspector.Tree.ScopeId == shown && Lider.Inspector.Tree.ScopeClass == "Quad", "opening did not scope to the drawing quad: " + string.Join("|", Lider.Inspector.Tree.Crumbs()));
            phase = 1;
            waited = 0;
            focus.MoveTo(0, 0);
            Click(2, w / 2, h / 2);
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Click(int button, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = button, X = x, Y = y });
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

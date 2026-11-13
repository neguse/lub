// lider の窓の中をマウスで選ぶ。event は lub から届くものと同じに Lider.OnEvent へ流す。
// 窓を正面 (角度 0) に置くので、画面の中央は窓の中央の行の文字に当たる。Target.cs はどの行も同じ。
//   - 押して下へ動かして離すと、押した所から何行かにわたって選ぶ。離した後に動かしても選びは変わらない
//   - 続けて 2 回押すと語、3 回押すと行を選ぶ
using System;
using Lub;
using LiderEditor;

public static class TestSelect
{
    const string Word = "abcdefghijklmnopqrstuvwxyz";
    static int stage;
    static float next;
    static bool failed;
    static int id;
    // 窓の開く動きの残り。動きは dt で進むので、重い frame があると実時間で待っても終わらない
    static float opening;

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
        opening -= dt;
        if (failed || opening > 0 || Sys.Seconds() < next)
            return;
        // 0.4 秒より間をあけて、前の stage の押しと続けて押したことにしない
        next = Sys.Seconds() + 0.5f;
        Gfx.Size(out var sw, out var sh);
        float cx = sw / 2, cy = sh / 2;
        if (stage == 0)
        {
            var w = Lider.Work;
            var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
            Check(w != null && path != null, "LIDER_TEST_FILE or the workspace is missing");
            if (failed)
                return;
            // 画面の真ん中に窓を置くため、インスペクタはしまう
            Lider.Inspector.Shown = false;
            w!.FocusOn(path!);
            id = w.FocusWindow!.ViewId;
            // 動いている間は、見えている大きさで指す
            opening = 1.5f;
        }
        else if (stage == 1)
        {
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseMotion, cx, cy + 60);
            Mouse(EventKind.MouseButtonUp, cx, cy + 60);
            var s = EditorHost.Selection(id);
            Check(s.Split('\n').Length >= 3, "dragging down selected '" + s + "'");
            Mouse(EventKind.MouseMotion, cx, cy + 120);
            Check(EditorHost.Selection(id) == s, "moving after the release changed the selection to '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 2)
        {
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseButtonUp, cx, cy);
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseButtonUp, cx, cy);
            Check(EditorHost.Selection(id) == Word, "a double click selected '" + EditorHost.Selection(id) + "'");
            Mouse(EventKind.MouseButtonDown, cx, cy);
            Mouse(EventKind.MouseButtonUp, cx, cy);
            var line = "// " + string.Join(" ", new[] { Word, Word, Word, Word, Word, Word, Word, Word });
            var s = EditorHost.Selection(id);
            Check(s == line || s == line + "\n", "a triple click selected '" + s + "'");
        }
        else
        {
            Console.WriteLine("PASS");
            App.Quit(0);
            return;
        }
        if (!failed && EditorHost.LastError() != "")
            Check(false, "editor error: " + EditorHost.LastError());
        stage++;
    }

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 1, X = x, Y = y });
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

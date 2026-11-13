// インスペクタの値が板の幅に収まらないとき。値はゲーム (game/Game.cs) の Stage.ClearTicks。
//   - 板を狭めていっても、描いた値 (InspectRow.Shown) は、値の全体か、値の頭に … を付けた物か、何も無いか (値の左で板が尽きた) のどれか。頭だけを … 無しで出さない (4980 を 49 と出さない)
//   - 狭めていく間に、値の全体を出す幅と、… で切る幅の両方を通る
// frame はゲームが動き始めてから数える。
using System;
using Lub;
using LiderCore;

public static class TestInspectFit
{
    static int frames;
    static bool failed;
    static bool whole;
    static bool cut;

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
        if (GameApp.Frames == 0)
            return;
        frames++;
        if (failed || frames < 20)
            return;
        var tree = Lider.Inspector.Tree;
        if (frames == 20)
        {
            tree.Toggle("Stage");
            return;
        }
        if (frames < 23)
            return;
        InspectRow? row = null;
        foreach (var r in tree.Rows)
            if (r.Key == "Stage.ClearTicks")
                row = r;
        Check(row != null && row.Text == "4980", "the inspector does not show Stage.ClearTicks = 4980");
        if (failed)
            return;
        var shown = row!.Shown.Count > 0 ? row.Shown[0] : "";
        var head = shown.EndsWith("…") ? Utf8.Sub(shown, 0, Utf8.Bytes(shown) - Utf8.Bytes("…")) : "";
        if (shown == "4980")
            whole = true;
        else if (shown.EndsWith("…") && "4980".StartsWith(head))
            cut = true;
        else
            Check(shown == "", "the board " + InspectView.PxW + " px wide drew 4980 as '" + shown + "'");
        // 1 frame ごとに板を 2 px 狭める (Prepare が次の frame に描き直す)
        if (InspectView.PxW > 160)
        {
            InspectView.PxW = Math.Max(160, InspectView.PxW - 2);
            return;
        }
        Check(whole, "the board never drew the whole value");
        Check(cut, "the board never cut the value with …");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
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

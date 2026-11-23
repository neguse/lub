// When an inspector value does not fit in the board width. The value is Stage.ClearTicks of the game (game/Game.cs).
//   - As the board narrows, the drawn value (InspectRow.Shown) is either the whole value, the head of the value followed by …, or nothing (the board ran out left of the value). Only the head is never shown without … (4980 is not shown as 49)
//   - While narrowing, it passes through both a width that shows the whole value and a width that cuts it with …
// Frames are counted from when the game starts running.
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
        // Check in the overlay layout (code windows lined up in a horizontal row on the cylindrical screen in space)
        Lider.Arrange(Lider.Overlay);
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
        // Wait for the game build
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
        // Narrow the board by 2 px every frame (Prepare redraws it on the next frame)
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

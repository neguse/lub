// When the initializer of a game static (game/Target.cs) is removed and saved, lub recreates the game's VM and starts from OnInit (restart). lider's windows, tabs, focus, and cursors are kept across the recreation.
// lider's VM is not recreated, so the windows stay as they were placed.
// The recreation is checked by the game's count (incremented every frame by game/Game.cs) going back to its initial value, and the static whose initializer was removed becoming 0. Values are read through the same path lider uses to look at the game (GameApp.Inspect).
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestRelayout
{
    static bool changed;
    static bool failed;
    static int before;
    static int reloads;
    static float waited;

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
        if (failed)
            return;
        waited += dt;
        var w = Lider.Work!;
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        var root = Environment.GetEnvironmentVariable("LIDER_ROOT")!;
        if (!changed)
        {
            // Wait until the game starts running and the count is far enough from its initial value
            Check(waited < 60, "the game did not start in 60 seconds");
            if (Value("Game", "Count") < 30)
                return;
            w.FocusOn(target);
            w.FocusOn(root + "/Second.cs");
            w.MoveTab(1);
            w.FocusOn(target);
            w.FocusOn(root + "/Third.cs");
            Lider.ShowTab(w.Windows[0], 0);
            w.Focus!.MoveTo(3, 8);
            w.FocusNth(1);
            w.Focus!.MoveTo(3, 4);
            Check(Shape(w) == "[*Target.cs Third.cs] [*Second.cs] focus 1", "the layout before the restart is " + Shape(w));
            Check(Value("Target", "Value") == 1, "the game has the initializer " + Value("Target", "Value") + " before the change, want 1");
            before = Value("Game", "Count");
            reloads = Lider.Reloads;
            // Remove the static's initializer (this recreates the game's VM)
            Io.SaveText(target, "// TestRelayout が開き、static の初期化子を消す file (ゲームの build に入る)。\npublic static class Target\n{\n    public static int Value;\n}\n");
            changed = true;
            waited = 0;
            return;
        }
        var count = Value("Game", "Count");
        if (Lider.Reloads == reloads || count < 0 || count >= before)
        {
            Check(waited < 60, "removing the static initializer did not restart the game: count " + count + " after " + before + ", " + (Lider.Reloads - reloads) + " reloads");
            return;
        }
        Check(Value("Target", "Value") == 0, "the restarted game has the removed initializer: " + Value("Target", "Value"));
        Check(Shape(w) == "[*Target.cs Third.cs] [*Second.cs] focus 1", "after the restart the layout is " + Shape(w));
        var first = w.Windows.Count > 0 ? w.Windows[0].Code.Doc : null;
        Check(first != null && first.Line == 3 && first.Col == 8, "the cursor of Target.cs is not kept");
        Check(w.Focus != null && w.Focus.Line == 3 && w.Focus.Col == 4, "the cursor of Second.cs is not kept");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // Current value of a static of a game class (-1 if it cannot be read)
    static int Value(string type, string name)
    {
        foreach (var n in GameApp.Inspect(type, 0, 100))
            if (n.Name == name && int.TryParse(n.Text, out var value))
                return value;
        return -1;
    }

    // Tab names per window (* on the current tab), and the index of the focused window
    static string Shape(Workspace w)
    {
        var s = "";
        foreach (var window in w.Windows)
        {
            var tabs = new List<string>();
            for (var i = 0; i < window.Code.Tabs.Count; i++)
            {
                var p = window.Code.Tabs[i].Path;
                tabs.Add((i == window.Code.Tab ? "*" : "") + Utf8.Sub(p, Utf8.LastIndexOf(p, "/") + 1));
            }
            s += "[" + string.Join(" ", tabs) + "] ";
        }
        return s + "focus " + (w.FocusWindow != null ? w.Windows.IndexOf(w.FocusWindow) : -1);
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

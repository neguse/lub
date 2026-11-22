// Uses the editor's CommandView (search, go to line) and NagView in lider's window. Keys are fed through Workspace.Key.
//   - Ctrl+F shows the search bar at the bottom of the window, searches for the typed text, and Return selects the match. F3 finds the next one
//   - Ctrl+G goes to a line
//   - Escape while the bar is shown closes the bar, not the window
//   - NagView appears at the top of the window, can be answered with keys, and after answering, typing goes into the text
//   - Clicking a position (uv) on the board moves the cursor before the character there
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestFind
{
    const int Return = 40;
    const int Escape = 41;
    const int Right = 79;
    const int F3 = 60;
    const int F = 9;
    const int G = 10;
    static bool ran;
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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(path != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        w!.FocusOn(path!);
        var doc = w.Focus!;

        Chord(w, Keys.ModCtrl, F);
        EditorHost.Step();
        Check(EditorHost.Modal(), "Ctrl+F did not open the find bar");
        var label = Command(doc, "Find Text: ", 0);
        Check(label >= 0 && EditorHost.CmdLine(label) == -1 && EditorHost.CmdY(label) > PanelView.BasePxH / 2,
            "the find bar is not drawn at the bottom of the panel");
        Type(w, "return");
        Press(w, Return);
        Check(!EditorHost.Modal(), "Return did not close the find bar");
        Check(doc.Line == 5 && EditorHost.Selection(doc.Id) == "return", "found '" + EditorHost.Selection(doc.Id) + "' on line " + doc.Line + ", want 'return' on 5");
        Press(w, F3);
        Check(doc.Line == 10 && EditorHost.Selection(doc.Id) == "return", "F3 found '" + EditorHost.Selection(doc.Id) + "' on line " + doc.Line + ", want 'return' on 10");

        Chord(w, Keys.ModCtrl, G);
        Type(w, "3");
        Press(w, Return);
        Check(doc.Line == 2, "Ctrl+G 3 moved to line " + doc.Line + ", want 2");

        Chord(w, Keys.ModCtrl, F);
        Press(w, Escape);
        Check(!EditorHost.Modal() && w.Windows.Count == 1 && w.Focus == doc, "Escape on the find bar closed the window or left the bar open");

        EditorHost.Ask(doc.Id, "Check", "Answer this?", new System.Collections.Generic.List<string> { "Yes", "No" });
        EditorHost.Step();
        Check(EditorHost.Modal(), "the NagView did not take the keys");
        var message = Command(doc, "Answer this?", 0);
        Check(message >= 0 && EditorHost.CmdLine(message) == -1 && EditorHost.CmdY(message) < PanelView.BasePxH / 2,
            "the NagView is not drawn at the top of the panel");
        Press(w, Right);
        Press(w, Return);
        Check(EditorHost.LastAnswer() == "No", "answered '" + EditorHost.LastAnswer() + "', want 'No'");
        Check(!EditorHost.Modal(), "the NagView kept the keys after the answer");
        doc.MoveTo(0, 0);
        Type(w, "x");
        Check(doc.Text(0).StartsWith("x//"), "typed after the NagView: " + doc.Text(0));

        ClickReturn(doc);

        Press(w, Escape);
        Check(w.Windows.Count == 1, "Escape closed the window");
        Lider.Run("window:close");
        Check(w.Windows.Count == 0, "window:close did not close the window");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // Click near the left edge of the r on line 11 `        return 2;`
    static void ClickReturn(Document doc)
    {
        // Highlighting makes per-word commands, so look at the command for "        return" on line 11, including the leading indentation
        var i = Command(doc, "        return", 11);
        Check(i >= 0, "no text command for line 11");
        if (failed)
            return;
        var cell = EditorHost.CmdW(i) / 14;
        var x = EditorHost.CmdX(i) + cell * 8.25f;
        var y = EditorHost.CmdY(i) + EditorHost.LineHeight(doc.Id) * 0.5f;
        PanelView.Click(doc, new Vec2(x / PanelView.BasePxW, y / PanelView.BasePxH));
        Check(doc.Line == 10 && doc.Col == 8, "clicked at " + doc.Line + ":" + doc.Col + ", want 10:8");
    }

    // Among the commands drawn at the panel size, the command for the characters of text (-1 if none).
    // If line is not 0, only the commands of that line (EditorHost.CmdLine) are looked at
    static int Command(Document doc, string text, int line)
    {
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdText(i) == text && (line == 0 || EditorHost.CmdLine(i) == line))
                return i;
        return -1;
    }

    // Type lowercase letters and digits by pressing keys
    static void Type(Workspace w, string s)
    {
        foreach (var c in Utf8.ByteList(s))
        {
            var key = c >= 'a' && c <= 'z' ? 4 + (c - 'a') : (c == '0' ? 39 : 30 + (c - '1'));
            Press(w, key);
        }
    }

    static void Press(Workspace w, int key)
    {
        w.Key(key, true);
        w.Key(key, false);
    }

    static void Chord(Workspace w, int modifier, int key)
    {
        w.SetMods(modifier);
        Press(w, key);
        w.SetMods(0);
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

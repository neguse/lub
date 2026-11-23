// Feeds lider key operations and checks the lines of the Document that opened Target.cs.
//   - Moving up/down keeps the visual column (full-width characters are 2 columns) and does not stop in the middle of a UTF-8 character
//   - lider's way of counting columns (Utf8.Cells) matches the width the editor draws
//   - Even after passing through a short line, it goes back to the column from before moving up/down.
//     Moving left/right or typing keeps that column
//   - Hiding with F1 while holding Ctrl and then releasing Ctrl does not leave Ctrl held the next time it is shown
//   - Keys typed while hidden do not go into the Document
//   - Characters and the editor's key names are determined by the character lub passes, not by the physical position (JIS layout)
//   - Even if F1 is held down and repeated KeyDowns arrive, it toggles only once
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestKeys
{
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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    // Feed only once on the first frame
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
        var doc = w.Focus;
        Check(doc != null, "cannot open " + path);
        Check(doc!.Find("static int"), "no 'static int' in " + path);
        if (failed)
            return;

        // From column 11 of the ASCII line, go down through the short line `    {` to `        return 1;`
        var line = doc.Line;
        Press(w, Keys.Down);
        Press(w, Keys.Down);
        Check(doc.Line == line + 2 && doc.Col == 11, "Down twice moved to " + doc.Line + ":" + doc.Col + ", want " + (line + 2) + ":11");

        // Go up to the Japanese line and type. Column 11 is after "日本" (full-width characters are 2 columns)
        Press(w, Keys.Up);
        Press(w, Keys.Up);
        Press(w, Keys.Up);
        Press(w, Keys.A);
        Check(doc.Line == line - 1, "Up moved to line " + doc.Line + ", want " + (line - 1));
        Check(doc.Text(doc.Line) == "    // 日本a語のコメント", "typed after Up: " + doc.Text(doc.Line));
        CheckCells(doc, doc.Text(doc.Line), 2);

        // Go down at column 12 after typing, and go up at column 11 after moving left by one
        Press(w, Keys.Down);
        Check(doc.Col == 12, "Down after typing moved to column " + doc.Col + ", want 12");
        Press(w, Keys.Left);
        Press(w, Keys.Up);
        Press(w, Keys.Right);
        Check(doc.Line == line - 1 && doc.Col == 14, "Left, Up, Right moved to " + doc.Line + ":" + doc.Col + ", want " + (line - 1) + ":14");

        // With any physical key, if Mods is Ctrl it works as Ctrl (for setups that map CapsLock to Ctrl)
        var caps = 57;
        Send(EventKind.KeyDown, caps, Keys.ModCtrl);
        Check(w.Ctrl, "Mods did not make Ctrl down");
        Send(EventKind.KeyUp, caps);
        Check(!w.Ctrl, "Mods did not make Ctrl up");

        // Feed them in the order they arrive from lub.
        // The a typed while hidden belongs to the game and does not go into the Document
        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyDown, Keys.LeftCtrl, Keys.ModCtrl);
        Send(EventKind.KeyUp, Keys.LeftCtrl);
        Check(!w.Ctrl, "Ctrl stayed down after it was released while hidden");
        Send(EventKind.KeyUp, Keys.F1);
        Send(EventKind.KeyDown, Keys.A);
        Send(EventKind.KeyUp, Keys.A);
        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyUp, Keys.F1);
        Check(!w.OnGame, "lider is not active after pressing F1 twice");
        Send(EventKind.KeyDown, Keys.A);
        Send(EventKind.KeyUp, Keys.A);
        Check(doc.Text(doc.Line) == "    // 日本aa語のコメント", "typed after F1: " + doc.Text(doc.Line));

        // JIS layout: the US [ position is @, Shift+2 is ", and the ¥ and "ろ" keys. Hankaku/Zenkaku does not type a character
        Type(47, 0, '@', '@');
        Type(31, Keys.ModShift, '"', '2');
        Type(137, 0, '\\', '\\');
        Type(135, Keys.ModShift, '_', '\\');
        Type(53, 0, 0, 0);
        Type(137, 0, 0xA5, 0xA5);
        Check(doc.Text(doc.Line) == "    // 日本aa@\"\\_¥語のコメント", "typed on JIS: " + doc.Text(doc.Line));

        // The keymap is also looked up by logical key: Ctrl with the JIS @ does nothing, and Ctrl with the [ at the US ] position moves to the previous block
        var jisLine = doc.Line;
        Type(47, Keys.ModCtrl, '@', '@');
        Check(doc.Line == jisLine, "Ctrl+@ ran the binding of Ctrl+[: line " + doc.Line);
        var deep = doc.Find("return 1;");
        Check(deep, "no 'return 1;'");
        var bodyLine = doc.Line;
        Type(48, Keys.ModCtrl, '[', '[');
        Check(doc.Line < bodyLine, "Ctrl+[ on JIS did not move from line " + bodyLine);
        Send(EventKind.KeyUp, Keys.LeftCtrl);

        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyUp, Keys.F1);
        Check(w.OnGame, "holding F1 toggled lider more than once");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // lider's columns (Utf8.Cells, the columns where MonoText places characters) match the width the editor drew: width of the full-width line = number of columns x the column width derived from the ASCII line (line asciiLine, 1-based).
    // The ASCII line is split into per-word commands by highlighting, so the widths of the text commands on that line are added up
    static void CheckCells(Document doc, string wide, int asciiLine)
    {
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        var ascii = doc.Text(asciiLine - 1);
        var wideW = -1.0f;
        var asciiW = 0.0f;
        for (var i = 0; i < n; i++)
        {
            if (EditorHost.CmdKind(i) != 2)
                continue;
            if (EditorHost.CmdText(i) == wide)
                wideW = EditorHost.CmdW(i);
            if (EditorHost.CmdLine(i) == asciiLine)
                asciiW += EditorHost.CmdW(i);
        }
        var cell = asciiW / Utf8.Bytes(ascii);
        Check(wideW > 0 && cell > 0 && System.Math.Abs(wideW - Utf8.Cells(wide, Utf8.Bytes(wide)) * cell) < 0.01f,
            "'" + wide + "' is " + wideW + " px wide in the editor, " + Utf8.Cells(wide, Utf8.Bytes(wide)) + " cells of " + cell + " px in lider");
    }

    static void Send(EventKind kind, int key, int mods = 0)
    {
        Lider.OnEvent(new EventData
        {
            Kind = kind, Key = key, Mods = mods,
            Typed = Keys.UsChar(key, (mods & Keys.ModShift) != 0), Plain = Keys.UsChar(key, false),
        });
    }

    static void Type(int key, int mods, int typed, int plain)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = typed, Plain = plain });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = mods, Typed = typed, Plain = plain });
    }

    static void Press(Workspace w, int key)
    {
        w.Key(key, true);
        w.Key(key, false);
    }

    // Report only the first failure and exit
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

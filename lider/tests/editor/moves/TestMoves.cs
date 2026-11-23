// Checks cursor movement and the edit commands of the keymap.
//   - Moving up/down keeps the visual column (full-width characters are 2 columns) and does not stop in the middle of a UTF-8 character. It goes back to the earlier column even after passing through a short line
//   - Moving left/right or typing keeps that column
//   - Ctrl+[ and Ctrl+] move to the ends of blocks separated by blank lines, and Ctrl+Left/Right move to word boundaries
//   - Home moves after the indentation, and pressing it again moves to the start of the line
//   - Return carries over the indentation, and Backspace deletes 4 spaces at once
//   - Tab and Shift+Tab add and remove indentation, and Ctrl+/ toggles line comments
//   - Ctrl+D selects a word, and pressing it again adds a cursor at the next occurrence of the same word (typing goes into both)
//   - Ctrl+X cuts the line and Ctrl+V pastes it
using System;
using Lub;
using LiderEditor;

public static class TestMoves
{
    const string Text = "class A\n{\n    // 日本語のコメント\n    {\n        return 1;\n    }\n\n    int x = 1; int y = x;\n}\n";
    static bool ran;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        Ed.Boot();
        var id = Ed.Open(Ed.Write("Moves.cs", Text));
        // Up from column 11 of line 5 (after "        re"): through the short line "    {" to the same visual column on the full-width line
        EditorHost.SetCursor(id, 5, 11);
        Ed.Press(Ed.Up);
        Ed.CheckCursor(id, 4, 6);
        Ed.Press(Ed.Up);
        // Column 10 is left of the middle of "本" after "    // 日" (9 columns), so it is before "本" (byte position 11)
        Ed.CheckCursor(id, 3, 11);
        Ed.Press(Ed.Down);
        Ed.Press(Ed.Down);
        Ed.CheckCursor(id, 5, 11);
        // Moving left by one gives column 9, which on the line above is right of the middle of "日", so it is again before "本"
        Ed.Press(Ed.Left);
        Ed.Press(Ed.Up);
        Ed.Press(Ed.Up);
        Ed.CheckCursor(id, 3, 11);
        Ed.Press(Ed.Right);
        Ed.CheckCursor(id, 3, 14);
        // Block and word boundaries
        Ed.Ctrl(48);
        Ed.CheckCursor(id, 7, 1);
        EditorHost.SetCursor(id, 9, 1);
        Ed.Ctrl(47);
        Ed.CheckCursor(id, 8, 5);
        Ed.Ctrl(Ed.Right);
        Ed.CheckCursor(id, 8, 8);
        Ed.Ctrl(Ed.Right);
        Ed.CheckCursor(id, 8, 10);
        Ed.Ctrl(Ed.Left);
        Ed.CheckCursor(id, 8, 9);
        Ed.Press(Ed.Home);
        Ed.CheckCursor(id, 8, 5);
        Ed.Press(Ed.Home);
        Ed.CheckCursor(id, 8, 1);
        // Newline that carries over the indentation, and Backspace over 4 spaces
        EditorHost.SetCursor(id, 5, 18);
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Line(id, 6) == "        ", "newline indent: '" + EditorHost.Line(id, 6) + "'");
        Ed.Press(Ed.Backspace);
        Ed.Check(EditorHost.Line(id, 6) == "    ", "backspace in the indent: '" + EditorHost.Line(id, 6) + "'");
        Ed.Type("z");
        Ed.Check(EditorHost.Line(id, 6) == "    z", "typed: '" + EditorHost.Line(id, 6) + "'");
        Ed.Press(Ed.Tab);
        Ed.Check(EditorHost.Line(id, 6) == "    z    ", "tab after text: '" + EditorHost.Line(id, 6) + "'");
        Ed.Shift(Ed.Tab);
        Ed.Check(EditorHost.Line(id, 6) == "z    ", "shift+tab: '" + EditorHost.Line(id, 6) + "'");
        Ed.Ctrl(56);
        Ed.Check(EditorHost.Line(id, 6) == "// z    ", "ctrl+/: '" + EditorHost.Line(id, 6) + "'");
        Ed.Ctrl(56);
        Ed.Check(EditorHost.Line(id, 6) == "z    ", "ctrl+/ again: '" + EditorHost.Line(id, 6) + "'");
        // Ctrl+D: select x and add the next x
        EditorHost.SetCursor(id, 9, 9);
        Ed.Ctrl(7);
        Ed.Check(EditorHost.Selection(id) == "x", "ctrl+d selected '" + EditorHost.Selection(id) + "'");
        Ed.Ctrl(7);
        Ed.Type("q");
        Ed.Check(EditorHost.Line(id, 9) == "    int q = 1; int y = q;", "ctrl+d twice and typing: " + EditorHost.Line(id, 9));
        Ed.Press(Ed.Escape);
        EditorHost.Draw(id, 640, 360);
        Ed.Check(EditorHost.CaretCount(id) == 1, "escape kept " + EditorHost.CaretCount(id) + " cursors");
        // Cut and paste a line
        var lines = EditorHost.LineCount(id);
        EditorHost.SetCursor(id, 1, 3);
        Ed.Ctrl(27);
        Ed.Check(EditorHost.LineCount(id) == lines - 1 && EditorHost.Line(id, 1) == "{", "ctrl+x cut the line: " + EditorHost.Line(id, 1));
        Ed.Ctrl(25);
        Ed.Check(EditorHost.Line(id, 1) == "class A" && EditorHost.Line(id, 2) == "{", "ctrl+v pasted the line above: " + EditorHost.Line(id, 1));
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

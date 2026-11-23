// Selects by pressing the mouse inside the view.
//   - The cursor moves before the character at the pressed point (after it, on the right half of the character)
//   - Pressing, moving, and releasing selects from the pressed point to there. Moving after releasing does not change it
//   - Pressing twice in a row selects a word, and three times selects a line
//   - Pressing the gutter moves to the start of the line
//   - The scroll position goes into LineY
using System;
using Lub;
using LiderEditor;

public static class TestMouse
{
    const string Word = "abcdefghijklmnopqrstuvwxyz";
    static int stage;
    static float next;
    static int id;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    // px of the point dx columns from the left edge of the character at column col of line line (1-based, on an ASCII line)
    static float X(int col, float dx)
    {
        return EditorHost.TextX(id) + (col - 1 + dx) * Ed.CellW;
    }

    static float Y(int line)
    {
        return EditorHost.LineY(id, line) + Ed.LineH * 0.5f;
    }

    public static void OnFrame(float dt)
    {
        if (Ed.Failed || Sys.Seconds() < next)
            return;
        // Wait longer than 0.4 seconds so it does not count as a press in a row with the press of the previous stage
        next = Sys.Seconds() + 0.5f;
        if (stage == 0)
        {
            Ed.Boot();
            var text = "";
            for (var i = 0; i < 40; i++)
                text += "// " + Word + " " + Word + "\n";
            id = Ed.Open(Ed.Write("Mouse.cs", text));
            EditorHost.Click(id, X(5, 0.25f), Y(3));
            Ed.CheckCursor(id, 3, 5);
            EditorHost.Click(id, X(5, 0.75f), Y(4));
            Ed.CheckCursor(id, 4, 6);
            // gutter
            EditorHost.Click(id, 5, Y(6));
            Ed.CheckCursor(id, 6, 1);
        }
        else if (stage == 1)
        {
            EditorHost.Press(id, X(4, 0), Y(2));
            EditorHost.Drag(id, X(8, 0), Y(4));
            EditorHost.Release(id, X(8, 0), Y(4));
            var s = EditorHost.Selection(id);
            Ed.Check(s == Word + " " + Word + "\n// " + Word + " " + Word + "\n// " + "abcd", "dragging selected '" + s + "'");
            EditorHost.Drag(id, X(8, 0), Y(9));
            Ed.Check(EditorHost.Selection(id) == s, "moving after the release changed the selection");
        }
        else if (stage == 2)
        {
            EditorHost.Click(id, X(10, 0), Y(2));
            EditorHost.Click(id, X(10, 0), Y(2));
            Ed.Check(EditorHost.Selection(id) == Word, "a double click selected '" + EditorHost.Selection(id) + "'");
            EditorHost.Click(id, X(10, 0), Y(2));
            Ed.Check(EditorHost.Selection(id) == "// " + Word + " " + Word + "\n", "a triple click selected '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 3)
        {
            // Line 11 comes below the top margin (y = 7). Line 10, half visible in the top margin, also counts as a visible line (same as Lite XL's get_visible_line_range)
            EditorHost.ScrollToLine(id, 11);
            Ed.Check(EditorHost.FirstLine(id) == 10 && EditorHost.LineY(id, 11) == 7, "ScrollToLine 11: first line " + EditorHost.FirstLine(id) + " at " + EditorHost.LineY(id, 11));
            EditorHost.Click(id, X(1, 0), Y(12));
            Ed.CheckCursor(id, 12, 1);
            // Pressing and calling Step does not move the visible area
            EditorHost.Step();
            Ed.Check(EditorHost.FirstLine(id) == 10, "the click scrolled to line " + EditorHost.FirstLine(id));
            Ed.Pass();
            return;
        }
        stage++;
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

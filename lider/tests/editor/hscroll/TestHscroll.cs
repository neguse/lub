// Horizontal scrolling.
//   - When the cursor goes past the right side of the window, scroll horizontally until the cursor is visible
//   - When the cursor comes back to where it is visible without scrolling, scroll back to the left edge so the start of lines is not cut off
using System;
using Lub;
using LiderEditor;

public static class TestHscroll
{
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
        var text = "using System;\nclass A\n{\n    // xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n    int x = 1;\n}\n";
        var id = Ed.Open(Ed.Write("Hscroll.cs", text));
        var left = EditorHost.TextX(id);
        // To the end of line 4 (a line longer than the window)
        EditorHost.SetCursor(id, 4, 78);
        EditorHost.Step();
        EditorHost.Draw(id, 640, 360);
        Ed.Check(EditorHost.TextX(id) < left, "the end of the long line did not scroll: text at " + EditorHost.TextX(id) + ", left " + left);
        // To the end of the short line below (column 9): visible without scrolling
        Ed.Press(Ed.Down);
        EditorHost.Step();
        EditorHost.Draw(id, 640, 360);
        Ed.CheckCursor(id, 5, 15);
        Ed.Check(EditorHost.TextX(id) == left, "the view stayed scrolled on a short line: text at " + EditorHost.TextX(id) + ", want " + left);
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

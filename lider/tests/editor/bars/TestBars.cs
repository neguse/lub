// Checks the bar at the bottom of the window (CommandView) and the bar at the top (NagView).
//   - Ctrl+F shows the search bar at the bottom of the window, searches for the typed text, and Return selects the match. F3 finds the next one, Shift+F3 the previous one
//   - Escape in the bar closes the bar and restores the selection from where the search started
//   - Ctrl+G goes to a line
//   - Ctrl+R replaces (everything if nothing is selected)
//   - NagView appears at the top of the window, an answer is chosen with left/right and given with Return, and y and n also answer. After answering, typing goes into the text
//   - Ctrl+N opens a doc without a name (TakeOpened), and Ctrl+S asks for a name and saves
//   - Ctrl+O opens a file by typing its path, and Ctrl+P opens one by typing part of its name
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

public static class TestBars
{
    const string Text = "class A\n{\n    int F()\n    {\n        return 1;\n    }\n    int G()\n    {\n        return 2;\n    }\n}\n";
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
        var path = Ed.Write("Bars.cs", Text);
        Ed.Write("other.cs", "// other\npublic static class Other\n{\n}\n");
        var id = Ed.Open(path);

        Ed.Ctrl(9);
        Ed.Check(EditorHost.Modal(), "Ctrl+F did not open the find bar");
        var label = Ed.Command(id, "Find Text: ", 0);
        Ed.Check(label >= 0 && EditorHost.CmdLine(label) == -1 && EditorHost.CmdY(label) > 360 / 2, "the find bar is not drawn at the bottom");
        Ed.Type("return");
        Ed.Check(EditorHost.Selection(id) == "return" && EditorHost.CursorLine(id) == 5, "typing did not preview the match");
        Ed.Press(Ed.Return);
        Ed.Check(!EditorHost.Modal(), "Return did not close the find bar");
        Ed.Check(EditorHost.CursorLine(id) == 5 && EditorHost.Selection(id) == "return", "found '" + EditorHost.Selection(id) + "' on line " + EditorHost.CursorLine(id));
        Ed.Press(Ed.F3);
        Ed.Check(EditorHost.CursorLine(id) == 9 && EditorHost.Selection(id) == "return", "F3 found '" + EditorHost.Selection(id) + "' on line " + EditorHost.CursorLine(id));
        Ed.Shift(Ed.F3);
        Ed.Check(EditorHost.CursorLine(id) == 5, "Shift+F3 moved to line " + EditorHost.CursorLine(id));

        // Escape closes the bar and restores the selection
        EditorHost.SetCursor(id, 1, 1);
        Ed.Ctrl(9);
        Ed.Type("int");
        Ed.Check(EditorHost.CursorLine(id) == 3, "the preview did not move to line 3");
        Ed.Press(Ed.Escape);
        Ed.Check(!EditorHost.Modal() && EditorHost.CursorLine(id) == 1 && EditorHost.CursorCol(id) == 1, "Escape did not restore the cursor");

        Ed.Ctrl(10);
        Ed.Type("7");
        Ed.Press(Ed.Return);
        Ed.CheckCursor(id, 7, 1);

        // Replace (everything, since nothing is selected)
        EditorHost.SetCursor(id, 1, 1);
        Ed.Ctrl(21);
        Ed.Type("int");
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Modal(), "the replace bar did not ask for the new text");
        Ed.Mods(false, false);
        // The typed int is selected, so typing replaces it
        Ed.Type("long");
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Line(id, 3) == "    long F()" && EditorHost.Line(id, 7) == "    long G()", "replaced: " + EditorHost.Line(id, 3) + " / " + EditorHost.Line(id, 7));

        // The confirmation bar
        EditorHost.Ask(id, "Check", "Answer this?", new List<string> { "Yes", "No" });
        EditorHost.Step();
        Ed.Check(EditorHost.Modal(), "the NagView did not take the keys");
        var message = Ed.Command(id, "Answer this?", 0);
        Ed.Check(message >= 0 && EditorHost.CmdLine(message) == -1 && EditorHost.CmdY(message) < 360 / 2, "the NagView is not drawn at the top");
        Ed.Press(Ed.Right);
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.LastAnswer() == "No", "answered '" + EditorHost.LastAnswer() + "', want 'No'");
        Ed.Check(!EditorHost.Modal(), "the NagView kept the keys after the answer");
        EditorHost.Ask(id, "Check", "Again?", new List<string> { "Yes", "No" });
        Ed.Type("y");
        Ed.Check(EditorHost.LastAnswer() == "Yes" && !EditorHost.Modal(), "y answered '" + EditorHost.LastAnswer() + "'");
        EditorHost.SetCursor(id, 1, 1);
        Ed.Type("x");
        Ed.Check(EditorHost.Line(id, 1) == "xclass A", "typed after the NagView: " + EditorHost.Line(id, 1));

        // Ctrl+N, and saving under a name
        Ed.Ctrl(17);
        var untitled = EditorHost.TakeOpened();
        Ed.Check(untitled > 0 && untitled != id && EditorHost.Path(untitled) == "", "Ctrl+N did not open a new doc");
        Ed.Type("z");
        Ed.Check(EditorHost.Line(untitled, 1) == "z" && EditorHost.Line(id, 1) == "xclass A", "typed into the new doc: " + EditorHost.Line(untitled, 1));
        Ed.Check(EditorHost.Dirty(untitled), "the new doc is not dirty");
        Ed.Ctrl(22);
        Ed.Check(EditorHost.Modal(), "Ctrl+S on the new doc did not ask for a name");
        Ed.Type("made.cs");
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Path(untitled).EndsWith("/made.cs") && !EditorHost.Dirty(untitled), "saved the new doc as '" + EditorHost.Path(untitled) + "'");
        Ed.Check(Ed.Read("made.cs") == "z\n", "made.cs has '" + Ed.Read("made.cs") + "'");

        // Ctrl+O and Ctrl+P
        EditorHost.Focus(id);
        Ed.Ctrl(18);
        Ed.Check(EditorHost.Modal(), "Ctrl+O did not open the open bar");
        Ed.Type("other.cs");
        Ed.Press(Ed.Return);
        var other = EditorHost.TakeOpened();
        Ed.Check(other > 0 && EditorHost.Line(other, 1) == "// other", "Ctrl+O opened '" + EditorHost.Path(other) + "'");
        EditorHost.Focus(id);
        Ed.Ctrl(19);
        Ed.Type("othe");
        Ed.Press(Ed.Return);
        var found = EditorHost.TakeOpened();
        Ed.Check(found > 0 && EditorHost.Path(found) == EditorHost.Path(other), "Ctrl+P othe opened '" + EditorHost.Path(found) + "'");
        Ed.Check(EditorHost.LastError() == "", "error: " + EditorHost.LastError());
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

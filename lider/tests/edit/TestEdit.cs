// Checks that edit operations fed into lider and saved are applied while the game keeps its state.
// The game (game/Game.cs) counts up every frame and reads Target.Value(). The count and the value are read through the same path lider uses to look at the game (GameApp.Inspect).
// Once the game is running, lider rewrites `return 1;` in game/Target.cs to `return 2;` and saves it, and once the reload chunk is applied, the following are checked.
//   - Target.Value() returns 2 (the change was applied)
//   - lider received the game's reload (Lider.Reloads)
//   - The game's count was not reset to its initial value
//   - The contents of the saved file, and the saved state of the Document
//   - The rewritten line stays raised after saving until the reload chunk is applied (LineChanged, drawn on the layer in front of the window), and sinks once it is applied (drawn on the surface)
//   - A file with CRLF line endings is saved with CRLF
//   - The minimap shows what was typed on the board of the opened file
// scripts/test.sh copies this to a temporary directory and starts it (because game/Target.cs gets rewritten).
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestEdit
{
    static int countAt;
    static Document? edited;
    static float waited;
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

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        waited += dt;
        // Wait for the game build
        if (GameApp.Frames == 0)
        {
            Check(waited < 60, "the game did not start in 60 seconds");
            return;
        }
        if (edited == null)
        {
            Edit();
            waited = 0;
            return;
        }
        var value = Game("Value");
        if (value == 1)
        {
            Check(Lider.Reloads == 0, "lider got a reload before the edit was applied");
            Check(edited!.LineChanged(edited.Line), "the saved line sank before the reload");
            Check(InFront(edited, "return 2;"), "the saved line is not drawn in the front layer before the reload");
            Check(waited < 60, "the edit was not applied in 60 seconds");
            return;
        }
        Check(value == 2, "Target.Value() is " + value + ", want 2");
        Check(Lider.Reloads >= 1, "lider did not get the reload");
        Check(Game("Count") > countAt, "the game frame count went back to its initial value: " + Game("Count") + " after " + countAt);
        Check(!edited.LineChanged(edited.Line), "the saved line is still floating after the reload");
        Check(!InFront(edited, "return 2;"), "the saved line is still drawn in the front layer after the reload");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Edit()
    {
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(path != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        Check(w!.Files.Contains(path!), "the workspace does not list " + path);
        w.FocusOn(path!);
        var doc = w.Focus;
        Check(doc != null, "cannot open " + path);
        Check(doc!.Find("return 1;"), "no 'return 1;' in " + path);
        // Move after `return `, delete 1, type 2, and save with Ctrl+S
        for (var i = 0; i < 7; i++)
            Press(w, Keys.Right);
        Press(w, Keys.Delete);
        Press(w, 31);
        Check(doc.Dirty, "the document is not dirty after typing");
        w.SetMods(Keys.ModCtrl);
        Press(w, Keys.S);
        w.SetMods(0);
        Check(!doc.Dirty, "the document is still dirty after saving");
        Io.LoadText(path!, out var text, out _, out _, out _);
        Check(text != null && text.Contains("return 2;") && !text.Contains("return 1;"), "the saved file was not edited");
        countAt = Game("Count");
        Check(countAt > 0, "cannot read the game frame count");
        edited = doc;
        SaveCrlf(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/crlf.txt");
    }

    // Current value of the game's static (-1 if it cannot be read)
    static int Game(string name)
    {
        foreach (var n in GameApp.Inspect("Game", 0, 100))
            if (n.Name == name && int.TryParse(n.Text, out var value))
                return value;
        return -1;
    }

    static void SaveCrlf(string path)
    {
        Io.SaveText(path, "a\r\nb\r\n");
        var doc = Document.Open(path);
        Check(doc != null, "cannot open " + path);
        doc!.Type("x");
        doc.Save();
        Io.LoadText(path, out var text, out _, out _, out _);
        Check(text == "xa\r\nb\r\n", "saved a CRLF file as " + (text ?? "null").Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    // Whether, among the editor commands drawn at the window size, the characters of the line containing text are drawn on the front layer
    // (Syntax highlighting splits a line into per-word commands, so commands are looked up by line number)
    static bool InFront(Document doc, string text)
    {
        var line = -1;
        for (var l = 0; l < doc.LineCount() && line < 0; l++)
            if (doc.Text(l).Contains(text))
                line = l + 1;
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        for (var i = 0; i < n; i++)
            if (line > 0 && EditorHost.CmdKind(i) == 2 && EditorHost.CmdLine(i) == line)
                return PanelView.Front(doc, EditorHost.CmdLine(i));
        Check(false, "no text command for '" + text + "'");
        return false;
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

// Copies a file rewritten outside (the version formatted after saving on the web) into a window that has no edits.
//   - Only the changed lines are copied, and the file does not become unsaved. The cursor stays on the same character. The difference from the game's version is handled the same as on save
//   - Not copied into a window that is being edited
//   - Not copied if LIDER_FOLLOW_DISK is not set
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestFollow
{
    const string Formatted = "public static class Target\n{\n    static int One(int a, int b)\n    {\n        return a + b;\n    }\n}\n";
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

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var w = Lider.Work!;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        Check(!w.FollowDisk, "following without LIDER_FOLLOW_DISK");
        w.FocusOn(path);
        var doc = w.Focus!;
        w.FollowDisk = true;
        w.Tick(0);

        // Put the cursor on the character b of a line where only whitespace changes
        Check(doc.Find("b)"), "no 'b)'");
        var saved = doc.Saved;
        Io.SaveText(path, Formatted);
        w.Tick(0);
        doc.Sync();
        Check(doc.Text(2) == "    static int One(int a, int b)", "line 3: " + doc.Text(2));
        Check(doc.Text(4) == "        return a + b;", "line 5: " + doc.Text(4));
        Check(!EditorHost.Dirty(doc.Id), "the formatted file is dirty");
        Check(doc.Saved > saved, "not treated as a save");
        Check(Utf8.At(doc.Text(2), doc.Col) == 'b', "the cursor moved to column " + doc.Col);

        // Not copied into a window that is being edited
        doc.Type("// typing\n");
        var typed = doc.Lines().Count;
        Io.SaveText(path, "public static class Target\n{\n}\n");
        w.Tick(0);
        Check(doc.Lines().Count == typed, "overwrote a dirty window");
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

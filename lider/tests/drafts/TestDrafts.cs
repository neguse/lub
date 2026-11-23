// Drafts of unsaved text.
//   - A draft comes back to the window still unsaved, and does not change the file or the game's version. A draft whose original file is missing is discarded
//   - Written on typing and deleted on saving. Kept while unsaved even if the window is closed, and deleted when the file is deleted
//   - Ctrl+P candidates get an unsaved mark
using System;
using Lub;
using LiderCore;

public static class TestDrafts
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

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var w = Lider.Work!;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        var dir = w.Root + "/.drafts";
        var draft = dir + "/Target.cs";
        Check(w.Drafts == null, "drafts without LIDER_DRAFTS");
        w.Drafts = new Drafts(dir);
        w.Drafts.Restore(w);

        var doc = w.Docs[path];
        Check(w.WindowOf(doc) != null, "no window for the restored file");
        Check(doc.Text(2) == "    public static int One() { return 2; }", "restored: " + doc.Text(2));
        Check(doc.Dirty, "the restored file is not dirty");
        Check(doc.LiveLines[2] == "    public static int One() { return 1; }", "the live version: " + doc.LiveLines[2]);
        Check(Disk.Stat(dir + "/Gone.cs") == null, "kept the draft of a missing file");
        Check(Disk.Stat(draft) != null, "lost the draft on restore");

        // The Ctrl+P mark
        var search = new Search();
        search.Open(w, "Target.cs");
        Check(Marked(search, "Target.cs"), "no mark on the dirty file");
        search.Open(w, "Other.cs");
        Check(!Marked(search, "Other.cs"), "a mark on the clean file");
        search.Cancel(w);

        // Typing writes it on the next Tick
        var before = Disk.Stat(draft)!.Size;
        doc.Type("// typing\n");
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(draft)!.Size > before, "the draft did not grow");

        // Kept while unsaved even after the window is closed
        w.Close(w.WindowOf(doc)!);
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(draft) != null, "closing the window dropped the draft");
        w.FocusOn(path);

        // Saving deletes it. The file gets the draft's text
        doc.Save();
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(draft) == null, "saving kept the draft");
        Check(!doc.Dirty, "dirty after saving");

        // A file with a deep path is also restored to the same path. A draft with the same text is discarded
        Io.SaveText(dir + "/Other.cs", "public static class Other\n{\n}");
        w.Drafts.Restore(w);
        Check(Disk.Stat(dir + "/Other.cs") == null, "kept a draft equal to the file");
        Check(Drafts.NameOf("a/b%c.cs") == "a%2Fb%25c.cs", "name: " + Drafts.NameOf("a/b%c.cs"));
        Check(Drafts.PathOf("a%2Fb%25c.cs") == "a/b%c.cs", "path: " + Drafts.PathOf("a%2Fb%25c.cs"));

        // Deleting the file also deletes the draft
        var other = w.PathOf("Other.cs");
        w.FocusOn(other);
        w.Focus!.Type("// typing\n");
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(dir + "/Other.cs") != null, "no draft of the other file");
        w.Drafts.Forget(w, other);
        Check(Disk.Stat(dir + "/Other.cs") == null, "forgetting kept the draft");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static bool Marked(Search search, string text)
    {
        foreach (var item in search.Results)
            if (item.Text == text)
                return item.Dirty;
        return false;
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

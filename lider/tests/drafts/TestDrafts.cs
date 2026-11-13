// 保存していない本文の下書き。
//   - 下書きは保存していないまま窓に戻り、file とゲームの版は変えない。元の file が無い下書きは捨てる
//   - 打つと書き、保存すると消す。窓を閉じても未保存の間は残り、file を消すと消える
//   - Ctrl+P の候補に未保存の印が付く
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

        // Ctrl+P の印
        var search = new Search();
        search.Open(w, "Target.cs");
        Check(Marked(search, "Target.cs"), "no mark on the dirty file");
        search.Open(w, "Other.cs");
        Check(!Marked(search, "Other.cs"), "a mark on the clean file");
        search.Cancel(w);

        // 打つと、次の Tick で書く
        var before = Disk.Stat(draft)!.Size;
        doc.Type("// typing\n");
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(draft)!.Size > before, "the draft did not grow");

        // 窓を閉じても未保存の間は残る
        w.Close(w.WindowOf(doc)!);
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(draft) != null, "closing the window dropped the draft");
        w.FocusOn(path);

        // 保存すると消える。file は下書きの本文になる
        doc.Save();
        w.Tick(Drafts.WriteSeconds);
        Check(Disk.Stat(draft) == null, "saving kept the draft");
        Check(!doc.Dirty, "dirty after saving");

        // 深い path の file も、同じ path に戻す。同じ本文の下書きは捨てる
        Io.SaveText(dir + "/Other.cs", "public static class Other\n{\n}");
        w.Drafts.Restore(w);
        Check(Disk.Stat(dir + "/Other.cs") == null, "kept a draft equal to the file");
        Check(Drafts.NameOf("a/b%c.cs") == "a%2Fb%25c.cs", "name: " + Drafts.NameOf("a/b%c.cs"));
        Check(Drafts.PathOf("a%2Fb%25c.cs") == "a/b%c.cs", "path: " + Drafts.PathOf("a%2Fb%25c.cs"));

        // file を消すと下書きも消える
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

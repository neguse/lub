// Ctrl+P で file を作り、Alt+Delete で消す。
//   - 新しい型の名前を打つと候補の最後に「作る」が出て、確定すると型の宣言だけの file を作って開く
//   - 名前が既にある file か、型の名前にならない字には「作る」を出さない
//   - 尋ねている間も、帯の下地は窓の全体を覆わない (本文が見える)
//   - Alt+Delete は尋ねてから消し、消さないと答えれば何も起きない。消すと窓が閉じ、一覧から消える
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestCreate
{
    static bool ran;
    static int waited;
    static Document? asked;
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
        var w = Lider.Work!;
        if (asked != null)
        {
            if (++waited < 3)
                return;
            Finish(w, asked);
            return;
        }
        if (ran)
            return;
        ran = true;
        var search = Lider.Search;
        w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);

        Check(!Offers(w, "Target"), "offered a file that exists");
        Check(!Offers(w, "a b"), "offered a name with a space");
        Check(!Offers(w, "1st"), "offered a name that starts with a digit");
        Check(Offers(w, "Fresh.cs"), "did not offer Fresh.cs");
        search.Cancel(w);

        search.Open(w);
        search.Type(w, "Fresh");
        search.Selected = search.Results.Count - 1;
        search.Commit(w);
        var fresh = w.PathOf("Fresh.cs");
        Check(!search.Shown, "the search stayed open");
        Check(w.Files.Contains(fresh), "the file is not listed");
        Check(w.Focus != null && w.Focus.Path == fresh, "the new file is not focused");
        Check(w.Focus!.Text(0) == "public class Fresh", "the new file has " + w.Focus.Text(0));

        asked = w.Focus;
        EditorHost.Focus(asked!.Id);
        Alt(Keys.Delete);
        Check(EditorHost.Modal(), "Alt+Delete did not ask");
    }

    static void Finish(Workspace w, Document doc)
    {
        var search = Lider.Search;
        var fresh = w.PathOf("Fresh.cs");
        asked = null;
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        var tallest = 0f;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdLine(i) < 0 && EditorHost.CmdKind(i) == 1)
                tallest = Math.Max(tallest, EditorHost.CmdH(i));
        Check(tallest > 0 && tallest < PanelView.BasePxH / 2, "the question covers the window: " + tallest);
        Press(w, 17); // n
        w.Tick(0);
        Check(!EditorHost.Modal(), "the question stayed");
        Check(w.Files.Contains(fresh) && w.FocusWindow != null && w.Focus == doc, "answering no deleted the file");

        Alt(Keys.Delete);
        Press(w, 28); // y
        w.Tick(0);
        Check(!EditorHost.Modal(), "the question stayed after yes");
        Check(!w.Files.Contains(fresh), "the deleted file is listed");
        Check(w.WindowOf(doc) == null && !w.Docs.ContainsKey(fresh), "the window of the deleted file stayed");
        search.Open(w);
        search.Type(w, "Fresh");
        Check(Offers(w, "Fresh"), "the deleted file is not offered again");
        search.Cancel(w);
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static bool Offers(Workspace w, string query)
    {
        var search = Lider.Search;
        search.Cancel(w);
        search.Open(w);
        search.Type(w, query);
        foreach (var r in search.Results)
            if (r.Create)
                return true;
        return false;
    }

    static void Alt(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = Keys.ModAlt, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
    }

    static void Press(Workspace w, int key)
    {
        w.Key(key, true);
        w.Key(key, false);
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

// エディタの command が開いた doc (Ctrl+N、Ctrl+O、Ctrl+P) を、lider の窓のタブにする。key は Workspace.Key から流す。
//   - Ctrl+N は名前の無い doc のタブを足して出し、打った文字はその doc に入る。Ctrl+S で名前を付けて保存すると、タブの Document の path がその file になる
//   - Ctrl+O で path を打って Return で、その file のタブを足す。もうタブのある file なら、タブを足さずにそのタブを出す
//   - Ctrl+P で名前の一部を打って Return で、一覧の最初の file のタブを出す
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestOpen
{
    const int Return = 40;
    const int Period = 55;
    const int N = 17;
    const int O = 18;
    const int P = 19;
    const int S = 22;
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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

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
        var target = w.Focus!;
        var targetText = target.Text(0);

        Chord(w, Keys.ModCtrl, N);
        Check(w.Windows.Count == 1 && w.FocusWindow!.Tabs.Count == 2 && w.Focus != target && w.Focus!.Path == "", "Ctrl+N did not add a tab for a new doc");
        if (failed)
            return;
        var untitled = w.Focus!;
        Type(w, "z");
        Check(untitled.Text(0) == "z" && target.Text(0) == targetText, "typed after Ctrl+N: '" + untitled.Text(0) + "' in the new doc, '" + target.Text(0) + "' in Target.cs");
        Chord(w, Keys.ModCtrl, S);
        Check(EditorHost.Modal(), "Ctrl+S on the new doc did not ask for a name");
        Type(w, "made");
        Press(w, Period);
        Type(w, "cs");
        Press(w, Return);
        w.Sync();
        Check(untitled.Path.EndsWith("made.cs") && w.Resolve(untitled.Path) == untitled.Path && w.Docs.ContainsKey(untitled.Path) && w.Docs[untitled.Path] == untitled,
            "after saving the new doc as made.cs, its path is '" + untitled.Path + "'");

        w.FocusOn(path!);
        OpenFile(w);
        Check(!EditorHost.Modal(), "Return did not close the open bar");
        Check(w.FocusWindow!.Tabs.Count == 3 && w.Focus!.Path.EndsWith("other.cs") && w.Focus.Text(1) == "public static class Other",
            "Ctrl+O other.cs opened '" + w.Focus?.Path + "' with " + w.FocusWindow!.Tabs.Count + " tabs");
        if (failed)
            return;
        var other = w.Focus!;
        Check(w.Resolve(other.Path) == other.Path, "the tab's path '" + other.Path + "' is not the workspace's path for the file");

        w.FocusOn(path!);
        OpenFile(w);
        Check(w.FocusWindow!.Tabs.Count == 3 && w.Focus == other, "opening other.cs again added a tab or did not show it (" + w.FocusWindow!.Tabs.Count + " tabs)");

        w.FocusOn(path!);
        Chord(w, Keys.ModCtrl, P);
        Check(EditorHost.Modal(), "Ctrl+P did not open the find bar");
        Type(w, "othe");
        Press(w, Return);
        Check(w.FocusWindow!.Tabs.Count == 3 && w.Focus == other, "Ctrl+P othe showed '" + w.Focus?.Path + "' with " + w.FocusWindow!.Tabs.Count + " tabs");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // Ctrl+O で other.cs を開く
    static void OpenFile(Workspace w)
    {
        Chord(w, Keys.ModCtrl, O);
        Check(EditorHost.Modal(), "Ctrl+O did not open the open bar");
        Type(w, "other");
        Press(w, Period);
        Type(w, "cs");
        Press(w, Return);
    }

    // 英小文字を、key を押して打つ
    static void Type(Workspace w, string s)
    {
        foreach (var c in Utf8.ByteList(s))
            Press(w, 4 + (c - 'a'));
    }

    static void Press(Workspace w, int key)
    {
        w.Key(key, true);
        w.Key(key, false);
    }

    static void Chord(Workspace w, int modifier, int key)
    {
        w.SetMods(modifier);
        Press(w, key);
        w.SetMods(0);
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

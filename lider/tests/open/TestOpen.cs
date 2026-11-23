// Turns docs opened by editor commands (Ctrl+N, Ctrl+O) into tabs of lider's window. Keys are fed through Workspace.Key.
//   - Ctrl+N adds and shows a tab for a doc without a name, and typed characters go into that doc. Saving under a name with Ctrl+S sets the path of the tab's Document to that file
//   - Typing a path after Ctrl+O and pressing Return adds a tab for that file. For a file that already has a tab, it shows that tab without adding one
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
        Check(w.Windows.Count == 1 && w.FocusWindow!.Code.Tabs.Count == 2 && w.Focus != target && w.Focus!.Path == "", "Ctrl+N did not add a tab for a new doc");
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
        Check(w.FocusWindow!.Code.Tabs.Count == 3 && w.Focus!.Path.EndsWith("other.cs") && w.Focus.Text(1) == "public static class Other",
            "Ctrl+O other.cs opened '" + w.Focus?.Path + "' with " + w.FocusWindow!.Code.Tabs.Count + " tabs");
        if (failed)
            return;
        var other = w.Focus!;
        Check(w.Resolve(other.Path) == other.Path, "the tab's path '" + other.Path + "' is not the workspace's path for the file");

        w.FocusOn(path!);
        OpenFile(w);
        Check(w.FocusWindow!.Code.Tabs.Count == 3 && w.Focus == other, "opening other.cs again added a tab or did not show it (" + w.FocusWindow!.Code.Tabs.Count + " tabs)");

        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // Open other.cs with Ctrl+O
    static void OpenFile(Workspace w)
    {
        Chord(w, Keys.ModCtrl, O);
        Check(EditorHost.Modal(), "Ctrl+O did not open the open bar");
        Type(w, "other");
        Press(w, Period);
        Type(w, "cs");
        Press(w, Return);
    }

    // Type lowercase letters by pressing keys
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

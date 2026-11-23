// Search for files and declarations (Search, Declarations).
//   - Declarations pick up types, methods, constructors, properties, and fields. Things inside strings, comments, bodies, and initializers, and enum members, are not picked up
//   - Typing narrows the candidates, and both files and declarations can be looked up in the same field
//   - Choosing with up/down opens a tab provisionally, and Escape closes it and restores the focus and cursor
//   - Return moves to the line of the chosen declaration and closes, and a file that already has a tab does not get another tab
//   - Pressing on the search board does not reach the window below
//   - Return with no candidates goes back to the state before opening, the same as Escape
//   - Declarations are not reloaded while typing, but after typing ends
//   - GoTo jumps to the declaration of the name, and Ctrl+- goes back. A missing name does not move
//   - F12 jumps if the word at the cursor has one declaration, and if there is none, opens search with that word entered. Ctrl+- goes back to the place before the jump
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestSearch
{
    static bool failed;
    static int frames;
    static int boardAt;

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
        frames++;
        var w = Lider.Work!;
        if (frames == 1)
        {
            ScanDeclarations();
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            Lider.Search.Open(w);
            return;
        }
        Lider.Search.Refresh(w);
        if (frames < 5)
            return;
        if (boardAt > 0)
        {
            if (frames == boardAt)
                PressBoard(w);
            return;
        }
        var s = Lider.Search;
        var first = w.Focus!;
        Check(s.Results.Count >= 2, "the files are not listed before typing");
        foreach (var r in s.Results)
            Check(r.Line < 0, "a declaration is listed before typing");

        TypeIn(w, "bcn");
        var listed = "";
        foreach (var r in s.Results)
            listed += r.Text + " ";
        Check(listed.Contains("Beacon.cs ") && !listed.Contains("Target"), "a file is not found: " + listed);
        s.Backspace(w);
        s.Backspace(w);
        s.Backspace(w);
        TypeIn(w, "bcn blnk");
        Check(s.Results.Count == 1 && s.Results[0].Text == "Beacon.Blink" && s.Results[0].Where == "Beacon.cs:4", "a declaration is not found");

        var count = w.FocusWindow!.Code.Tabs.Count;
        s.Move(w, 1);
        Check(w.FocusWindow!.Code.Tabs.Count == count + 1 && w.Focus!.Path.EndsWith("Beacon.cs") && w.Focus.Line == 3, "selecting did not preview the place");
        s.Cancel(w);
        Check(!s.Shown && w.FocusWindow!.Code.Tabs.Count == count && w.Focus == first, "Escape did not go back");

        s.Open(w);
        TypeIn(w, "swep");
        var line = w.Focus!.Line;
        s.Move(w, 0);
        Check(w.FocusWindow!.Code.Tabs.Count == count && w.Focus!.Line == 5, "selecting in an open file moved to " + w.Focus!.Line);
        s.Cancel(w);
        Check(w.Focus!.Line == line, "Escape did not put the cursor back");

        // A provisionally opened tab does not remain even if Return is pressed after the candidates run out
        s.Open(w);
        TypeIn(w, "bcn blnk");
        s.Move(w, 1);
        TypeIn(w, "zzzz");
        s.Commit(w);
        Check(!s.Shown && w.FocusWindow!.Code.Tabs.Count == count && w.Focus == first, "Return with no results kept the preview");

        // Not reloaded right after typing, but reloaded when search is opened
        var lastType = first;
        lastType.MoveTo(0, 0);
        lastType.Type("class Fresh { int Typed; }\n");
        s.Refresh(w);
        Check(s.Find("Fresh.Typed") == null, "a keystroke rescanned at once");
        s.Open(w);
        Check(s.Find("Fresh.Typed") != null, "opening the search did not rescan");
        s.Cancel(w);
        lastType.Type("");
        EditorHost.SetCursor(lastType.Id, 1, 1);
        for (var i = 0; i < 27; i++)
            EditorHost.Key(Keys.Delete, true);
        lastType.Sync();
        s.Open(w);
        s.Cancel(w);

        s.Open(w);
        TypeIn(w, "rdr rnge");
        s.Commit(w);
        Check(!s.Shown && w.Focus == first && w.Focus!.Line == 3, "Return did not move to the declaration");

        // Opening a tab and confirming keeps the tab
        s.Open(w);
        TypeIn(w, "bcn");
        s.Commit(w);
        Check(w.FocusWindow!.Code.Tabs.Count == count + 1 && w.Focus!.Path.EndsWith("Beacon.cs"), "Return did not open the file");
        // Ctrl+- goes back to the place before confirming (the tab stays open)
        w.Back();
        Check(w.Focus == first && w.Focus!.Line == 3, "going back did not return to the place before Return");
        w.Back();
        Check(w.Focus!.Line == line, "going back twice did not return: " + w.Focus!.Line);
        w.Back();
        Check(w.Focus!.Line == line, "going back with no history moved the cursor");

        Check(Search.WordAt("foo.Bar(x)", 5) == "Bar" && Search.WordAt("foo.Bar(x)", 7) == "Bar" && Search.WordAt("a  b", 2) == "", "the word at the cursor");
        w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        w.Focus!.MoveTo(1, 22);
        s.Jump(w);
        Check(!s.Shown && w.Focus!.Line == 1, "F12 on a type did not jump to its one declaration");
        w.Focus!.MoveTo(0, 0);
        s.Jump(w);
        Check(s.Shown && s.Query == "", "F12 on a comment did not open the search: " + s.Query);
        s.Cancel(w);
        w.Focus!.MoveTo(7, 10);
        s.Jump(w);
        Check(s.Shown && s.Query == "return", "F12 on a word with no declaration did not open the search: " + s.Query);
        s.Cancel(w);

        w.Focus!.MoveTo(0, 0);
        var before = w.Focus!;
        s.GoTo(w, "Beacon.Blink");
        Check(w.Focus!.Path.EndsWith("Beacon.cs") && w.Focus!.Line == 3, "GoTo did not jump to the declaration");
        s.GoTo(w, "Nothing");
        Check(w.Focus!.Path.EndsWith("Beacon.cs") && w.Focus!.Line == 3, "GoTo moved for a missing name");
        w.Back();
        Check(w.Focus == before && before.Line == 0, "going back after GoTo did not return");
        // Pressing on the search board only closes it, and the cursor of the window below does not move
        s.Open(w);
        boardAt = frames + 3;
    }

    static void PressBoard(Workspace w)
    {
        var s = Lider.Search;
        Gfx.Size(out var sw, out var sh);
        w.Focus!.MoveTo(2, 0);
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = sw * 0.5f, Y = sh * 0.14f });
        Check(!s.Shown, "pressing the board did not close the search");
        Check(w.Focus!.Line == 2, "the press went through the board to the window: " + w.Focus!.Line);
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = sw * 0.5f, Y = sh * 0.14f });
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void TypeIn(Workspace w, string text)
    {
        foreach (var c in Utf8.ByteList(text))
            Lider.Search.Type(w, Utf8.Encode(c));
    }

    static void ScanDeclarations()
    {
        var lines = new List<string>
        {
            "using System;",
            "namespace Game.Core;",
            "// class Commented { }",
            "[Serializable]",
            "public class Outer<T> : Base where T : class",
            "{",
            "    public const string Text = \"class Quoted { void No() { } }\";",
            "    static readonly List<float> Colors = new List<float> { 1, 2 };",
            "    public int Size { get; private set; }",
            "    public Outer(int a = 1) { var inner = 2; }",
            "    public int Twice(int x) => x * 2;",
            "    public static List<int> Make<U>(U u)",
            "    {",
            "        int local = 0;",
            "        if (local == 0) { Call(); }",
            "        return null;",
            "    }",
            "    public (int, int) Pair() { return (1, 2); }",
            "    public static Outer<T> operator +(Outer<T> a, Outer<T> b) => a;",
            "    public int this[int i] => i;",
            "    public enum Kind { One, Two = 2 }",
            "    /* void Hidden() { } */",
            "    struct Inner",
            "    {",
            "        char Brace = '{';",
            "        void Deep() { }",
            "    }",
            "}",
            "interface IThing { void Act(); }",
            "class Kid : Outer {",
            "    public Kid(int a) : base(a) { }",
            "    public Kid() : this(1) { }",
            "    public T Make2<T>() where T : new() { return new T(); }",
            "    char Quote = '\\u0041'; int After;",
            "}",
            "record struct Point(int X);",
        };
        var got = "";
        foreach (var d in Declarations.Scan(lines))
            got += d.Name + ":" + d.Line + " ";
        var want = "Outer:4 Outer.Text:6 Outer.Colors:7 Outer.Size:8 Outer.Outer:9 Outer.Twice:10 Outer.Make:11 Outer.Pair:17 Outer.Kind:20 Outer.Inner:22 Outer.Inner.Brace:24 Outer.Inner.Deep:25 IThing:28 IThing.Act:28 Kid:29 Kid.Kid:30 Kid.Kid:31 Kid.Make2:32 Kid.Quote:33 Kid.After:33 Point:35 ";
        Check(got == want, "got " + got + "\nwant " + want);
        Check(Declarations.Scan(new List<string> { "class A {", "  const int B = 1;", "  int C = 2;", "}" })[1].Const
            && !Declarations.Scan(new List<string> { "class A {", "  const int B = 1;", "  int C = 2;", "}" })[2].Const, "a const field is not marked");
        Check(Declarations.Scan(new List<string> { "class A {", "  int C = 2;", "}" })[0].IsType
            && !Declarations.Scan(new List<string> { "class A {", "  int C = 2;", "}" })[1].IsType, "a type is not marked");
        var kinds = Declarations.Scan(new List<string> { "class A {", "  int F = 1;", "  int P { get; set; }", "  void M() { }", "  int E() => 1;", "}" });
        Check(!kinds[1].IsCode && kinds[2].IsCode && kinds[3].IsCode && kinds[4].IsCode, "the code declarations are not marked");
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

// Types Japanese with jamo (kana-kanji conversion) in lider's window. Keys are fed through Workspace.Key.
//   - Ctrl+Space switches to kana mode, and typing romaji shows the kana being converted inline at the cursor in the text
//   - Converting with Space shows the candidate list on the layer in front of the window (commands with line number -1)
//   - Confirming with Return puts the kanji into the text
//   - Escape during conversion cancels it and does not close the window. With kana mode turned off, typing produces ASCII
//   - During input, F7 converts to katakana, F9 to full-width Latin letters, F10 to half-width Latin letters, and F6 to hiragana. F6 and F7 are fed as lub events, to check that frame stepping does not take them
//   - Confirming writes the learned data to the file in env LIDER_JAMO_USER
// The dictionary is env LIDER_JAMO_DICT, or lider's data/mozc.dict if it is not set (copied by scripts/build-native.sh).
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestIme
{
    static int mods;
    const int Return = 40;
    const int Escape = 41;
    const int Space = 44;
    const int End = 77;
    const int F6 = 63;
    const int F7 = 64;
    const int F9 = 66;
    const int F10 = 67;
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
        var doc = w.Focus!;
        // End of the `    // ` line (line 4)
        doc.MoveTo(3, 0);
        Press(w, End);
        var line = doc.Line;
        Check(doc.Text(line) == "    // ", "line " + line + " is '" + doc.Text(line) + "'");

        Chord(w, Keys.ModCtrl, Space);
        Check(EditorHost.ImeEnabled(), "Ctrl+Space did not turn the kana mode on");
        Type(w, "kyouhaiitenkidesune");
        Check(EditorHost.ImeComposing(), "typing romaji did not start a composition");
        Check(doc.Text(line) == "    // きょうはいいてんきですね", "the preedit is not inline: " + doc.Text(line));

        Press(w, Space);
        EditorHost.Step();
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        var candidates = 0;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdLine(i) == -1 && EditorHost.CmdText(i).StartsWith("1 "))
                candidates++;
        Check(candidates == 1, "the candidate list is not drawn in the front layer (" + candidates + " first candidates)");

        Press(w, Return);
        Check(!EditorHost.ImeComposing(), "Return did not commit");
        Check(doc.Text(line) == "    // 今日はいい天気ですね", "committed: " + doc.Text(line));

        Type(w, "aiu");
        Check(EditorHost.Modal(), "the composition does not hold Escape");
        Press(w, Escape);
        Check(!EditorHost.ImeComposing() && w.Windows.Count == 1, "Escape did not cancel the composition, or closed the window");
        Check(doc.Text(line) == "    // 今日はいい天気ですね", "after Escape: " + doc.Text(line));

        Type(w, "aiu");
        Fn(F7);
        Check(doc.Text(line) == "    // 今日はいい天気ですねアイウ", "after F7: " + doc.Text(line));
        Fn(F9);
        Check(doc.Text(line) == "    // 今日はいい天気ですねａｉｕ", "after F9: " + doc.Text(line));
        Fn(F10);
        Check(doc.Text(line) == "    // 今日はいい天気ですねaiu", "after F10: " + doc.Text(line));
        Fn(F6);
        Check(doc.Text(line) == "    // 今日はいい天気ですねあいう", "after F6: " + doc.Text(line));
        Press(w, Return);
        Check(doc.Text(line) == "    // 今日はいい天気ですねあいう", "committed after F6: " + doc.Text(line));
        var user = Environment.GetEnvironmentVariable("LIDER_JAMO_USER") ?? "";
        var stat = Disk.Stat(user);
        Check(user != "" && stat != null && stat.Size > 0, "the learned data was not written to '" + user + "'");

        Chord(w, Keys.ModCtrl, Space);
        Type(w, "a");
        Check(!EditorHost.ImeEnabled() && doc.Text(line) == "    // 今日はいい天気ですねあいうa", "after the kana mode off: " + doc.Text(line));
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // Press and release a key into Lider.OnEvent, the same as the events that come from lub
    static void Fn(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = mods, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
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
        mods = modifier;
        w.SetMods(modifier);
        Press(w, key);
        mods = 0;
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

    public static ErrorAction OnError(string message)
    {
        Console.WriteLine("FAIL " + message);
        return ErrorAction.Abort;
    }
}

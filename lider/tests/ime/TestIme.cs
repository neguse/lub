// lider の窓で、jamo (かな漢字変換) で日本語を打つ。key は Workspace.Key から流す。
//   - Ctrl+Space でかなの mode にし、ローマ字を打つと、変換中のかなが本文の cursor の所に inline で出る
//   - Space で変換すると、候補の一覧が窓の手前の層 (行の番号が -1 の命令) に出る
//   - Return で確定すると、漢字が本文に入る
//   - 変換中の Escape は取り消して窓を閉じない。かなの mode を切ると ASCII を打つ
//   - 入力中の F7 はカタカナ、F9 は全角英字、F10 は半角英字、F6 はひらがなにする。F6 と F7 は lub の event から流し、frame の行き来に取られないことを見る
//   - 確定すると、学習を env LIDER_JAMO_USER の file に書く
// 辞書は env LIDER_JAMO_DICT、無ければ lider の data/mozc.dict (scripts/build-native.sh が写す)。
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
        // `    // ` の行 (4 行目) の末尾
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

    // lub から届く event と同じに、Lider.OnEvent へ key を押して離す
    static void Fn(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = mods, Typed = Keys.UsChar(key, false), Plain = Keys.UsChar(key, false) });
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

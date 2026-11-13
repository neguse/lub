// jamo (src/Jamo.ref.cs の native の拡張) で日本語を打つ。辞書は env LIDER_JAMO_DICT (無ければ確かめずに通す)。
//   - Ctrl+Space でかなの mode にし、ローマ字を打つと、変換中のかなが本文の cursor の所に inline で出る
//   - Space で変換すると、候補の一覧が帯と同じ手前の層 (行の番号が -1 の命令) に出る
//   - Return で確定すると、漢字が本文に入る。変換中の文字の入れ替えは、消した回数に数えない
//   - 変換中の Escape は取り消す (Modal の間は key をエディタが取る)
//   - 入力中の F7 はカタカナ、F9 は全角英字、F10 は半角英字、F6 はひらがなにする
//   - 確定すると、学習を env LIDER_JAMO_USER の file に書く
//   - かなの mode を切ると ASCII を打つ
using System;
using Lub;
using LiderEditor;

public static class TestIme
{
    static bool ran;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        Ed.Boot();
        var dict = Environment.GetEnvironmentVariable("LIDER_JAMO_DICT") ?? "";
        if (dict == "" || Disk.Stat(dict) == null || !EditorHost.ImeDict(dict))
        {
            Console.WriteLine("SKIP no jamo dictionary");
            Ed.Pass();
            return;
        }
        var id = Ed.Open(Ed.Write("Ime.cs", "class A\n{\n    // \n}\n"));
        EditorHost.SetCursor(id, 3, 8);
        Ed.Mods(true, false);
        Ed.Press(Ed.Space);
        Ed.Mods(false, false);
        Ed.Check(EditorHost.ImeEnabled(), "Ctrl+Space did not turn the kana mode on");
        var removed = EditorHost.RemovedCount(id);
        Ed.Type("kyouhaiitenkidesune");
        Ed.Check(EditorHost.ImeComposing() && EditorHost.Modal(), "typing romaji did not start a composition");
        Ed.Check(EditorHost.Line(id, 3) == "    // きょうはいいてんきですね", "the preedit is not inline: " + EditorHost.Line(id, 3));
        Ed.Press(Ed.Space);
        EditorHost.Step();
        var n = EditorHost.Draw(id, 640, 360);
        var candidates = 0;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdLine(i) == -1 && EditorHost.CmdText(i).StartsWith("1 "))
                candidates++;
        Ed.Check(candidates == 1, "the candidate list is not drawn in the front layer (" + candidates + " first candidates)");
        Ed.Press(Ed.Return);
        Ed.Check(!EditorHost.ImeComposing(), "Return did not commit");
        Ed.Check(EditorHost.Line(id, 3) == "    // 今日はいい天気ですね", "committed: " + EditorHost.Line(id, 3));
        Ed.Check(EditorHost.RemovedCount(id) == removed, "replacing the preedit was counted as removing");

        Ed.Type("aiu");
        Ed.Press(Ed.Escape);
        Ed.Check(!EditorHost.ImeComposing() && EditorHost.Line(id, 3) == "    // 今日はいい天気ですね", "after Escape: " + EditorHost.Line(id, 3));

        Ed.Type("aiu");
        Ed.Press(Ed.F7);
        Ed.Check(EditorHost.Line(id, 3) == "    // 今日はいい天気ですねアイウ", "after F7: " + EditorHost.Line(id, 3));
        Ed.Press(Ed.F9);
        Ed.Check(EditorHost.Line(id, 3) == "    // 今日はいい天気ですねａｉｕ", "after F9: " + EditorHost.Line(id, 3));
        Ed.Press(Ed.F10);
        Ed.Check(EditorHost.Line(id, 3) == "    // 今日はいい天気ですねaiu", "after F10: " + EditorHost.Line(id, 3));
        Ed.Press(Ed.F6);
        Ed.Check(EditorHost.Line(id, 3) == "    // 今日はいい天気ですねあいう", "after F6: " + EditorHost.Line(id, 3));
        Ed.Press(Ed.Return);
        var user = Environment.GetEnvironmentVariable("LIDER_JAMO_USER") ?? "";
        var stat = Disk.Stat(user);
        Ed.Check(user != "" && stat != null && stat.Size > 0, "the learned data was not written to '" + user + "'");

        Ed.Mods(true, false);
        Ed.Press(Ed.Space);
        Ed.Mods(false, false);
        Ed.Type("a");
        Ed.Check(!EditorHost.ImeEnabled() && EditorHost.Line(id, 3) == "    // 今日はいい天気ですねあいうa", "after the kana mode off: " + EditorHost.Line(id, 3));
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

// Types Japanese with jamo (the native extension in src/Jamo.ref.cs). The dictionary is env LIDER_JAMO_DICT (if missing, the test passes without checking).
//   - Ctrl+Space switches to kana mode, and typing romaji shows the kana being converted inline at the cursor in the text
//   - Converting with Space shows the candidate list on the same front layer as the bars (commands with line number -1)
//   - Confirming with Return puts the kanji into the text. Replacing characters during conversion does not count toward the removal count
//   - Escape during conversion cancels it (while Modal, the editor takes the keys)
//   - During input, F7 converts to katakana, F9 to full-width Latin letters, F10 to half-width Latin letters, and F6 to hiragana
//   - Confirming writes the learned data to the file in env LIDER_JAMO_USER
//   - With kana mode turned off, typing produces ASCII
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

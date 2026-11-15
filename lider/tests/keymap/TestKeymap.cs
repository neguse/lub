// key の割り当てを変える。
//   - 命令 keys:set で命令を選ぶと、次に押した key がその命令に割り当たり、前の key は外れる
//   - 修飾なしの字の key と、focus をゲームへ移す key は、割り当てられない
//   - Esc で、割り当てずにやめる
//   - 変えた分は file に書かれ、その行から読み直すと同じ割り当てになる
//   - 検索と案内には、今の key が出る
//   - 命令 keys:reset で、既定に戻る
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestKeymap
{
    const string Toggle = "inspector:toggle";
    // key の番号は、A からの並びの順
    const int KeyI = Keys.A + 8;
    const int KeyO = Keys.A + 14;
    static int frames;
    static int stage;
    static float waited;
    static bool failed;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
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

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        waited += dt;
        if (waited < 0.4f)
            return;
        waited = 0;
        var search = Lider.Search;
        switch (stage++)
        {
            case 0:
                Check(Keymap.StrokeOf(Toggle) == "alt+i" && Keymap.ShownOf(Toggle) == "Alt+I", "the key at first: " + Keymap.StrokeOf(Toggle));
                Pick(w);
                Check(Lider.KeyFor == Toggle && search.Shown && search.Prompt.Contains(Toggle) && search.Prompt.Contains("Alt+I"), "not waiting for a key: " + search.Prompt);
                // 修飾なしの字は割り当てられない
                Key(Keys.A, 0, 'a');
                Check(Lider.KeyFor == Toggle && search.Prompt.Contains("割り当てられない"), "a plain letter was taken: " + search.Prompt);
                // focus をゲームへ移す key は取れない
                Key(Keys.F1, 0, 0);
                Check(Lider.KeyFor == Toggle && Keymap.StrokeOf("focus:game") == "f1", "the key of focus:game was taken");
                Key(KeyO, Keys.ModAlt, 'o');
                Check(Lider.KeyFor == null && !search.Shown, "still waiting after a key");
                Check(Keymap.StrokeOf(Toggle) == "alt+o" && Keymap.CommandsOf("alt+i") == "", "the keys after the change: " + Keymap.StrokeOf(Toggle) + " / " + Keymap.CommandsOf("alt+i"));
                break;
            case 1:
                Io.LoadText(Environment.GetEnvironmentVariable("LIDER_KEYS")!, out var text, out _, out _, out _);
                Check(text != null && text.Contains("alt+o " + Toggle + "\n") && text.Contains("alt+i\n"), "the file: " + text);
                // 新しい key で効き、前の key では効かない
                var shown = Lider.Inspector.Shown;
                var focused = w.Focused;
                Key(KeyI, Keys.ModAlt, 'i');
                Check(Lider.Inspector.Shown == shown && w.Focused == focused, "the old key still works");
                Key(KeyO, Keys.ModAlt, 'o');
                Check(w.Focused == Lider.InspectWindow, "the new key did not move the focus to the inspector");
                // 検索の命令の行に、今の key が出る
                search.Open(w, ">" + Toggle);
                Check(search.Results.Count > 0 && search.Results[0].Where.Contains("[Alt+O]"), "the search shows: " + (search.Results.Count > 0 ? search.Results[0].Where : ""));
                search.Cancel(w);
                // 案内にも出る
                var hints = "";
                foreach (var hint in HintRules.For(new HintContext { Target = HintTarget.Nothing }))
                    hints += hint.Key + "|";
                Check(hints.Contains("Alt+O|") && !hints.Contains("Alt+I|"), "the hints show: " + hints);
                // file の行から読み直すと、同じ割り当てになる
                Keymap.Reset();
                Check(Keymap.StrokeOf(Toggle) == "alt+i", "Reset did not put the key back");
                Keymap.Load(text!);
                Check(Keymap.StrokeOf(Toggle) == "alt+o" && Keymap.CommandsOf("alt+i") == "" && string.Join("\n", Keymap.Lines()) + "\n" == text, "reading the file back gave: " + string.Join(" / ", Keymap.Lines()));
                break;
            case 2:
                // Esc は、割り当てずにやめる
                Pick(w);
                Key(Keys.Escape, 0, 0);
                Check(Lider.KeyFor == null && !search.Shown && Keymap.StrokeOf(Toggle) == "alt+o", "Esc changed the key: " + Keymap.StrokeOf(Toggle));
                Check(Lider.Run("keys:reset") && Keymap.StrokeOf(Toggle) == "alt+i" && Keymap.Lines().Count == 0, "keys:reset did not put the keys back");
                break;
            case 3:
                Io.LoadText(Environment.GetEnvironmentVariable("LIDER_KEYS")!, out var after, out _, out _, out _);
                Check(after != null && after.Trim() == "", "the file after the reset: " + after);
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    // keys:set を呼び、検索の欄で命令を選ぶ
    static void Pick(Workspace w)
    {
        Check(Lider.Run("keys:set"), "keys:set did not run");
        Lider.Search.Type(w, Toggle);
        Check(Lider.Search.Results.Count > 0 && Lider.Search.Results[0].Command == Toggle, "the command is not the first result");
        Lider.Search.Commit(w);
    }

    static void Key(int key, int mods, int plain)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = mods == 0 ? plain : 0, Plain = plain });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
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

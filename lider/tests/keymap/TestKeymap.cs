// Changes key assignments.
//   - Choosing a command with the keys:set command assigns the next pressed key to that command, and the previous key is removed
//   - Character keys without modifiers, and the key that moves focus to the game, cannot be assigned
//   - Esc cancels without assigning
//   - Changes are written to the file, and reloading from those lines gives the same assignments
//   - Search and the hints show the current key
//   - The keys:reset command goes back to the defaults
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestKeymap
{
    const string Toggle = "inspector:toggle";
    // Key numbers are in order starting from A
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
                // Character keys without modifiers cannot be assigned
                Key(Keys.A, 0, 'a');
                Check(Lider.KeyFor == Toggle && search.Prompt.Contains("割り当てられない"), "a plain letter was taken: " + search.Prompt);
                // The key that moves focus to the game cannot be taken
                Key(Keys.F1, 0, 0);
                Check(Lider.KeyFor == Toggle && Keymap.StrokeOf("focus:game") == "f1", "the key of focus:game was taken");
                Key(KeyO, Keys.ModAlt, 'o');
                Check(Lider.KeyFor == null && !search.Shown, "still waiting after a key");
                Check(Keymap.StrokeOf(Toggle) == "alt+o" && Keymap.CommandsOf("alt+i") == "", "the keys after the change: " + Keymap.StrokeOf(Toggle) + " / " + Keymap.CommandsOf("alt+i"));
                break;
            case 1:
                Io.LoadText(Environment.GetEnvironmentVariable("LIDER_KEYS")!, out var text, out _, out _, out _);
                Check(text != null && text.Contains("alt+o " + Toggle + "\n") && text.Contains("alt+i\n"), "the file: " + text);
                // Works with the new key, and not with the previous key
                var shown = Lider.Inspector.Shown;
                var focused = w.Focused;
                Key(KeyI, Keys.ModAlt, 'i');
                Check(Lider.Inspector.Shown == shown && w.Focused == focused, "the old key still works");
                Key(KeyO, Keys.ModAlt, 'o');
                Check(w.Focused == Lider.InspectWindow, "the new key did not move the focus to the inspector");
                // The current key appears on the command's row in search
                search.Open(w, ">" + Toggle);
                Check(search.Results.Count > 0 && search.Results[0].Where.Contains("[Alt+O]"), "the search shows: " + (search.Results.Count > 0 ? search.Results[0].Where : ""));
                search.Cancel(w);
                // It also appears in the hints
                var hints = "";
                foreach (var hint in HintRules.For(new HintContext { Target = HintTarget.Nothing }))
                    hints += hint.Key + "|";
                Check(hints.Contains("Alt+O|") && !hints.Contains("Alt+I|"), "the hints show: " + hints);
                // Reloading from the file's lines gives the same assignments
                Keymap.Reset();
                Check(Keymap.StrokeOf(Toggle) == "alt+i", "Reset did not put the key back");
                Keymap.Load(text!);
                Check(Keymap.StrokeOf(Toggle) == "alt+o" && Keymap.CommandsOf("alt+i") == "" && string.Join("\n", Keymap.Lines()) + "\n" == text, "reading the file back gave: " + string.Join(" / ", Keymap.Lines()));
                break;
            case 2:
                // Esc cancels without assigning
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

    // Call keys:set and choose the command in the search field
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

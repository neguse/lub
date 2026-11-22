// Alt+D (game:compare) shows the saved version and the draft version of the game side by side (DraftGame).
//   - The backdrop screen is split evenly into left and right, with the saved version on the left and the draft version on the right
//   - The draft version copies the saved version's state when it starts, and afterwards advances by the same frames (the counts match)
//   - Text rewritten without saving goes only into the draft version (the saved version and the file on disk do not change)
//   - If the draft version cannot be compiled, its tag says it is showing "最後に通った版"
//   - The version whose values lider looks at becomes the version of the game image window that last had focus
//   - Calling it again, or the draft version's close mark, goes back to the saved version only
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestCompare
{
    const int Delete = 76;
    static int frames;
    static int stage;
    static int copiedAt;
    static int savedBefore;
    static int draftBefore;
    static Document? doc;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
        Lider.OnInit();
        // Check in the overlay arrangement (the code panels laid out in a single row on the cylinder screen in space)
        Lider.Arrange(Lider.Overlay);
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
        Lider.OnFrame(dt);
        // Wait for the game build
        if (GameApp.Frames == 0)
            return;
        frames++;
        if (frames > 1200)
            Fail("stopped at stage " + stage);
        var w = Lider.Work!;
        if (stage == 0 && Count(GameApp.Ref) >= 30)
        {
            Lider.Run("game:compare");
            stage = 1;
        }
        else if (stage == 1 && DraftGame.Copies > 0 && DraftGame.Ref != null)
        {
            var windows = Lider.Screens[0].Windows;
            Check(windows.Count == 2, "want 2 game windows, got " + windows.Count);
            var left = windows[0].ViewportRect!;
            var right = windows[1].ViewportRect!;
            Check(left.W > 0 && Math.Abs(left.W - right.W) <= 1 && left.X < right.X && Math.Abs(left.X + left.W - right.X) <= 1,
                "the windows are not side by side: " + left.X + "+" + left.W + " and " + right.X + "+" + right.W);
            // While side by side, both images get a title band, and each image fits below it
            var body = windows[1].ContentRect!;
            Check(windows[0].TitleBar > 0 && body.Y == right.Y + windows[1].TitleBar && Math.Abs(body.H + windows[1].TitleBar - right.H) <= 1,
                "the picture is not under the title bar: " + body.Y + "+" + body.H + " in " + right.Y + "+" + right.H);
            copiedAt = Count(GameApp.Ref);
            stage = 2;
        }
        else if (stage == 2)
        {
            // The frame after the copy (where both have advanced 1 frame)
            var draft = Count(DraftGame.Ref);
            var saved = Count(GameApp.Ref);
            Check(draft >= 30 && draft == saved, "the draft is not in step with the saved version: draft " + draft + ", saved " + saved + " (copied at " + copiedAt + ")");
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            doc = w.Focus;
            Replace("return 1;", "1", "100");
            stage = 3;
        }
        else if (stage == 3 && DraftGame.Build != null && DraftGame.Build.LastKind == "batch")
        {
            savedBefore = Count(GameApp.Ref);
            draftBefore = Count(DraftGame.Ref);
            frames = 0;
            stage = 4;
        }
        else if (stage == 4 && frames == 10)
        {
            var saved = Count(GameApp.Ref) - savedBefore;
            var draft = Count(DraftGame.Ref) - draftBefore;
            Check(saved > 0 && saved < 20, "the saved version should keep its speed: " + saved + " in 10 frames");
            Check(draft >= 500, "the draft should run the unsaved edit: " + draft + " in 10 frames");
            Check(doc!.Dirty, "the edit should stay unsaved");
            Io.LoadText(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!, out var disk, out _, out _, out _);
            Check(disk != null && disk.Contains("return 1;"), "the file on disk changed");
            Check(DraftGame.Label() == "編集中", "label " + DraftGame.Label());
            Replace("return 100;", "100", "100 +");
            stage = 5;
        }
        else if (stage == 5 && DraftGame.Build!.LastKind == "error")
        {
            Check(DraftGame.Label() == "編集中 (変換エラー: 最後に通った版)", "label " + DraftGame.Label());
            // Pressing the draft version's title moves focus to it, and pressing its close mark stops the side-by-side view. The saved version's title has no close mark
            var saved = Lider.Screens[0].Windows[0].ViewportRect!;
            var draft = Lider.Screens[0].Windows[1].ViewportRect!;
            // On a small display the Code window overlaps the game image's title, so move the Code screen down out of the way (the nearer title gets pressed first)
            w.CodeScreen.Shift(0, -3);
            // The right end is the move-to-Space mark, so press to its left
            var bar = Lider.Screens[0].Windows[0].TitleBar;
            Click(saved.X + saved.W - bar * 1.5f, saved.Y + bar * 0.5f);
            Check(DraftGame.Shown && w.Focused == Lider.Screens[0].Windows[0], "a click near the right end of the saved title closed something or did not focus it");
            Click(draft.X + draft.W * 0.5f, draft.Y + bar * 0.5f);
            Check(DraftGame.Shown && w.Focused == Lider.Screens[0].Windows[1], "a click on the draft title did not move the focus");
            Check(Watched.Version == GameVersion.Saved, "lider watched the draft before the focus moved to it");
            stage = 51;
        }
        else if (stage == 51)
        {
            // lider looks at the version that has focus: values come from the draft version, and the version name appears in the inspector title
            var watched = -1;
            foreach (var n in Watched.Inspect("Game", 0, 100))
                if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                    watched = count;
            Check(Watched.Version == GameVersion.Draft, "lider does not watch the focused draft");
            Check(watched == Count(DraftGame.Ref) && watched != Count(GameApp.Ref), "the watched count " + watched + " is not the draft's " + Count(DraftGame.Ref) + " (saved " + Count(GameApp.Ref) + ")");
            Check(Lider.TitleOf(Lider.InspectWindow).Label.Contains("編集中"), "the inspector title does not name the draft: " + Lider.TitleOf(Lider.InspectWindow).Label);
            var draft = Lider.Screens[0].Windows[1].ViewportRect!;
            var bar = Lider.Screens[0].Windows[0].TitleBar;
            Click(draft.X + draft.W - bar * 0.5f, draft.Y + bar * 0.5f);
            Check(w.Focused == Lider.Screens[0].Windows[0], "closing the focused draft did not move the focus to the saved version");
            stage = 6;
        }
        else if (stage == 6)
        {
            Check(Watched.Version == GameVersion.Saved, "lider still watches the closed draft");
            Check(Lider.Screens[0].Windows.Count == 1 && !DraftGame.Shown, "the close mark did not go back to the saved version");
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    static void Click(float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = x, Y = y });
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = x, Y = y });
    }

    static void Press(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = 0, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
    }

    // Retypes from as to in line (without saving)
    static void Replace(string line, string from, string to)
    {
        var lines = doc!.Lines();
        var at = -1;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains(line))
                at = i;
        var col = Utf8.IndexOf(doc.Text(at), from);
        EditorHost.Focus(doc.Id);
        doc.MoveTo(at, col);
        for (var i = 0; i < Utf8.Bytes(from); i++)
        {
            EditorHost.Key(Delete, true);
            EditorHost.Key(Delete, false);
        }
        doc.Type(to);
    }

    // Current value of the game's Count (-1 if it cannot be read)
    static int Count(AppRef? app)
    {
        if (app == null)
            return -1;
        foreach (var n in Sys.Inspect("Game", 0, 100, app))
            if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                return count;
        return -1;
    }

    static void Check(bool ok, string message)
    {
        if (!ok)
            Fail(message);
    }

    static void Fail(string message)
    {
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

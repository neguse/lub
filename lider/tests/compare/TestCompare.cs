// F10 で、保存した版と編集中の版のゲームを並べる (DraftGame)。
//   - 背景のスクリーンが左右に等分され、左に保存した版、右に編集中の版が出る
//   - 編集中の版は、動き始めた時に保存した版の状態を写し、その後は同じ frame を進む (数が同じ)
//   - 保存せずに書き換えた本文は、編集中の版だけに入る (保存した版と disk の file は変わらない)
//   - 編集中の版が変換できないと、札が「最後に通った版」を出していると書く
//   - もう一度 F10 で、保存した版だけに戻る
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
        // ゲームの build を待つ
        if (GameApp.Frames == 0)
            return;
        frames++;
        if (frames > 1200)
            Fail("stopped at stage " + stage);
        var w = Lider.Work!;
        if (stage == 0 && Count(GameApp.Ref) >= 30)
        {
            Press(Keys.F10);
            stage = 1;
        }
        else if (stage == 1 && DraftGame.Copies > 0 && DraftGame.Ref != null)
        {
            var windows = Lider.Screens[0].Windows;
            Check(windows.Count == 2, "want 2 game windows, got " + windows.Count);
            var left = windows[0].ViewportBounds!;
            var right = windows[1].ViewportBounds!;
            Check(left.W > 0 && Math.Abs(left.W - right.W) <= 1 && left.X < right.X && Math.Abs(left.X + left.W - right.X) <= 1,
                "the windows are not side by side: " + left.X + "+" + left.W + " and " + right.X + "+" + right.W);
            copiedAt = Count(GameApp.Ref);
            stage = 2;
        }
        else if (stage == 2)
        {
            // 写した次の frame (どちらも 1 frame 進んだ所)
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
            Press(Keys.F10);
            stage = 6;
        }
        else if (stage == 6)
        {
            Check(Lider.Screens[0].Windows.Count == 1 && !DraftGame.Shown, "F10 did not go back to the saved version");
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    static void Press(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = 0, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
    }

    // line の中の from を to に打ち直す (保存しない)
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

    // ゲームの Count の今の値 (読めなければ -1)
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

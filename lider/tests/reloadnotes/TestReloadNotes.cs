// field の初期値を変えて保存すると、ゲームの reload の知らせ (Lider.GameReloadNotes) が届き、その field を宣言した窓の下端に「Shift+F5 で作り直すと入る」と出る。
// 次の reload が当たると消える。
// 窓で game/Target.cs (lider が開くゲームの build に入る) を書き換えて保存するので、lub の watch と tcs を通る。
// 書き換えるのは、ゲームが動き始めてから。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestReloadNotes
{
    const int Delete = 76;
    static int frames;
    static int stage;
    static int reloads;
    static float waited;
    static bool failed;
    static Document? doc;

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
        if (failed || frames < 3)
            return;
        var w = Lider.Work!;
        waited += dt;
        // ゲームの build を待つ
        if (GameApp.Frames == 0)
        {
            Check(waited < 60, "the game did not start in 60 seconds");
            return;
        }
        Check(waited < 60, "stage " + stage + " did not finish in 60 seconds (" + w.InitChanged.Count + " notes, " + Lider.Reloads + " reloads)");
        if (stage == 0)
        {
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            doc = w.Focus!;
            Lider.Search.Refresh(w);
            Replace("Hp = 40", "40", "100");
            stage = 1;
            waited = 0;
            return;
        }
        if (stage == 1)
        {
            if (w.InitChanged.Count == 0)
                return;
            var notices = Lider.InitNotices(w, w.FocusWindow!);
            Check(w.InitChanged.Count == 1 && w.InitChanged[0] == "ReloadNotesTarget.Hp", "got the notes " + string.Join(", ", w.InitChanged));
            Check(notices.Count == 1 && notices[0].StartsWith("4: ReloadNotesTarget.Hp ") && notices[0].Contains("Shift+F5"), "the window shows " + string.Join(", ", notices));
            if (failed)
                return;
            reloads = Lider.Reloads;
            Replace("Hp * 2", "2", "3");
            stage = 2;
            waited = 0;
            return;
        }
        if (Lider.Reloads == reloads)
            return;
        Check(w.InitChanged.Count == 0, "the note stayed after the next reload");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // line の中の from を to に打ち直して保存する
    static void Replace(string line, string from, string to)
    {
        var at = IndexOf(doc!.Lines(), line);
        var col = Utf8.IndexOf(doc.Text(at), from);
        EditorHost.Focus(doc.Id);
        doc.MoveTo(at, col);
        for (var i = 0; i < Utf8.Bytes(from); i++)
        {
            EditorHost.Key(Delete, true);
            EditorHost.Key(Delete, false);
        }
        doc.Type(to);
        doc.Save();
    }

    static int IndexOf(List<string> lines, string part)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains(part))
                return i;
        return -1;
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

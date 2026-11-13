// 保存した版が compile error なら、ゲームの build の診断 (Lider.GameBuildFailed) で error が窓の file と行に付き、直した版の reload が当たると消える。
// 窓で game/Target.cs (lider が開くゲームの build に入る) を書き換えて保存するので、lub の watch と tcs を通る。
// 書き換えるのは、ゲームが動き始めてから。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestBuildError
{
    const int Delete = 76;
    static int frames;
    static int stage;
    static float waited;
    static bool failed;
    static Document? doc;
    static int line;
    static int reloads;

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
        Check(waited < 60, "stage " + stage + " did not finish in 60 seconds (" + w.Errors.Count + " errors, " + Lider.Reloads + " reloads)");
        if (stage == 0)
        {
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            doc = w.Focus!;
            line = IndexOf(doc.Lines(), "return 1;");
            reloads = Lider.Reloads;
            Replace(line, "1", "missing");
            stage = 1;
            waited = 0;
            return;
        }
        if (stage == 1)
        {
            if (w.Errors.Count == 0)
                return;
            var mine = w.ErrorsIn(doc!.Path);
            Check(w.Failures == 1 && mine.Count == 1, "got " + w.Errors.Count + " errors, " + mine.Count + " in " + doc.Path + " (" + (w.Errors.Count > 0 ? w.Errors[0].Path : "") + ")");
            if (failed)
                return;
            Check(Lider.Reloads == reloads, "the version with the error was reloaded");
            Check(mine[0].Line == line && mine[0].Message.Contains("'missing'"), "the error is on line " + mine[0].Line + ": " + mine[0].Message);
            Replace(line, "missing", "2");
            stage = 2;
            waited = 0;
            return;
        }
        if (w.Errors.Count > 0 || Lider.Reloads == reloads)
            return;
        Check(w.Failures == 1, "the fixed save failed again");
        Check(Value() == 2, "the game reads " + Value() + " from the fixed version, want 2");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // ゲームの Value の今の値 (読めなければ -1)
    static int Value()
    {
        foreach (var n in GameApp.Inspect("Game", 0, 100))
            if (n.Name == "Value" && int.TryParse(n.Text, out var value))
                return value;
        return -1;
    }

    // line 行目の from を to に打ち直して保存する
    static void Replace(int line, string from, string to)
    {
        var col = Utf8.IndexOf(doc!.Text(line), from);
        EditorHost.Focus(doc.Id);
        doc.MoveTo(line, col);
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

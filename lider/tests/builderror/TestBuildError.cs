// If the saved version has a compile error, the game build's diagnostics (Lider.GameBuildFailed) attach the error to the panel's file and line, and it disappears when the reload of the fixed version is applied.
// The panel rewrites and saves game/Target.cs (part of the build of the game lider opens), so it goes through lub's watch and tcs.
// The rewrite happens after the game starts running.
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
        // Wait for the game build
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

    // Current value of the game's Value (-1 if it cannot be read)
    static int Value()
    {
        foreach (var n in GameApp.Inspect("Game", 0, 100))
            if (n.Name == "Value" && int.TryParse(n.Text, out var value))
                return value;
        return -1;
    }

    // Retypes from as to on line line and saves
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

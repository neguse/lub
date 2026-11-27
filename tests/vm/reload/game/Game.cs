// Checks hot reload in the tcs VM player (lub Game.csproj) (tests/lub-test copies it to a temporary directory and runs it).
// Rewriting the body of Advance, which OnFrame calls, applies the diff while keeping the state, and OnReload is called.
// On a save that changes the initializer of an instance field, a notice reaches OnReloadNotes after OnReload, and live instances keep their current values.
// On a save with a compile error, the diagnostics reach OnBuildFailed, and the previous code keeps running.
// The player logs `lub: reloaded` each time it applies a diff, and `lub: reload failed: ...` on a save with a compile error (../expect.txt).
// The search strings are written split up so that each statement to rewrite appears in only one place in this file.
using System;
using Lub;

public class Tree
{
    public int Hp = 40;
}

public static class Game
{
    static int Frames;
    static int Step = 1;
    static int Reloads;
    static string Failed = "";
    static string Notes = "";
    static int ReloadsAtNotes;
    static Tree Planted = new Tree();
    static int phase;
    static int framesAtSave;

    static void Fail(string message)
    {
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    static void Edit(string from, string to)
    {
        string path = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        Io.LoadText(path, out var text, out _, out _, out _);
        if (text == null || !text.Contains(from))
        {
            Fail("cannot edit " + path);
            return;
        }
        Io.SaveText(path, text.Replace(from, to));
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    static void Advance()
    {
        Frames += Step;
    }

    public static void OnReload() { Reloads++; }

    public static void OnBuildFailed(string errors) { Failed = errors; }

    public static void OnReloadNotes(string notes)
    {
        Notes = notes;
        ReloadsAtNotes = Reloads;
    }

    public static void OnFrame(float dt)
    {
        Advance();
        if (phase == 0 && Frames >= 5)
        {
            framesAtSave = Frames;
            Edit("Frames += " + "Step;", "Frames += " + "Step * 100;");
            phase = 1;
        }
        else if (phase == 1 && Reloads > 0)
        {
            if (Reloads != 1)
                Fail("OnReload ran " + Reloads + " times");
            else if (Frames < framesAtSave + 100)
                Fail("the state was lost or the new code did not run: " + Frames + " after " + framesAtSave);
            Edit("public int Hp = " + "40;", "public int Hp = " + "100;");
            phase = 2;
        }
        else if (phase == 2 && Notes != "")
        {
            if (!Notes.Contains("Tree.Hp"))
                Fail("OnReloadNotes did not get the note: '" + Notes + "'");
            else if (ReloadsAtNotes != 2)
                Fail("OnReloadNotes ran with " + ReloadsAtNotes + " reloads, not after the second OnReload");
            else if (Planted.Hp != 40 || new Tree().Hp != 100)
                Fail("the initializer change: planted " + Planted.Hp + ", new " + new Tree().Hp);
            else
            {
                Edit("Frames += " + "Step * 100;", "Frames += " + "oops;");
                phase = 3;
            }
        }
        else if (phase == 3 && Failed != "")
        {
            if (!Failed.Contains("oops"))
                Fail("OnBuildFailed did not get the error: '" + Failed + "'");
            else
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
        else if (Sys.Seconds() > 60)
            Fail("timeout in phase " + phase);
    }
}

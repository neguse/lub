// tcs VM の player (lub_vm Game.csproj) の overlay と hot reload の検査 (scripts/vm-project-test.sh が一時ディレクトリに写して動かす)。
// overlay の OnInit はゲームの後に呼ばれる。
// ゲームの OnFrame の本文を書き換えると、ゲームの状態を保ったまま差分が当たり、ゲームと overlay の OnReload が呼ばれる。
// compile error の保存では、ゲームと overlay の OnBuildFailed に診断が届き、ゲームは前のコードのまま動く。
using System;
using Lub;

public static class Tool
{
    static int phase;
    static int reloads;
    static int framesAtSave;
    static string failed = "";

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
        if (!Game.Inited)
            Fail("the overlay OnInit ran before the game OnInit");
    }

    public static void OnReload() { reloads++; }

    public static void OnBuildFailed(string errors) { failed = errors; }

    public static void OnFrame(float dt)
    {
        if (phase == 0 && Game.Frames >= 5)
        {
            framesAtSave = Game.Frames;
            Edit("Frames += Step;", "Frames += Step * 100;");
            phase = 1;
        }
        else if (phase == 1 && reloads > 0)
        {
            if (Game.Reloads != 1)
                Fail("the game OnReload ran " + Game.Reloads + " times");
            else if (Game.Frames < framesAtSave)
                Fail("the game state was lost: " + Game.Frames + " < " + framesAtSave);
            Edit("Frames += Step * 100;", "Frames += oops;");
            phase = 2;
        }
        else if (phase == 2 && failed != "")
        {
            if (!failed.Contains("oops") || !Game.Failed.Contains("oops"))
                Fail("OnBuildFailed did not get the error: '" + failed + "' / '" + Game.Failed + "'");
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

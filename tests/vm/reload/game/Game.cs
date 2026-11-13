// tcs VM の player (lub Game.csproj) の hot reload の検査 (scripts/vm-project-test.sh が一時ディレクトリに写して動かす)。
// OnFrame が呼ぶ Advance の本文を書き換えると、状態を保ったまま差分が当たり、OnReload が呼ばれる。
// instance の field の初期化子を変えた保存では、OnReload の後に OnReloadNotes に知らせが届き、生きている instance は今の値のまま。
// compile error の保存では、OnBuildFailed に診断が届き、前のコードのまま動く。
// player は差分を当てるたびに `lub: reloaded`、compile error の保存では `lub: reload failed: ...` を log に出す (../expect.txt)。
// 書き換える文は、この file の中で 1 か所だけに現れるように、探す文字列を分けて書く。
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

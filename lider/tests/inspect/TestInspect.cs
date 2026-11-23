// Checks the inspector rows (InspectTree). The rows are made from the classes and values of the game (game/Game.cs).
//   - The roots are the game's classes, with the entry (Game) first. Child rows follow only for open nodes
//   - A list whose elements are all instances of the same class becomes a table with a header row (the columns are the fields of the first element) and one row per element. A missing field is ""
//   - A list with more than ChunkSize elements is split into nodes of ChunkSize each, and elements follow only for open chunks
//   - When a value that has not changed for a while changes, Glow is set, and a value that changes every frame does not set it
//   - The jump target of a row is the class for roots and instances, class.field for fields, and none for element values
// The game builds the list on the first frame and changes Shelf.Calm at a fixed frame. Glow is checked on the frame where the Calm change is seen.
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestInspect
{
    static int frames;
    static bool failed;
    static readonly InspectTree tree = new InspectTree();

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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    static void TestFrame(float dt)
    {
        // Wait for the game build
        if (failed || GameApp.Frames == 0)
            return;
        frames++;
        if (frames == 1)
        {
            tree.Refresh();
            Check(Names(0) == "Game", "the first roots are " + Names(0));
            Check(Find("Shelf") >= 0 && !tree.Rows[Find("Shelf")].Open, "Shelf is not a closed root");
            tree.Toggle("Shelf");
            tree.Toggle("Shelf.Crates");
            tree.Toggle("Shelf.Many");
            tree.Toggle("Shelf.Many#2");
            tree.Refresh();
            var at = Find("Shelf.Crates#columns");
            Check(at >= 0, "the crates are not a table");
            if (at < 0)
                return;
            var header = tree.Rows[at];
            Check(header.Name == "Crate" && string.Join(" ", header.Cells!) == "Hp Name Next", "the columns are " + string.Join(" ", header.Cells!));
            var first = tree.Rows[at + 1];
            Check(first.Key == "Shelf.Crates[0]" && string.Join("|", first.Cells!) == "3|\"a\"|Crate", "the first crate is " + string.Join("|", first.Cells!));
            Check(first.Target == "Crate", "the crate row goes to " + first.Target);
            Check(tree.Rows[Find("Shelf")].Target == "Shelf", "the root goes to " + tree.Rows[Find("Shelf")].Target);
            Check(tree.Rows[Find("Shelf.Ticks")].Target == "Shelf.Ticks", "the static field goes to " + tree.Rows[Find("Shelf.Ticks")].Target);
            var last = tree.Rows[at + 3];
            Check(last.Key == "Shelf.Crates[2]" && string.Join("|", last.Cells!) == "9|\"\"|", "the last crate is " + string.Join("|", last.Cells!));
            var chunks = Find("Shelf.Many#0");
            Check(chunks >= 0 && tree.Rows[chunks].Name == "[0…99]" && tree.Rows[chunks + 2].Name == "[200…249]", "Many is not split by 100");
            Check(tree.Rows[chunks + 3].Name == "[200]" && tree.Rows[chunks + 3].Text == "400" && tree.Rows[chunks + 3].Depth == tree.Rows[chunks].Depth + 1, "the opened chunk does not start with Many[200]");
            Check(Find("Shelf.Many[249]") == chunks + 52, "the opened chunk does not hold 50 items");
            Check(tree.Rows[Find("Shelf.Many[249]")].Target == "", "an item of ints has a target: " + tree.Rows[Find("Shelf.Many[249]")].Target);
        }
        if (frames >= 2)
            tree.Refresh();
        if (failed || Find("Shelf.Calm") < 0)
            return;
        // Wait until the game changes Calm
        if (tree.Rows[Find("Shelf.Calm")].Text != "1")
        {
            Check(frames < 600, "Shelf.Calm did not change");
            return;
        }
        Check(frames > ValueFlash.SteadyFrames, "Shelf.Calm changed before it stayed long enough");
        Check(tree.Rows[Find("Shelf.Calm")].Glow > 0.9f, "a value that changed after a while does not glow");
        Check(tree.Rows[Find("Shelf.Ticks")].Glow == 0, "a value that changes every frame glows");
        if (!failed)
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    static int Find(string key)
    {
        for (var i = 0; i < tree.Rows.Count; i++)
            if (tree.Rows[i].Key == key)
                return i;
        return -1;
    }

    static string Names(int at)
    {
        return at < tree.Rows.Count ? tree.Rows[at].Name : "";
    }

    // Report only the first failure and exit
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

// インスペクタの行 (InspectTree) を確かめる。行にするのはゲーム (game/Game.cs) の class と値。
//   - 根はゲームの class で、entry (Game) が先頭。開いた節だけ子の行が続く
//   - 要素が同じ class の instance だけの並びは、見出しの行 (列は最初の要素の field) と 1 要素 1 行の表になる。無い field は ""
//   - 要素が ChunkSize を超える並びは ChunkSize ずつの節に区切り、開いた区切りだけ要素が続く
//   - しばらく変わらなかった値が変わると Glow が立ち、毎 frame 変わる値は立たない
//   - 行の飛ぶ先は、根と instance は class、field は class.field、要素の値は無し
// ゲームは最初の frame で並びを作り、決めた frame で Shelf.Calm を変える。Calm が変わったのを見た frame で Glow を確かめる。
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
        // ゲームの build を待つ
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
        // ゲームが Calm を変えるまで待つ
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

    // 最初の失敗だけを出して終わる
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

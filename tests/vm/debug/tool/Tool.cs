// tcs VM の player の検査の口 (Sys.TakeTrace / TakeValues / Inspect / InspectHistory、Rewind) の検査。
// ゲームのソースは --trace で変換され、overlay (このディレクトリ) は変換されない。
// 止めている間に、生きている object を作り直す保存 (field を足す) をしても、止まったまま (控えはその時の 1 frame からやり直す)。
using System;
using System.Collections.Generic;
using Lub;

public static class Tool
{
    static int frame;
    static int pausedScore = -1;
    static int reloads;
    static int reloadedAt = -1;
    static bool failed;

    static void Fail(string message)
    {
        if (failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    static string Names(List<InspectNode> nodes)
    {
        var s = "";
        foreach (var n in nodes)
            s += n.Name + "=" + n.Text + "(" + n.Count + ") ";
        return s;
    }

    public static void OnInit()
    {
        Rewind.Record(30);
    }

    public static void OnReload() { reloads++; }

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

    public static void OnFrame(float dt)
    {
        if (failed)
            return;
        frame++;
        var lines = Sys.TakeTrace();
        var values = Sys.TakeValues();
        if (frame == 3)
        {
            bool line = false;
            foreach (var l in lines)
                if (l.EndsWith("Game.cs:32"))
                    line = true;
            if (!line)
                Fail("the trace did not have Game.cs:32: " + string.Join(" ", lines));
            bool score = false;
            bool local = false;
            foreach (var v in values)
            {
                if (v.Site.EndsWith(":9:Score") && v.Text == "3")
                    score = true;
                if (v.Site.EndsWith(":doubled") && v.Text == "6")
                    local = true;
            }
            if (!score || !local)
            {
                var all = "";
                foreach (var v in values)
                    all += v.Site + "=" + v.Text + " ";
                Fail("the values were " + all);
            }
            foreach (var l in lines)
                if (l.Contains("Tool.cs"))
                    Fail("the overlay was traced: " + l);
            var roots = Names(Sys.Inspect("", 0, 10));
            if (!roots.StartsWith("Game="))
                Fail("roots: " + roots);
            var game = Names(Sys.Inspect("Game", 0, 10));
            if (!game.Contains("Score=3(0)") || !game.Contains("P=Player(3)") || !game.Contains("Counts={") )
                Fail("Game: " + game);
            var player = Names(Sys.Inspect("Game.P", 0, 10));
            if (player != "At=Pos{X=6 Y=0}(2) Hp=6(0) Items=[1](1) ")
                Fail("Game.P: " + player);
            var items = Names(Sys.Inspect("Game.P.Items", 0, 10));
            if (items != "[0]=7(0) ")
                Fail("Game.P.Items: " + items);
            var counts = Names(Sys.Inspect("Game.Counts", 0, 10));
            if (counts != "[\"a\"]=1(0) ")
                Fail("Game.Counts: " + counts);
            var bindings = Names(Sys.Inspect("Game.B", 0, 10));
            if (bindings != "blob=<native>(0) ")
                Fail("Game.B: " + bindings);
            var node = Sys.Inspect("Game", 1, 1)[0];
            Sys.InspectPin("p", node.Path);
            if (!Sys.InspectPinned("p", node.Id))
                Fail("the pinned player was not found");
        }
        else if (frame == 20)
        {
            var history = Sys.InspectHistory("Game.Score", 5);
            if (history.Count != 5 || history[4] != 19 || history[0] != 15)
                Fail("history: " + string.Join(" ", history));
            Rewind.Pause(5);
        }
        else if (frame == 21)
        {
            if (Rewind.Position() != 5)
                Fail("position " + Rewind.Position());
            pausedScore = Game.Score;
        }
        else if (frame == 22)
        {
            // 止めている間は、毎 frame 同じ frame を作り直す
            if (Game.Score != pausedScore || pausedScore != 15)
                Fail("paused score " + Game.Score + " / " + pausedScore);
            Rewind.Pause(-1);
        }
        else if (frame == 24)
        {
            // 止めた frame の前の状態から続けるので、止めた frame (15) を作り直してから 2 frame 進んでいる
            if (Rewind.Position() != -1 || Game.Score != 16)
                Fail("after resume: position " + Rewind.Position() + " score " + Game.Score);
            Rewind.Pause(0);
            Edit("public int Hp = 3;", "public int Hp = 3;\n    public int Mp;");
        }
        else if (frame > 24 && reloadedAt < 0)
        {
            if (reloads > 0)
                reloadedAt = frame;
            else if (frame > 24 + 60 * 60)
                Fail("the save that adds a field did not reload");
        }
        else if (reloadedAt > 0 && frame == reloadedAt + 2)
        {
            if (Rewind.Position() != 0 || Rewind.Count() != 1)
                Fail("after a save that remade objects while paused: position " + Rewind.Position() + " count " + Rewind.Count());
            pausedScore = Game.Score;
        }
        else if (reloadedAt > 0 && frame == reloadedAt + 4)
        {
            if (Game.Score != pausedScore)
                Fail("the game ran while paused: " + pausedScore + " -> " + Game.Score);
            Rewind.Pause(-1);
        }
        else if (reloadedAt > 0 && frame == reloadedAt + 6)
        {
            if (Rewind.Position() != -1 || Game.Score != pausedScore + 1)
                Fail("after resume: position " + Rewind.Position() + " score " + Game.Score + " (paused at " + pausedScore + ")");
            else
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }
}

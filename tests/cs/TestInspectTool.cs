// TestInspect の overlay。
using System.Collections.Generic;
using Lub;

public static class TestInspectTool
{
    static int frames;
    static string pinned = "";

    public static void OnFrame(float dt)
    {
        frames++;
        var root = Sys.Inspect("", 0, 1);
        Check.That(root.Count == 1 && root[0].Path == "TestInspect", "the entry class is not the first root");

        var statics = Sys.Inspect("TestInspect", 0, 100);
        Check.That(Names(statics) == "Frames first crates named missing", "statics are " + Names(statics));
        Check.That(statics[0].Text == "" + TestInspect.Frames, "Frames is " + statics[0].Text);
        Check.That(statics[2].Text == "[3]" && statics[2].Count == 3, "crates is " + statics[2].Text + " with " + statics[2].Count);
        Check.That(statics[4].Text == "null" && statics[4].Count == 0, "missing is " + statics[4].Text);

        var tail = Sys.Inspect("TestInspect.crates", 1, 5);
        Check.That(Names(tail) == "[1] [2]", "crates from 1 are " + Names(tail));
        Check.That(tail[0].Path == "TestInspect.crates[1]" && tail[0].Type == "Crate", "crates[1] is " + tail[0].Path + " " + tail[0].Type);

        var b = Sys.Inspect("TestInspect.crates[1]", 0, 10);
        Check.That(Names(b) == "Hp Name Next", "the fields of crates[1] are " + Names(b));
        Check.That(b[0].Text == "5" && b[1].Text == "\"b\"", "crates[1] is " + b[0].Text + " " + b[1].Text);
        Check.That(!b[2].Cycle && b[2].Count == 3, "crates[1].Next is not open");

        // first → first.Next → first は祖先に戻るので開かない
        var back = Sys.Inspect("TestInspect.first.Next", 0, 10);
        Check.That(back.Count == 3 && back[2].Name == "Next" && back[2].Cycle && back[2].Count == 0, "the way back to first is not a cycle");

        var dict = Sys.Inspect("TestInspect.named", 0, 10);
        Check.That(Names(dict) == "[\"big one\"] small", "named is " + Names(dict));
        Check.That(dict[0].Path == "TestInspect.named[\"big one\"]", "the path is " + dict[0].Path);
        var big = Sys.Inspect(dict[0].Path, 0, 10);
        Check.That(big.Count == 2 && big[0].Text == "1", "named[\"big one\"] did not resolve");

        Check.That(Sys.Inspect("TestInspect.nothing.Hp", 0, 10).Count == 0, "a missing path returned nodes");

        // Id は同じ table なら道によらず同じで、別の table とは違う。table でなければ空
        var second = tail[0].Id;
        Check.That(second != "" && second == Sys.Inspect("TestInspect.first", 0, 10)[2].Id, "the same table has two ids");
        Check.That(second != tail[1].Id, "two tables have the same id");
        Check.That(b[0].Id == "", "a number has an id");

        // 覚えた table は Id で確かめられ、辿れない path で忘れる
        Sys.InspectPin("k", "TestInspect.crates[1]");
        Check.That(Sys.InspectPinned("k", second), "the pinned table is not found");
        Check.That(!Sys.InspectPinned("k", tail[1].Id), "a different id matched the pin");
        Check.That(!Sys.InspectPinned("other", second), "an unknown key matched");
        Sys.InspectPin("k", "TestInspect.nothing");
        Check.That(!Sys.InspectPinned("k", second), "the pin was not cleared");
        Check.That(Sys.Inspect("Nobody", 0, 10).Count == 0, "an unknown class returned nodes");
        if (frames == 2)
        {
            pinned = Sys.Inspect("Bin", 0, 1)[0].Id;
            Sys.InspectPin("gone", "Bin.Held");
            Check.That(Sys.InspectPinned("gone", pinned), "the held crate is not pinned");
            Bin.Held = null;
        }
        // 手放した table は覚えていても回収される
        if (frames >= 2 && !Sys.InspectPinned("gone", pinned))
            Check.Pass();
        Check.That(frames < 600, "the pin kept the table alive");
    }

    static string Names(List<InspectNode> nodes)
    {
        var s = "";
        foreach (var n in nodes)
            s += (s == "" ? "" : " ") + n.Name;
        return s;
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}

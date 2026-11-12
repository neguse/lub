// 検査の API (Sys.Inspect など) の app の引数: App.Use で開いた app の static を、開いた側から読む。
// app を省くと自分を見るので、同じ名前の static でも別の値が返る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestAppObserveChild
{
    static int counter = 100;
    static List<int> items = new List<int>();

    public static void OnFrame(float dt)
    {
        counter++;
        items.Add(counter);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
    }
}

public static class TestAppObserve
{
    static int counter = 7;
    static int frame;

    static string Text(List<InspectNode> nodes, string name)
    {
        foreach (var n in nodes)
        {
            if (n.Name == name) return n.Text;
        }
        return "(none)";
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", "TestAppObserveChild");
        if (frame == 0)
        {
            // まだ module を読み込んでいない app は空
            Check.That(Sys.Inspect("", 0, 10, child).Count == 0, "Inspect before the first Step must be empty");
        }
        Check.That(App.Step(child, rt, dt), $"frame {frame}: Step failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        frame++;
        // 開いた app の static
        var theirs = Sys.Inspect("TestAppObserveChild", 0, 10, child);
        Check.That(Text(theirs, "counter") == (100 + frame).ToString(), $"frame {frame}: child counter is {Text(theirs, "counter")}");
        var items = Sys.Inspect("TestAppObserveChild.items", 0, 100, child);
        Check.That(items.Count >= frame, $"frame {frame}: child items has {items.Count} nodes");
        // 開いた app の個体の一覧にも届く (class の個体は無いので空で、失敗しない)
        Sys.InspectInstances(4, 100, child);
        // app を省くと自分
        var mine = Sys.Inspect("TestAppObserve", 0, 10);
        Check.That(Text(mine, "counter") == "7", $"own counter is {Text(mine, "counter")}");
        Check.That(Text(Sys.Inspect("TestAppObserveChild", 0, 10), "counter") != (100 + frame).ToString(),
            "Inspect without an app must not read the opened app");
        if (frame == 5)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

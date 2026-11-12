// Lubx.AppTimeline: 開いた app の frame を控え、止めて戻り、続ける。
// TestAppTimelineChild は、積んだ値と mouse の位置から決まる色で main を塗る。
//   frame 0..7: 進める (mouse の位置は frame ごとに違う)。
//   frame 8..9: 3 個前 (frame 4) で止める。frame 4 と同じ色を描き続ける。
//   frame 10: 1 個前 (frame 6) で止める。frame 6 と同じ色。
//   frame 11..: 続ける。frame 6 の状態から、今の入力で進む。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class TestAppTimelineChild
{
    static int frame;
    static int sum;

    public static void OnFrame(float dt)
    {
        Input.MousePos(out var x, out _);
        sum = (sum * 5 + (int)x + 1) % 256;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { sum / 255.0f, frame / 255.0f, 0, 1 } });
        Gfx.EndPass();
        frame++;
    }
}

public static class TestAppTimeline
{
    const int frames = 16;
    static Readback? rb;
    static int frame;
    static AppTimeline timeline = new AppTimeline(8, 100);
    static int[] red = new int[frames];
    static int[] green = new int[frames];

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rb = Gfx.Readback("app_timeline");
        for (var i = 0; i < frames; i++) red[i] = -1;
    }

    // 止めずに進めたときの、開いた app の frame k の赤 (mouse の x は k % 5)
    static int Sum(int upTo, int[] xs)
    {
        var s = 0;
        for (var k = 0; k <= upTo; k++) s = (s * 5 + xs[k] + 1) % 256;
        return s;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", "TestAppTimelineChild");
        if (frame == 8)
        {
            Check.That(timeline.Count == 8, $"Count is {timeline.Count}, want 8");
            timeline.Pause(3);
            Check.That(timeline.Position == 3, $"Position is {timeline.Position}, want 3");
        }
        if (frame == 10) timeline.Pause(1);
        if (frame == 11)
        {
            timeline.Resume();
            Check.That(timeline.Position == -1, "Resume must leave the pause");
            Check.That(timeline.Count == 6, $"Count after Resume is {timeline.Count}, want 6");
        }
        // 止めている間に渡す入力は使われない (控えた入力で作り直す)
        Check.That(timeline.Step(child, rt, dt, new StepInput { MouseX = frame % 5, MouseY = 0 }), $"frame {frame}: Step failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Gfx.ReadTexture(rb!, rt, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (frame >= 2)
        {
            Check.That(st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}, want Ready");
            if (st != Gfx.ReadbackStatus.Ready) return;
            red[id] = bytes!.Get(0);
            green[id] = bytes.Get(1);
        }
        frame++;
        if (frame < frames) return;
        // 開いた app が見た mouse の x の列。frame 0..7 はそのまま、続けた後は frame 6 の状態に frame 11.. の入力が続く
        var xs = new int[frames];
        for (var k = 0; k < 8; k++) xs[k] = k % 5;
        for (var k = 0; k < 8; k++)
            Check.That(red[k] == Sum(k, xs) && green[k] == k, $"frame {k}: drew {red[k]},{green[k]}, want {Sum(k, xs)},{k}");
        for (var k = 8; k < 10; k++)
            Check.That(red[k] == red[4] && green[k] == 4, $"paused frame {k}: drew {red[k]},{green[k]}, frame 4 drew {red[4]},4");
        Check.That(red[10] == red[6] && green[10] == 6, $"paused frame 10: drew {red[10]},{green[10]}, frame 6 drew {red[6]},6");
        // 続けた後: 開いた app の frame 6, 7, 8 を、開いた側の frame 11, 12, 13 の入力で進める
        for (var k = 11; k < frames - 2; k++)
        {
            var childFrame = 6 + (k - 11);
            xs[childFrame] = k % 5;
            Check.That(red[k] == Sum(childFrame, xs) && green[k] == childFrame,
                $"resumed frame {k}: drew {red[k]},{green[k]}, want {Sum(childFrame, xs)},{childFrame}");
        }
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

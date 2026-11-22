// Lubx.AppTimeline: records the frames of an opened app, pauses and goes back, and resumes.
// TestAppTimelineChild fills main with a color determined by the accumulated values and the mouse position.
//   frame 0..7: advance (the mouse position differs per frame).
//   frame 8..9: pause 3 back (frame 4). Keeps drawing the same color as frame 4.
//   frame 10: pause 1 back (frame 6). Same color as frame 6.
//   frame 11..: resume. Advances from the state of frame 6 with the current input.
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

    // The red of frame k of the opened app when advanced without pausing (mouse x is k % 5)
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
        // Input passed while paused is not used (frames are recreated from the recorded input)
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
        // The sequence of mouse x seen by the opened app. Frames 0..7 as is; after resuming, the input of frames 11.. follows the state of frame 6
        var xs = new int[frames];
        for (var k = 0; k < 8; k++) xs[k] = k % 5;
        for (var k = 0; k < 8; k++)
            Check.That(red[k] == Sum(k, xs) && green[k] == k, $"frame {k}: drew {red[k]},{green[k]}, want {Sum(k, xs)},{k}");
        for (var k = 8; k < 10; k++)
            Check.That(red[k] == red[4] && green[k] == 4, $"paused frame {k}: drew {red[k]},{green[k]}, frame 4 drew {red[4]},4");
        Check.That(red[10] == red[6] && green[10] == 6, $"paused frame 10: drew {red[10]},{green[10]}, frame 6 drew {red[6]},6");
        // After resuming: advances frames 6, 7, 8 of the opened app with the input of the opener's frames 11, 12, 13
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

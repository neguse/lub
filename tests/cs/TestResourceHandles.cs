// resource は、key を宣言し続けている間は引け続け、sweep された後は引けなくなる。
// key を毎フレーム作って捨てる書き方でも、生きている key は変わらない。
// handle の数値そのものは C# に出ていないので、handle の再利用や stale の検査は key からの引き直し (LookupBuffer) で見る。
// 表の大きさが伸びないことは tests/c/resources_smoke.c が見る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestResourceHandles
{
    const int sweep = 2;
    const int frames = 60;
    const int perFrame = 50;

    static int frame;
    static BufferRef? keepRef;
    static readonly List<float> data = new List<float> { 0.0f, 0.0f, 0.0f };
    static readonly List<List<string>> history = new List<List<string>>();

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", ResourceSweepAfterFrames = sweep });
    }

    public static void OnFrame(float dt)
    {
        frame++;

        var keep = Gfx.UseBuffer("handles_keep", Gfx.BufferType.Storage, data, 1);
        Check.That(keep != null, "a live key must be declared");
        if (keepRef == null)
            keepRef = keep;

        var keys = new List<string>();
        history.Add(keys);
        for (int i = 1; i <= perFrame; i++)
        {
            var key = "handles_churn_" + frame + "_" + i;
            var r = Gfx.UseBuffer(key, Gfx.BufferType.Storage, data, 1);
            Check.That(r != null, "churn key " + key + " must be declared");
            keys.Add(key);
        }

        // sweep + 2 frame より前に宣言した key は sweep 済み
        int old = frame - sweep - 2;
        if (old >= 1)
        {
            foreach (var key in history[old - 1])
                Check.That(Gfx.LookupBuffer(key) == null, "swept key " + key + " must not resolve");
        }
        // 生きている key は引ける
        Check.That(Gfx.LookupBuffer("handles_keep") != null, "the live key must resolve");
        foreach (var key in keys)
            Check.That(Gfx.LookupBuffer(key) != null, "live key " + key + " must resolve");

        if (frame >= frames)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

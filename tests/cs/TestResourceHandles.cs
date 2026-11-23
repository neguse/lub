// A resource can be looked up as long as its key keeps being declared, and can no longer be looked up after it is swept.
// Even code that creates and discards key strings every frame does not change which keys are alive.
// Handle numbers themselves are not exposed to C#, so handle reuse and staleness are checked by looking up the key again (LookupBuffer).
// tests/c/resources_smoke.c checks that the table does not grow.
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

        // Keys declared before sweep + 2 frames have been swept
        int old = frame - sweep - 2;
        if (old >= 1)
        {
            foreach (var key in history[old - 1])
                Check.That(Gfx.LookupBuffer(key) == null, "swept key " + key + " must not resolve");
        }
        // Live keys can be looked up
        Check.That(Gfx.LookupBuffer("handles_keep") != null, "the live key must resolve");
        foreach (var key in keys)
            Check.That(Gfx.LookupBuffer(key) != null, "live key " + key + " must resolve");

        if (frame >= frames)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

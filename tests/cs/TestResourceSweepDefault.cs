// Even if ResourceSweepAfterFrames is not specified in Config, a resource whose declarations stopped for 120 frames is swept.
// A resource declared every frame remains no matter how many frames pass.
// Frames in which OnFrame ended with an error are not counted toward the sweep.
using System;
using System.Collections.Generic;
using Lub;

public static class TestResourceSweepDefault
{
    const int sweep = 120;
    const int errorFrames = 200;

    static int frame;
    static readonly List<float> data = new List<float> { 1.0f, 2.0f, 3.0f, 4.0f };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        var kept = Gfx.UseBuffer("sweep_default_kept", Gfx.BufferType.Storage, data, 1);
        Check.That(kept != null, "a buffer declared every frame must be declared");

        if (frame == 1)
        {
            // Declare only in this frame
            Gfx.UseBuffer("sweep_default_dropped", Gfx.BufferType.Storage, data, 1);
            Check.That(Audio.Snd("sweep_default_snd", data, 1, 48000, 1) != null, "snd must be declared");
            return;
        }
        if (frame <= 1 + errorFrames)
        {
            Check.Error("an error frame", "data is required");
            Gfx.UseBuffer("sweep_default_error", Gfx.BufferType.Storage, null);
            return;
        }

        // Number of frames that ended without a declaration (excluding frames that ended with an error)
        int ended = frame - 2 - errorFrames;
        bool present = Gfx.LookupBuffer("sweep_default_dropped") != null;
        if (ended <= sweep)
        {
            Check.That(present, "an undeclared buffer must stay for " + sweep + " counted frames (frame " + frame + ")");
            return;
        }
        Check.That(!present, "an undeclared buffer must be swept after " + sweep + " counted frames");
        Check.That(Gfx.UseBuffer("sweep_default_dropped", Gfx.BufferType.Storage, null, 1) == null, "a swept buffer declared without data must be absent");
        Check.That(Audio.Snd("sweep_default_snd", null, 1, 48000, 1) == null, "a swept snd declared without data must be absent");
        Check.That(Gfx.UseBuffer("sweep_default_kept", Gfx.BufferType.Storage, null, 1) != null, "a buffer declared every frame must stay");
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

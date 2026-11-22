// Shaders with the same key refer to the same thing.
// A declaration with the same version returns the same ref, and a different version in the same frame is an error.
// A different version in the next frame is an edit, so it is recreated; this also works in the frame after one that ended with an error.
// A declaration that failed to compile is also a declaration; declaring the same version again is not a conflict and does not recompile.
using System;
using Lub;

public static class TestShaderKeyConflict
{
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        Io.LoadText("tests/cs/data/test_swept_ref_error.vs.slang", out var vs, out _, out _, out _);
        Io.LoadText("tests/cs/data/test_swept_ref_error.fs.slang", out var fs, out _, out _, out _);
        if (vs == null || fs == null)
        {
            frame--;
            return;
        }
        if (frame == 5 || frame == 6)
        {
            // A failed version is not recompiled, so "recompile failed" appears in the log only once.
            var kept = Gfx.UseShader("conflict_sh", "broken", "broken", 105);
            var again = Gfx.UseShader("conflict_sh", "broken", "broken", 105);
            Check.That(kept != null && kept.Version == 4 && again != null && again.Version == 4,
                "a failed compile must keep the old shader without a conflict");
            return;
        }
        if (frame == 7)
        {
            Check.Pass();
            return;
        }
        var first = Gfx.UseShader("conflict_sh", vs, fs, frame);
        var second = Gfx.UseShader("conflict_sh", vs, fs, frame);
        Check.That(first != null && first.Version == frame && second != null && second.Version == frame, "the same version must return the same shader");
        if (frame == 3)
        {
            Check.Error("a different version in the same frame must fail", "different versions");
            Gfx.UseShader("conflict_sh", vs, fs, frame + 100);
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

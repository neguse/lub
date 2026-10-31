// 同じ key の shader は同じものを指す。
// 同じ version の宣言は同じ ref を返し、同じフレームの違う version は error になる。
// 次のフレームの違う version は編集なので作り直す。
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
        if (frame == 4)
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

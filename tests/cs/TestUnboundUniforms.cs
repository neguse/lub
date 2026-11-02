// uniform block を持つ shader を uniform を渡さずに draw しても、draw は止まらず落ちずに描けること。
// - フレームの最初の draw で渡さない。
// - uniform block の無い shader で描いた直後に渡さない。
// 渡さなかった uniform block の値は backend ごとに違ってよいので (TestUnboundBindings の仮の resource と同じ)、texture が決める R だけを確かめる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestUnboundUniforms
{
    const int c = 80;

    static readonly string[] names = new string[] { "first", "after_other" };
    static readonly List<Readback> readbacks = new List<Readback>();
    static readonly bool[] done = new bool[] { false, false };
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (int i = 0; i < names.Length; i++)
            readbacks.Add(Gfx.Readback(names[i])!);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "readback never became ready");

        Io.LoadText("tests/cs/data/test_unbound_uniforms.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_uniforms.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_bindings.vs.slang", out var vs2, out var verVs2, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_bindings.fs.slang", out var fs2, out var verFs2, out _, out _);
        Check.That(vs != null && fs != null && vs2 != null && fs2 != null, "shader source missing");
        var sh = Gfx.UseShader("sh", vs!, fs!, verVs ^ verFs);
        var other = Gfx.UseShader("other", vs2!, fs2!, verVs2 ^ verFs2);
        var texOpts = new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var texA = Gfx.UseTexture("tex_a", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 40, 0, 0, 255 }, 1, texOpts)!;
        var texC = Gfx.UseTexture("tex_c", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { c, 0, 0, 255 }, 1, texOpts)!;
        var targetOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rtFirst = Gfx.UseTexture("rt_first", 1, 1, Gfx.PixelFormat.Rgba8, null, 1, targetOpts)!;
        var rtAfter = Gfx.UseTexture("rt_after_other", 1, 1, Gfx.PixelFormat.Rgba8, null, 1, targetOpts)!;
        var opts = new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None };
        var otherOpts = new DrawOpts { Shader = other!, Depth = false, Cull = Gfx.Cull.None };
        var noUniforms = new Bindings();
        noUniforms.Set("tex", texC);
        var otherBindings = new Bindings();
        otherBindings.Set("tex_a", texA);

        Gfx.BeginPass(new PassOpts { Target = rtFirst, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(3, noUniforms, opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rtAfter, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(3, otherBindings, otherOpts);
        Gfx.Draw(3, noUniforms, opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        var targets = new TextureRef[] { rtFirst, rtAfter };
        bool all = true;
        for (int i = 0; i < names.Length; i++)
        {
            if (!done[i])
            {
                int? req = null;
                if (!requested)
                    req = 1;
                Gfx.ReadTexture(readbacks[i], targets[i], req, out var st, out var bytes, out var w, out var h, out _, out _, out var id, out _, out var err);
                Check.That(st != Gfx.ReadbackStatus.Error, names[i] + ": read_texture failed: " + err);
                if (st == Gfx.ReadbackStatus.Ready)
                {
                    Check.That(id == 1 && w == 1 && h == 1, names[i] + ": unexpected readback result");
                    var r = bytes!.Get(0);
                    Check.That(Math.Abs(r - c) <= 2, names[i] + ": R got " + r + ", want " + c);
                    done[i] = true;
                }
            }
            if (!done[i])
                all = false;
        }
        requested = true;

        if (all)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

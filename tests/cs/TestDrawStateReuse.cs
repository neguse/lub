// backend が前の draw の pipeline、descriptor set、uniform を使い回しても、各 draw が自分の状態で描くこと。
// draw は 1 列の target に 1 画素ずつ塗り、R は texture、G と B は uniform で決まる。
// - 同じ pipeline で texture を A, B, A と交互に変える。
// - 別の layout の shader に移ってから戻る。
// - texture を外す (白の既定の texture で描く)。
// - uniform を省くと直前の uniform で描く (同じ画素を別の texture で塗り直す)。
// - pass をまたいで同じ pipeline と set で描く。
// - shader は 1 つの stage に uniform block を 2 つ持ち、取り違えると画素の位置か色がずれる。
// 複数のフレームを描いてから読むので、フレームの先頭で bind をやり直さないと描かれない。
using System;
using System.Collections.Generic;
using Lub;

public static class TestDrawStateReuse
{
    const int w = 12;
    const int readFrame = 3;

    static Readback? rb;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
        rb = Gfx.Readback("draw_state_reuse");
    }

    static Bindings U(int x, float g, float b)
    {
        var bindings = new Bindings();
        bindings.Set("cell", new Vec4(x, 0, w, 1));
        bindings.Set("tint", new Vec4(g / 255.0f, b / 255.0f, 0, 0));
        return bindings;
    }

    static Bindings U(TextureRef tex, int x, float g, float b)
    {
        var bindings = U(x, g, b);
        bindings.Set("tex", tex);
        return bindings;
    }

    static void Expect(Bytes bytes, int x, int r, int g, int b)
    {
        var ar = bytes.Get(x * 4);
        var ag = bytes.Get(x * 4 + 1);
        var ab = bytes.Get(x * 4 + 2);
        Check.That(ar == r && ag == g && ab == b,
            $"test_draw_state_reuse: pixel {x} is ({ar}, {ag}, {ab}), want ({r}, {g}, {b})");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_draw_state_reuse: readback never became ready");

        Io.LoadText("tests/cs/data/test_draw_state_reuse.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_draw_state_reuse.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_draw_state_reuse_other.vs.slang", out var vs2, out var verVs2, out _, out _);
        Io.LoadText("tests/cs/data/test_draw_state_reuse_other.fs.slang", out var fs2, out var verFs2, out _, out _);
        if (vs == null || fs == null || vs2 == null || fs2 == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var other = Gfx.UseShader("other", vs2, fs2, verVs2 ^ verFs2);
        if (sh == null || other == null) return;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };
        var otherOpts = new DrawOpts { Shader = other, Depth = false, Cull = Gfx.Cull.None };
        var texOpts = new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var a = Gfx.UseTexture("a", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 40, 0, 0, 255 }, 1, texOpts)!;
        var c = Gfx.UseTexture("c", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 80, 0, 0, 255 }, 1, texOpts)!;
        var rt = Gfx.UseTexture("rt", w, 1, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, U(a, 0, 1, 2), opts);
        Gfx.Draw(6, U(c, 1, 3, 4), opts);
        Gfx.Draw(6, U(a, 2, 5, 6), opts);
        var both = U(c, 3, 7, 0);
        both.Set("tex2", a);
        Gfx.Draw(6, both, otherOpts);
        Gfx.Draw(6, U(a, 4, 8, 9), opts);
        Gfx.Draw(6, U(5, 10, 11), opts);
        Gfx.Draw(6, U(c, 6, 12, 13), opts);
        var bindings = new Bindings();
        bindings.Set("tex", a);
        Gfx.Draw(6, bindings, opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rt, Load = Gfx.LoadAction.Load });
        Gfx.Draw(6, U(a, 7, 14, 15), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        if (frames < readFrame)
            return;
        int? req = frames == readFrame ? 1 : null;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out var rw, out var rh, out _,
            out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && rw == w && rh == 1, "unexpected readback result");
        Expect(bytes!, 0, 40, 1, 2);
        Expect(bytes!, 1, 80, 3, 4);
        Expect(bytes!, 2, 40, 5, 6);
        Expect(bytes!, 3, 80, 40, 7);
        Expect(bytes!, 4, 40, 8, 9);
        Expect(bytes!, 5, 255, 10, 11);
        Expect(bytes!, 6, 40, 12, 13);
        Expect(bytes!, 7, 40, 14, 15);
        Expect(bytes!, 8, 0, 0, 0);
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

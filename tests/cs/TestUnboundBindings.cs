// shader が宣言した texture / StructuredBuffer の一部か全部を bindings で渡さない draw。
// 渡さなかった slot は backend が仮の resource で埋めるので、draw は止まらず落ちずに描ける。
// 仮の resource の値は backend ごとに違ってよいので、渡した resource が決める channel だけを確かめる。
// shader: R = tex_a、G = tex_b (FS の 2 つの texture)、B = item (VS)。
using System;
using System.Collections.Generic;
using Lub;

public static class TestUnboundBindings
{
    const int a = 200;
    const int b = 100;
    const int itemValue = 153;

    static readonly string[] names = new string[] { "none", "b_only", "a_only", "no_item", "all" };
    static readonly bool[] useA = new bool[] { false, false, true, true, true };
    static readonly bool[] useB = new bool[] { false, true, false, true, true };
    static readonly bool[] useItem = new bool[] { false, false, false, false, true };
    // channel ごとの期待値。
    // -1 は確かめない
    static readonly int[] wantR = new int[] { -1, -1, a, a, a };
    static readonly int[] wantG = new int[] { -1, b, -1, b, b };
    static readonly int[] wantB = new int[] { -1, -1, -1, -1, itemValue };

    static readonly List<Readback> readbacks = new List<Readback>();
    static readonly List<TextureRef> targets = new List<TextureRef>();
    static readonly bool[] done = new bool[] { false, false, false, false, false };
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (int i = 0; i < names.Length; i++)
            readbacks.Add(Gfx.Readback(names[i])!);
    }

    static void CheckChannel(string name, int ch, int got, int want)
    {
        if (want < 0)
            return;
        Check.That(Math.Abs(got - want) <= 2, name + ": channel " + ch + " got " + got + ", want " + want);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "readback never became ready");

        Io.LoadText("tests/cs/data/test_unbound_bindings.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_bindings.fs.slang", out var fs, out var verFs, out _, out _);
        Check.That(vs != null && fs != null, "shader source missing");
        var sh = Gfx.UseShader("sh", vs!, fs!, verVs ^ verFs);
        var texOpts = new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var texA = Gfx.UseTexture("tex_a", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { a, 0, 0, 255 }, 1, texOpts);
        var texB = Gfx.UseTexture("tex_b", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, b, 0, 255 }, 1, texOpts);
        var item = Gfx.UseBuffer("item", Gfx.BufferType.Storage, new List<float> { 0, 0, itemValue / 255.0f, 0 }, 1);

        targets.Clear();
        for (int i = 0; i < names.Length; i++)
        {
            var rt = Gfx.UseTexture("rt_" + names[i], 1, 1, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts
            {
                Target = true,
                Filter = Gfx.Filter.Nearest,
                Wrap = Gfx.Wrap.Clamp,
            });
            var bindings = new Dictionary<string, object>();
            if (useA[i])
                bindings["tex_a"] = texA!;
            if (useB[i])
                bindings["tex_b"] = texB!;
            if (useItem[i])
                bindings["item"] = item!;
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
            Gfx.Draw(3, bindings, new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None });
            Gfx.EndPass();
            targets.Add(rt!);
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

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
                    CheckChannel(names[i], 1, bytes!.Get(0), wantR[i]);
                    CheckChannel(names[i], 2, bytes.Get(1), wantG[i]);
                    CheckChannel(names[i], 3, bytes.Get(2), wantB[i]);
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

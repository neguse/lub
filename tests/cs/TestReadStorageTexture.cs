// storage だけの texture (render target でないもの) も Gfx.ReadTexture で読み戻せる。
// compute が rgba8 の storage texture の各 pixel に x に応じた値を書き、読み戻した値を見る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestReadStorageTexture
{
    const int Width = 16;
    const string Cs = @"
[format(""rgba8"")] RWTexture2D<float4> dst;
[shader(""compute"")][numthreads(1, 1, 1)] void cs_main(uint3 tid : SV_DispatchThreadID) {
  dst[tid.xy] = float4(float(tid.x) / 15.0, 1.0, 0.0, 1.0);
}
";
    static Readback? rb;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("read_storage_texture");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_read_storage_texture: readback never became ready");
        var sh = Gfx.UseShaderCompute("cs", Cs, 1);
        var tex = Gfx.UseTexture("storage_only", Width, 1, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Storage = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        Check.That(sh != null && tex != null, "test_read_storage_texture: shader or texture");
        if (sh == null || tex == null)
            return;
        if (frames == 1)
        {
            var bindings = new Bindings();
            bindings.Set("dst", tex);
            Gfx.Dispatch(Width, 1, 1, bindings, new DispatchOpts { Shader = sh });
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = frames == 1 ? 1 : null;
        Gfx.ReadTexture(rb!, tex, req, out var st, out var bytes, out var w, out var h, out _,
            out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "test_read_storage_texture: read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == Width && h == 1, "test_read_storage_texture: unexpected readback result");
        var bad = new List<string>();
        for (var x = 0; x < Width; x++)
        {
            int r = bytes!.Get(x * 4);
            int g = bytes.Get(x * 4 + 1);
            if (Math.Abs(r - x * 17) > 1 || g != 255)
                bad.Add("x " + x + ": (" + r + ", " + g + ")");
        }
        Check.That(bad.Count == 0, "test_read_storage_texture: " + string.Join("; ", bad));
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

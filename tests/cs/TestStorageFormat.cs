// compute が書く storage texture の形式は、shader の [format] か、無ければ要素の型 (float4 = rgba32f、float = r32f) で決まる。
// 各 case は 1x1 の texture に形式でしか表せない値を書き、その値を sample して rt の自分の列に判定を描く。
// 40001 は 16 bit float に丸められ、1.5 は rgba8 に入らない。
// texture と shader の形式が違う dispatch は error になる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestStorageFormat
{
    class Case
    {
        public string Decl;
        public Gfx.PixelFormat Format;
        public string Value;
        public float Expect;
        public float Tolerance;

        public Case(string decl, Gfx.PixelFormat format, string value, float expect, float tolerance)
        {
            Decl = decl;
            Format = format;
            Value = value;
            Expect = expect;
            Tolerance = tolerance;
        }
    }

    const string float4Decl = "RWTexture2D<float4> dst";
    const string float4Value = "float4(40001.0, 0.0, 0.0, 1.0)";

    static readonly List<Case> cases = new List<Case>
    {
        new Case(float4Decl, Gfx.PixelFormat.Rgba32f, float4Value, 40001, 0.25f),
        new Case("RWTexture2D<float> dst", Gfx.PixelFormat.R32f, "40001.0", 40001, 0.25f),
        new Case("[format(\"rgba32f\")] " + float4Decl, Gfx.PixelFormat.Rgba32f, float4Value, 40001, 0.25f),
        new Case("[format(\"rgba16f\")] " + float4Decl, Gfx.PixelFormat.Rgba16f, "float4(1.5, 0.0, 0.0, 1.0)", 1.5f, 0.01f),
        new Case("[format(\"rgba8\")] " + float4Decl, Gfx.PixelFormat.Rgba8, "float4(0.5, 0.0, 0.0, 1.0)", 0.5f, 0.01f),
    };

    static Readback? rb;
    static int frames;
    static bool verified;
    static bool mismatchTried;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("storage_format");
    }

    static string Compute(string decl, string value)
    {
        return decl + ";\n[shader(\"compute\")][numthreads(1, 1, 1)] void cs_main(uint3 tid : SV_DispatchThreadID) {\n  dst[tid.xy] = " + value + ";\n}\n";
    }

    static readonly TextureOpts storageOpts = new TextureOpts { Storage = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frames++;
        Check.That(frames < 300, "test_storage_format: readback never became ready");

        Io.LoadText("tests/cs/data/test_storage_preserve.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_storage_format.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
        {
            frames--;
            return;
        }

        if (verified)
        {
            var sh = Gfx.UseShaderCompute("mismatch_cs", Compute(float4Decl, float4Value), 1);
            var tex = Gfx.UseTexture("mismatch", 1, 1, Gfx.PixelFormat.Rgba16f, null, 1, storageOpts);
            Check.That(sh != null && tex != null, "test_storage_format: mismatch resources");
            if (mismatchTried)
            {
                Check.Pass();
                return;
            }
            mismatchTried = true;
            Check.Error("rgba16f texture for a float4 storage texture", "is rgba16f");
            var bindings = new Bindings();
            bindings.Set("dst", tex!);
            Gfx.Dispatch(1, 1, 1, bindings, new DispatchOpts { Shader = sh });
            return;
        }

        var rt = Gfx.UseTexture("rt", cases.Count, 1, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        var shR = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        Check.That(rt != null && shR != null, "test_storage_format: rt or shader");

        if (frames == 1)
        {
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
            Gfx.EndPass();
            var bindings = new Bindings();
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var cs = Gfx.UseShaderCompute("cs" + i, Compute(c.Decl, c.Value), 1);
                var tex = Gfx.UseTexture("tex" + i, 1, 1, c.Format, null, 1, storageOpts);
                bindings.Clear();
                bindings.Set("dst", tex!);
                Gfx.Dispatch(1, 1, 1, bindings, new DispatchOpts { Shader = cs });
                Gfx.BeginPass(new PassOpts { Target = rt, Load = Gfx.LoadAction.Load });
                bindings.Clear();
                bindings.Set("tex", tex!);
                bindings.Set("col", new Vec4(i, 0, 0, 0));
                bindings.Set("expect", new Vec4(c.Expect, c.Tolerance, 0, 0));
                Gfx.Draw(3, bindings,
                    new DrawOpts { Shader = shR, Depth = false, Cull = Gfx.Cull.None });
                Gfx.EndPass();
            }
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = frames == 1 ? 1 : null;
        Gfx.ReadTexture(rb!, rt!, req, out var st, out var bytes, out var w, out var h, out _,
            out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "test_storage_format: read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == cases.Count && h == 1, "test_storage_format: unexpected readback result");
        var bad = new List<string>();
        for (var x = 0; x < cases.Count; x++)
        {
            if (bytes!.Get(x * 4) != 255)
                bad.Add(cases[x].Decl + " wrote a different value (R " + bytes.Get(x * 4) + ")");
        }
        Check.That(bad.Count == 0, "test_storage_format: " + string.Join("; ", bad));
        verified = true;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

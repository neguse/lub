// draw と dispatch の uniform は shader が宣言した field とちょうど一致しなければ error になる。
// 足りない field、shader に無い名前、値の数の違いを 1 フレームに 1 つずつ確かめ、一致する draw と dispatch は通ることを見る。
// reflection に入りきらない長さの field の名前は、shader の compile が error になる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestUniformMatch
{
    const string Fs = @"
struct FsUniforms { float4 tint; float gain; };
ConstantBuffer<FsUniforms> fu;
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return float4(fu.tint.rgb * fu.gain, 1.0); }";

    // 32 文字の field の名前 (上限は 31 文字)
    const string LongFs = @"
struct FsUniforms { float4 a_field_name_of_32_characters___; };
ConstantBuffer<FsUniforms> fu;
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return fu.a_field_name_of_32_characters___; }";

    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    static Bindings Full()
    {
        var b = new Bindings();
        b.Set("cell", new Vec4(0, 0, 1, 1));
        b.Set("tint", new Vec4(1, 0, 0, 1));
        b.Set("gain", 1.0f);
        return b;
    }

    static void Draw(ShaderRef sh, Bindings b)
    {
        Gfx.BeginPass(new PassOpts { Target = BindingsTestUtil.Target("rt", 1, 1), ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, b, BindingsTestUtil.Opts(sh));
        Gfx.EndPass();
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Io.LoadText("tests/cs/data/test_compute_uniform_block.cs.slang", out var cs, out var verCs, out _, out _);
        if (cs == null)
            return;
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Fs, 1);
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        var buf = Gfx.UseBuffer("buf", Gfx.BufferType.Storage, new List<float> { 0, 0, 0, 0, 0, 0, 0, 0 }, 1);
        if (sh == null || shC == null || buf == null)
            return;
        frame++;
        switch (frame)
        {
            case 1:
                {
                    var b = new Bindings();
                    b.Set("cell", new Vec4(0, 0, 1, 1));
                    Check.Error("a draw without the fragment block must fail", "uniforms don't match shader 'sh': missing 'tint', 'gain'");
                    Draw(sh, b);
                    break;
                }
            case 2:
                {
                    var b = Full();
                    b.Set("tnit", new Vec4(1, 0, 0, 1));
                    b.Set("unused", 1.0f);
                    Check.Error("a name the shader doesn't declare must fail", "uniforms don't match shader 'sh': not in the shader 'tnit', 'unused'");
                    Draw(sh, b);
                    break;
                }
            case 3:
                {
                    var b = Full();
                    b.Set("tint", new Vec3(1, 0, 0));
                    b.Set("gain", new Vec2(1, 1));
                    Check.Error("a value of the wrong size must fail", "uniforms don't match shader 'sh': values 'tint' has 3 (want 4), 'gain' has 2 (want 1)");
                    Draw(sh, b);
                    break;
                }
            case 4:
                {
                    var b = new Bindings();
                    b.Set("cell", new Vec4(0, 0, 1, 1));
                    b.Set("tint", new Vec3(1, 0, 0));
                    b.Set("extra", 1.0f);
                    Check.Error("every kind of mismatch is listed", "missing 'gain'; not in the shader 'extra'; values 'tint' has 3 (want 4)");
                    Draw(sh, b);
                    break;
                }
            case 5:
                {
                    var b = new Bindings();
                    b.Set("out_buf", buf);
                    b.Set("value", new Vec2(0, 1));
                    Check.Error("a dispatch value of the wrong size must fail", "uniforms don't match shader 'cs': values 'value' has 2 (want 4)");
                    Gfx.Dispatch(1, 1, 1, b, new DispatchOpts { Shader = shC });
                    break;
                }
            case 6:
                Check.Error("a field name longer than the reflection holds must fail", "'a_field_name_of_32_characters___' in 'fu' is 32 characters; the limit is 31");
                Gfx.UseShader("long", BindingsTestUtil.Vs, LongFs, 1);
                break;
            case 7:
                {
                    Draw(sh, Full());
                    var b = new Bindings();
                    b.Set("out_buf", buf);
                    b.Set("value", new Vec4(0, 1, 0, 1));
                    Gfx.Dispatch(1, 1, 1, b, new DispatchOpts { Shader = shC });
                    BindingsTestUtil.EmptyMainPass();
                    Check.Pass();
                    break;
                }
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

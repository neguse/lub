// 1 つの shader が結べる数 (texture 8、storage buffer 4、storage texture 4。vertex と fragment は合計) を超えた宣言は、上限を名指す error になる。
// 上限ちょうどの shader は作れる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestShaderLimits
{
    static int frame;

    const string Vs = @"
struct VSOut { float4 pos : SV_Position; };
[shader(""vertex"")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = float4(0.0, 0.0, 0.0, 1.0);
  return o;
}
";

    // n 枚の texture を宣言して全部を読む fragment shader (名前は prefix0, prefix1, ...)
    static string Fs(string prefix, int n)
    {
        string decl = "";
        string sum = "float4 c = float4(0.0, 0.0, 0.0, 0.0);\n";
        for (int i = 0; i < n; i++)
        {
            decl += "LUB_TEXTURE2D(" + prefix + i + ");\n";
            sum += "  c += LUB_SAMPLE_LOD(" + prefix + i + ", float2(0.5, 0.5));\n";
        }
        return decl + "[shader(\"fragment\")] float4 fs_main() : SV_Target {\n  " + sum + "  return c;\n}\n";
    }

    // 2 枚の texture を読む vertex shader
    const string VsTex = @"
LUB_TEXTURE2D(vt0);
LUB_TEXTURE2D(vt1);
struct VSOut { float4 pos : SV_Position; };
[shader(""vertex"")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = LUB_SAMPLE_LOD(vt0, float2(0.5, 0.5)) + LUB_SAMPLE_LOD(vt1, float2(0.5, 0.5));
  return o;
}
";

    // n 個の storage buffer か storage texture に書く compute shader
    static string Cs(bool textures, int n)
    {
        string decl = "";
        string body = "";
        for (int i = 0; i < n; i++)
        {
            if (textures)
            {
                decl += "RWTexture2D<float4> o" + i + ";\n";
                body += "  o" + i + "[tid.xy] = float4(1.0, 1.0, 1.0, 1.0);\n";
            }
            else
            {
                decl += "RWStructuredBuffer<float> o" + i + ";\n";
                body += "  o" + i + "[tid.x] = 1.0;\n";
            }
        }
        return decl + "[shader(\"compute\")][numthreads(1, 1, 1)] void cs_main(uint3 tid : SV_DispatchThreadID) {\n" + body + "}\n";
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        frame++;
        Check.Frame();
        if (frame == 1)
        {
            Check.Error("9 textures in the fragment shader", "at most 8 textures");
            Gfx.UseShader("limit_fs", Vs, Fs("t", 9), 1);
        }
        else if (frame == 2)
        {
            Check.Error("2 + 7 textures in the vertex and fragment shaders", "at most 8 textures");
            Gfx.UseShader("limit_vs_fs", VsTex, Fs("t", 7), 1);
        }
        else if (frame == 3)
        {
            Check.Error("5 storage buffers", "at most 4 storage buffers");
            Gfx.UseShaderCompute("limit_sbuf", Cs(false, 5), 1);
        }
        else if (frame == 4)
        {
            Check.Error("5 storage textures", "at most 4 storage textures");
            Gfx.UseShaderCompute("limit_stex", Cs(true, 5), 1);
        }
        else if (frame == 5)
        {
            Check.That(Gfx.UseShader("limit_ok", Vs, Fs("t", 8), 1) != null, "8 textures must compile");
            Check.That(Gfx.UseShaderCompute("limit_ok_sbuf", Cs(false, 4), 1) != null, "4 storage buffers must compile");
            Check.That(Gfx.UseShaderCompute("limit_ok_stex", Cs(true, 4), 1) != null, "4 storage textures must compile");
        }
        else
        {
            Check.Pass();
        }
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

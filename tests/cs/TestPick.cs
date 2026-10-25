// Gfx.Pick は、指した画素に一番手前で描いた draw の番号と、その draw を呼んだ所を返す。
// 4 枚の板を depth を付けて描く: A (全面、z 0.5)、B (左半分、z 0.3)、C (右半分、z 0.7)、D (全面、z 0.9)。
// 左半分は B が、右半分は A が一番手前に見える。
// 結果を待たずに次の要求を出すと、前の要求は捨てられる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestPick
{
    const string Vs = "struct U { float4 rect; float4 color; };\n"
        + "ConstantBuffer<U> u;\n"
        + "struct VSOut { float4 color : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), u.color.w, 1.0);\n"
        + "  o.color = float4(u.color.xyz, 1.0);\n"
        + "  return o;\n"
        + "}\n";
    const string Fs = "struct FSIn { float4 color : COLOR; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target { return i.color; }\n";

    static int frame;
    static int step;
    static int waited;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    static void Quad(ShaderRef sh, float x0, float x1, float z)
    {
        Gfx.Draw(4, new Dictionary<string, object>
        {
            ["uniforms"] = new Dictionary<string, object>
            {
                ["rect"] = new List<float> { x0, -1, x1, 1 },
                ["color"] = new List<float> { 0.5f, 0.5f, 0.5f, z },
            },
        }, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        var sh = Gfx.UseShader("test_pick", Vs, Fs, 1);
        Check.That(sh != null, "shader");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Quad(sh!, -1, 1, 0.5f);
        Quad(sh!, -1, 0, 0.3f);
        Quad(sh!, 0, 1, 0.7f);
        Quad(sh!, -1, 1, 0.9f);
        Gfx.EndPass();

        Gfx.Size(out var w, out var h);
        if (step == 0)
        {
            Gfx.Pick(Gfx.MainTex!, w / 4, h / 2);
            step = 1;
            return;
        }
        if (step == 1)
        {
            // 次の frame で前の要求が流し直され、読み戻しを待っている間に、次の要求を出す
            step = 15;
            return;
        }
        if (step == 15)
        {
            // 前の要求の結果を待たずに出した要求は、前の要求を捨てる
            Gfx.Pick(Gfx.MainTex!, w * 3 / 4, h / 2);
            step = 2;
            return;
        }
        var hit = Gfx.PickResult();
        if (hit == null)
        {
            waited++;
            Check.That(waited < 30, "no pick result in 30 frames");
            return;
        }
        waited = 0;
        if (step == 2)
        {
            Check.That(hit.Draw == 0, "right half: draw " + hit.Draw + ", want 0 (A)");
            Gfx.Pick(Gfx.MainTex!, w / 4, h / 2);
            step = 3;
            return;
        }
        Check.That(hit.Draw == 1, "left half: draw " + hit.Draw + ", want 1 (B)");
        Check.That(hit.Site.Contains("TestPick"), "site '" + hit.Site + "' does not name the caller");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

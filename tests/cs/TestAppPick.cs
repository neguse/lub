// The app argument of Gfx.Pick: picks the frontmost draw at a pixel of the main of an app opened with App.Use.
// TestAppPickChild draws 2 planes: A (full screen, z 0.5), B (left half, z 0.3). B is in front on the left half, A on the right half.
// The opener's draws do not go into the opened app's record.
using System;
using Lub;

public static class TestAppPickChild
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

    public static void Quad(ShaderRef sh, float x0, float x1, float z)
    {
        var b = new Bindings();
        b.Set("rect", new Vec4(x0, -1, x1, 1));
        b.Set("color", new Vec4(0.5f, 0.5f, 0.5f, z));
        Gfx.Draw(4, b, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None });
    }

    public static ShaderRef? Shader(string key) { return Gfx.UseShader(key, Vs, Fs, 1); }

    public static void OnFrame(float dt)
    {
        var sh = Shader("app_pick_child");
        if (sh == null) return;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Quad(sh, -1, 1, 0.5f);
        Quad(sh, -1, 0, 0.3f);
        Gfx.EndPass();
    }
}

public static class TestAppPick
{
    static int step;
    static int waited;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = Gfx.UseTexture("child", 16, 8, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        var sh = TestAppPickChild.Shader("app_pick_parent");
        Check.That(rt != null && sh != null, "resources");
        if (rt == null || sh == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", "TestAppPickChild");
        Check.That(App.Step(child, rt, dt), "Step failed");
        // The opener also draws 3 planes to its own main (they must not mix into the draw indices of the opened app)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        TestAppPickChild.Quad(sh, -1, 1, 0.2f);
        TestAppPickChild.Quad(sh, -1, 1, 0.1f);
        TestAppPickChild.Quad(sh, -1, 1, 0.05f);
        Gfx.EndPass();

        if (step == 0)
        {
            // The opened app's main is 16x8. Right half
            Gfx.Pick(Gfx.MainTex!, 12, 4, child);
            step = 1;
            return;
        }
        var hit = Gfx.PickResult(child);
        if (hit == null)
        {
            waited++;
            Check.That(waited < 30, "no pick result in 30 frames");
            return;
        }
        waited = 0;
        // No request was issued for the opener itself
        Check.That(Gfx.PickResult() == null, "the opening app has a pick result");
        if (step == 1)
        {
            Check.That(hit.Draw == 0, "right half: draw " + hit.Draw + ", want 0 (A)");
            Gfx.Pick(Gfx.MainTex!, 4, 4, child);
            step = 2;
            return;
        }
        if (step == 2)
        {
            Check.That(hit.Draw == 1, "left half: draw " + hit.Draw + ", want 1 (B)");
            Check.That(hit.Site.Contains("TestAppPick.cs:"), "site '" + hit.Site + "' does not name the caller");
            // Outside the target
            Gfx.Pick(Gfx.MainTex!, 100, 4, child);
            step = 3;
            return;
        }
        Check.That(hit.Draw == -1, "outside the target: draw " + hit.Draw + ", want -1");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

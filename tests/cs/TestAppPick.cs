// Gfx.Pick の app の引数: App.Use で開いた app の main の画素に、一番手前で描いた draw を引く。
// TestAppPickChild は 2 枚の板を描く: A (全面、z 0.5)、B (左半分、z 0.3)。左半分は B、右半分は A が手前。
// 開いた側の draw は、開いた app の記録に入らない。
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
        // 開いた側も自分の main に 3 枚描く (開いた app の draw の番号に混ざらないこと)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        TestAppPickChild.Quad(sh, -1, 1, 0.2f);
        TestAppPickChild.Quad(sh, -1, 1, 0.1f);
        TestAppPickChild.Quad(sh, -1, 1, 0.05f);
        Gfx.EndPass();

        if (step == 0)
        {
            // 開いた app の main は 16x8。右半分
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
        // 自分には要求を出していない
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
            // target の外
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

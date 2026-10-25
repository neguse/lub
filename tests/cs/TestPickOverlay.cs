// Gfx.Pick は overlay の draw を記録しない。
// ゲームは画面の中央に板を 1 枚描き、overlay (TestPickOverlayTool) はその後で画面全体に板を描いてから中央を指す。
// overlay の板が手前に見えても、返るのはゲームの板 (draw 0)。
// lub tests/cs/TestPickOverlay.csproj で動かす (overlay は csproj の entry でだけ付く)。
using System;
using System.Collections.Generic;
using Lub;

public static class TestPickOverlay
{
    public const string Vs = "struct U { float4 rect; };\n"
        + "ConstantBuffer<U> u;\n"
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.5, 1.0);\n"
        + "}\n";
    public const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0.5, 0.5, 0.5, 1); }\n";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void Rect(ShaderRef sh, float x0, float y0, float x1, float y1)
    {
        Gfx.Draw(4, new Dictionary<string, object>
        {
            ["uniforms"] = new Dictionary<string, object> { ["rect"] = new List<float> { x0, y0, x1, y1 } },
        }, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None, Depth = false });
    }

    public static void OnFrame(float dt)
    {
        var sh = Gfx.UseShader("test_pick_overlay_game", Vs, Fs, 1);
        if (sh == null)
            return;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Rect(sh, -0.5f, -0.5f, 0.5f, 0.5f);
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

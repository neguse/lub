// Gfx.Pick の、frame の中の出来事に対する振る舞い。
// 1. 上下: 画面の上半分と下半分に板を描き、上と下を指すとそれぞれの板が返る (target の px は左上原点)。
// 2. 記録中の要求: 記録している frame の中で出した要求は、その frame ではなく後の frame から引く。
//    記録している frame は全面の板 1 枚、後の frame は隅の板と全面の板の 2 枚を描くので、後の frame から引くと 1 が返る。
// 3. 書き換え: 中央に板を描いた後、同じ frame で同じ buffer を隅を指す頂点に書き換えても (同じ大きさ)、描いた時の頂点で流し直す。
// 4. 作り直し: 3 と同じことを、大きさの違う頂点で buffer を作り直してする。
// 5. 範囲の外: target の外を指すと、何も無い (-1) という結果が返る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestPickFrame
{
    const string RectVs = "struct U { float4 rect; };\n"
        + "ConstantBuffer<U> u;\n"
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.5, 1.0);\n"
        + "}\n";
    const string PullVs = "StructuredBuffer<float2> verts;\n"
        + "static const uint idx[6] = {0, 1, 2, 0, 2, 3};\n"
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  return float4(verts[idx[vid]], 0.5, 1.0);\n"
        + "}\n";
    const string Fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0.5, 0.5, 0.5, 1); }\n";

    static readonly List<float> center = new List<float> { -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f };
    static readonly List<float> corner = new List<float> { 0.8f, 0.8f, 0.9f, 0.8f, 0.9f, 0.9f, 0.8f, 0.9f };
    static readonly List<float> cornerBig = new List<float> { 0.8f, 0.8f, 0.9f, 0.8f, 0.9f, 0.9f, 0.8f, 0.9f, 0, 0, 0, 0 };

    static int step;
    static int phaseFrame;
    static int waited;
    static int version;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    static void Rect(ShaderRef sh, float x0, float y0, float x1, float y1)
    {
        Gfx.Draw(4, new Dictionary<string, object>
        {
            ["uniforms"] = new Dictionary<string, object> { ["rect"] = new List<float> { x0, y0, x1, y1 } },
        }, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None, Depth = false });
    }

    // 中央に板を描いてから、同じ frame で buffer を隅の頂点にする
    static void PulledThenMoved(ShaderRef pull, string key, List<float> moved)
    {
        version++;
        var buf = Gfx.UseBuffer(key, Gfx.BufferType.Storage, center, version);
        Gfx.Draw(6, new Dictionary<string, object> { ["verts"] = buf! },
            new DrawOpts { Shader = pull, Cull = Gfx.Cull.None, Depth = false });
        version++;
        Gfx.UseBuffer(key, Gfx.BufferType.Storage, moved, version);
    }

    // 結果を待つ。
    // 届いていなければ -2
    static int Result()
    {
        var hit = Gfx.PickResult();
        if (hit == null)
        {
            waited++;
            Check.That(waited < 30, "no pick result in 30 frames at step " + step);
            return -2;
        }
        waited = 0;
        return hit.Draw;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        phaseFrame++;
        var rect = Gfx.UseShader("test_pick_frame_rect", RectVs, Fs, 1);
        var pull = Gfx.UseShader("test_pick_frame_pull", PullVs, Fs, 1);
        Check.That(rect != null && pull != null, "shader");
        Gfx.Size(out var w, out var h);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        if (step <= 2)
        {
            // 上半分が 0、下半分が 1 (NDC の y は上が +1)
            Rect(rect!, -1, 0, 1, 1);
            Rect(rect!, -1, -1, 1, 0);
        }
        else if (step == 3)
        {
            // phaseFrame 1 は要求を記録している frame
            if (phaseFrame == 1)
            {
                Rect(rect!, -1, -1, 1, 1);
            }
            else
            {
                Rect(rect!, 0.8f, 0.8f, 0.9f, 0.9f);
                Rect(rect!, -1, -1, 1, 1);
            }
        }
        else if (step == 4)
        {
            PulledThenMoved(pull!, "test_pick_frame_update", corner);
        }
        else if (step == 5)
        {
            PulledThenMoved(pull!, "test_pick_frame_resize", cornerBig);
        }
        else
        {
            Rect(rect!, -1, -1, 1, 1);
        }
        Gfx.EndPass();

        if (step == 0)
        {
            Gfx.Pick(Gfx.MainTex!, w / 2, h / 4);
            step = 1;
            phaseFrame = 0;
            return;
        }
        if (step == 1)
        {
            var top = Result();
            if (top == -2)
                return;
            Check.That(top == 0, "top: draw " + top + ", want 0");
            Gfx.Pick(Gfx.MainTex!, w / 2, h * 3 / 4);
            step = 2;
            phaseFrame = 0;
            return;
        }
        if (step == 2)
        {
            var bottom = Result();
            if (bottom == -2)
                return;
            Check.That(bottom == 1, "bottom: draw " + bottom + ", want 1");
            // 次の frame を記録させる
            Gfx.Pick(Gfx.MainTex!, w / 2, h / 2);
            step = 3;
            phaseFrame = 0;
            return;
        }
        if (step == 3)
        {
            if (phaseFrame == 1)
            {
                Gfx.Pick(Gfx.MainTex!, w / 2, h / 2);
                return;
            }
            var later = Result();
            if (later == -2)
                return;
            Check.That(later == 1, "a request made while recording was answered from the frame it was made in: draw " + later + ", want 1");
            Gfx.Pick(Gfx.MainTex!, w / 2, h / 2);
            step = 4;
            phaseFrame = 0;
            return;
        }
        if (step == 4)
        {
            var updated = Result();
            if (updated == -2)
                return;
            Check.That(updated == 0, "a buffer updated after the draw: draw " + updated + ", want 0");
            Gfx.Pick(Gfx.MainTex!, w / 2, h / 2);
            step = 5;
            phaseFrame = 0;
            return;
        }
        if (step == 5)
        {
            var resized = Result();
            if (resized == -2)
                return;
            Check.That(resized == 0, "a buffer made again after the draw: draw " + resized + ", want 0");
            Gfx.Pick(Gfx.MainTex!, -5, h + 5);
            step = 6;
            phaseFrame = 0;
            return;
        }
        var outside = Result();
        if (outside == -2)
            return;
        Check.That(outside == -1, "a pick outside the target: draw " + outside + ", want -1");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

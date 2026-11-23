// Behavior of Gfx.Pick with respect to events within a frame.
// 1. Top and bottom: draws planes on the top and bottom halves of the screen; pointing at the top and at the bottom returns the respective plane (target px has its origin at the top left).
// 2. Request during recording: a request issued within a frame that is being recorded picks from a later frame, not from that frame.
//    The recorded frame draws 1 full-screen plane and the later frame draws 2 planes, a corner one and a full-screen one, so picking from the later frame returns 1.
// 3. Rewrite: after drawing a plane in the center, even if the same buffer is rewritten in the same frame with vertices pointing at a corner (same size), the replay uses the vertices from draw time.
// 4. Recreate: does the same as 3 by recreating the buffer with vertices of a different size.
// 5. Out of range: pointing outside the target returns a "nothing" result (-1).
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

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    static void Rect(ShaderRef sh, float x0, float y0, float x1, float y1)
    {
        var b = new Bindings();
        b.Set("rect", new Vec4(x0, y0, x1, y1));
        Gfx.Draw(4, b, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None, Depth = false });
    }

    // Draw a plane in the center, then set the buffer to the corner vertices in the same frame.
    // A declaration without version marks the contents as changed, so the same key can be declared twice in the same frame
    static void PulledThenMoved(ShaderRef pull, string key, List<float> moved)
    {
        var buf = Gfx.UseBuffer(key, Gfx.BufferType.Storage, center);
        var b = new Bindings();
        b.Set("verts", buf!);
        Gfx.Draw(6, b, new DrawOpts { Shader = pull, Cull = Gfx.Cull.None, Depth = false });
        Gfx.UseBuffer(key, Gfx.BufferType.Storage, moved);
    }

    // Wait for the result.
    // -2 if it has not arrived
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
            // The top half is 0, the bottom half is 1 (NDC y is +1 at the top)
            Rect(rect!, -1, 0, 1, 1);
            Rect(rect!, -1, -1, 1, 0);
        }
        else if (step == 3)
        {
            // phaseFrame 1 is the frame that records the request
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
            // Have the next frame recorded
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

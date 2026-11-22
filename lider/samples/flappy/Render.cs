// Drawing. Every object is drawn as a stretched unit cube, and each object calls Gfx.Draw in its own Draw.
// Non-VR draws to main_tex with a camera looking from the side; XR draws into the eye passes, scaled down and placed in front of the eyes.
using System.Collections.Generic;
using Lub;
using Lubx;

public static class Render
{
    public static readonly List<float> BirdColor = new List<float>
    {
        1.0f,
        0.8f,
        0.2f,
        1,
    };
    public static readonly List<float> DeadColor = new List<float>
    {
        0.9f,
        0.3f,
        0.25f,
        1,
    };
    public static readonly List<float> PipeColor = new List<float>
    {
        0.35f,
        0.8f,
        0.4f,
        1,
    };
    public static readonly List<float> GroundColor = new List<float>
    {
        0.45f,
        0.35f,
        0.25f,
        1,
    };
    public static readonly DrawOpts Opts = new DrawOpts
    {
        Depth = true,
        Cull = Gfx.Cull.None,
    };

    static readonly XrAnchor anchor = new XrAnchor();
    static readonly float[] eyeVp = new float[16];
    static readonly float[] background = new float[] { 0.05f, 0.05f, 0.15f, 1 };

    // No vertex data; the face (6 vertices each) and corner are derived from the vertex index. Brightness varies per face
    const string Vs =
        "struct Uniforms { float4x4 mvp; float4 color; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float4 color : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  uint face = vid / 6;\n"
        + "  uint k = vid % 6;\n"
        + "  float a = (k == 1 || k == 4 || k == 5) ? 0.5 : -0.5;\n"
        + "  float b = (k == 2 || k == 3 || k == 5) ? 0.5 : -0.5;\n"
        + "  float s = (face & 1) != 0 ? 0.5 : -0.5;\n"
        + "  uint axis = face >> 1;\n"
        + "  float3 p = axis == 0 ? float3(s, a, b) : (axis == 1 ? float3(a, s, b) : float3(a, b, s));\n"
        + "  float shade = axis == 1 ? (s > 0 ? 1.0 : 0.45) : (axis == 0 ? 0.7 : 0.85);\n"
        + "  VSOut o;\n"
        + "  o.pos = mul(u.mvp, float4(p, 1.0));\n"
        + "  o.color = float4(u.color.rgb * shade, u.color.a);\n"
        + "  return o;\n"
        + "}\n";
    const string Fs =
        "struct FSIn { float4 color : COLOR; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target { return i.color; }\n";

    public static Bindings Cube(Mat4 mvp, List<float> color)
    {
        var bindings = new Bindings();
        bindings.Set("mvp", mvp);
        bindings.Set("color", color);
        return bindings;
    }

    public static void Frame()
    {
        var sh = Gfx.UseShader("flappy_cube", Vs, Fs, 1);
        if (sh == null)
            return;
        Opts.Shader = sh;
        if (!Xr.Active())
        {
            var vp = Camera3d.Vp(
                new Camera3dOpts
                {
                    Eye = new Vec3(0, 0, -8),
                    Target = new Vec3(0, 0, 0),
                }
            );
            Gfx.BeginPass(
                new PassOpts { Target = Gfx.MainTex, ClearColor = background }
            );
            Flappy.Draw(vp);
            Gfx.EndPass();
            return;
        }
        var left = Xr.View(0, 0.05f, 100);
        var right = Xr.View(1, 0.05f, 100);
        if (left == null || right == null)
            return;
        if (!anchor.IsSet())
            anchor.Recenter(left, right);
        // In the reference space, -z is forward, as in OpenXR. 1 game unit is 0.18 m, placed slightly below eye height, 2.6 m ahead
        var world =
            Mat4.Translate(new Vec3(0, -0.15f, -2.6f))
            * Mat4.Scale(new Vec3(0.18f, 0.18f, 0.18f));
        foreach (var view in new[] { left, right })
        {
            anchor.ViewProjection(view, eyeVp);
            var values = new List<float>();
            foreach (var v in eyeVp)
                values.Add(v);
            var vp = Mat4.FromList(values, 0);
            Gfx.BeginPass(
                new PassOpts { Target = view.Target, ClearColor = background }
            );
            Flappy.Draw(vp * world);
            Gfx.EndPass();
        }
    }
}

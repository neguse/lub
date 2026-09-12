// Renderer3d の影を、床と浮いた箱だけで確認する。
// 光へ向かう方向 (1, 2, 0) から床への投影は (x - y / 2, 0, z)。
// 水色の枠はこの式から求めた輪郭。G: 枠、S: 影を切り替える。
using System;
using System.Collections.Generic;
using static Lub;

public static class RendererShadow28
{
    static Renderer3d? renderer;
    static Mesh3d? cube;
    static bool guides = true;
    static bool shadows = true;

    public static void OnInit()
    {
        Lub.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND"),
            Width = 960,
            Height = 540,
        });
        renderer = new Renderer3d("shadow28");
        cube = new Mesh3d("shadow28_cube");
    }

    public static void OnEvent(EventData e) { }
    public static void OnQuit() { }

    static Mat4 Box(float x, float y, float z, float hx, float hy, float hz)
    {
        return Mat4.Translate(new Vec3(x, y, z)) * Mat4.Scale(new Vec3(hx, hy, hz));
    }

    public static void OnFrame(float dt)
    {
        var r = renderer;
        var mesh = cube;
        if (r == null || mesh == null) return;
        if (!mesh.Ready()) mesh.Rebuild(Shapes3d.Cube());
        if (Input.KeyPressed("g")) guides = !guides;
        if (Input.KeyPressed("s")) shadows = !shadows;

        r.Light.Dir = new Vec3(1, 2, 0);
        r.Shadow.Center = new Vec3(0, 0, 0);
        r.Shadow.Extent = 3;
        r.Shadow.Enabled = shadows;
        r.Ssao.Enabled = false;
        r.Bloom.Enabled = false;
        r.Aa.Enabled = false;
        r.Begin(new Camera
        {
            Eye = new Vec3(-3, 5, -6),
            Target = new Vec3(-0.4f, 0.4f, 0),
            Fov = 42,
            Near = 0.1f,
            Far = 30,
        });

        // 床上面 y=0。箱は x,z=[-0.5,0.5], y=[0.5,1.5]。
        r.Draw(mesh, Box(0, -0.1f, 0, 2.5f, 0.1f, 2),
            new Draw3dOpts { Tint = Color.Rgb(0.65f, 0.65f, 0.65f) });
        r.Draw(mesh, Box(0, 1, 0, 0.5f, 0.5f, 0.5f),
            new Draw3dOpts { Tint = Color.Rgb(0.9f, 0.55f, 0.2f) });

        if (guides)
        {
            // 投影の全体は x=[-1.25,0.25], z=[-0.5,0.5]。
            // 枠は床から少し離して重なりのちらつきを避ける。
            // Blend 指定により枠自身は影を落とさない。
            var guide = new Draw3dOpts
            {
                Tint = Color.Rgb(0.1f, 0.95f, 0.95f),
                Blend = Gfx.Blend.Alpha,
                Uniforms = new Dictionary<string, object>
                {
                    ["shadow_p"] = new List<float> { 0, 0, 0, 0 },
                },
            };
            r.Draw(mesh, Box(-0.5f, 0.006f, -0.5f, 0.75f, 0.002f, 0.008f), guide);
            r.Draw(mesh, Box(-0.5f, 0.006f, 0.5f, 0.75f, 0.002f, 0.008f), guide);
            r.Draw(mesh, Box(-1.25f, 0.006f, 0, 0.008f, 0.002f, 0.5f), guide);
            r.Draw(mesh, Box(0.25f, 0.006f, 0, 0.008f, 0.002f, 0.5f), guide);
        }
        r.End();
    }
}

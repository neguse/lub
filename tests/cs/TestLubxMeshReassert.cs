// Lubx.Mesh3d と MeshText が GPU buffer を毎 frame 再主張することを見る。
// ResourceSweepAfterFrames を小さくし、同じ mesh と glyph を sweep 期間より長く描き続けても stale handle の error にならないことを確かめる。
using System;
using Lub;
using Lubx;

public static class TestLubxMeshReassert
{
    const int sweepFrames = 2;
    const int frames = 8;
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";

    static int frame;
    static Renderer3d? ren;
    static Mesh3d? cube;
    static MeshText? text;
    static Camera? cam;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
            ResourceSweepAfterFrames = sweepFrames,
        });
        ren = new Renderer3d("mesh_reassert");
        cube = new Mesh3d("mesh_reassert_cube");
        text = new MeshText("mesh_reassert_text", fontPath, 1, 320, 180);
        cam = new Camera { Eye = new Vec3(0, 1.5f, -4), Target = new Vec3(0, 0, 0) };
    }

    static void Draw()
    {
        ren!.Begin(cam!);
        ren.Draw(cube, Mat4.Identity());
        ren.End();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        text!.Text("Ab", 10, 100, 48);
        Gfx.EndPass();
    }

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame == 1)
        {
            // gfx は OnInit では使えないので最初の frame で upload する
            cube!.Rebuild(Shapes3d.Cube());
            Check.That(cube.Ready(), "cube must be ready after rebuild");
        }
        else
        {
            // 再主張されていなければ sweep 後の draw は stale handle になる。
            // draw の前に lookup で見て、落ちる前に message を出す。
            Check.That(Gfx.LookupBuffer("mesh_reassert_cube_vb") != null, "frame " + frame + ": cube vb was swept");
            Check.That(Gfx.LookupBuffer("mesh_reassert_text_v:65") != null, "frame " + frame + ": glyph vb was swept");
        }
        Draw();
        if (frame == frames)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

// Checks that Lubx.Mesh3d and MeshText reassert their GPU buffers every frame.
// Makes ResourceSweepAfterFrames small, and checks that drawing the same mesh and glyphs for longer than the sweep period does not cause stale handle errors.
// Then lets only the cube be swept by not drawing it for longer than the sweep period, and also checks that Mesh3d recreates the buffers from the vertices and indices it kept in Rebuild.
using System;
using Lub;
using Lubx;

public static class TestLubxMeshReassert
{
    const int sweepFrames = 2;
    const int frames = 8;
    // Number of frames in which the cube is not drawn (longer than the sweep period)
    const int idleFrames = sweepFrames + 2;
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

    static void Draw(bool withCube)
    {
        ren!.Begin(cam!);
        if (withCube)
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
            // gfx cannot be used in OnInit, so upload in the first frame
            cube!.Rebuild(Shapes3d.Cube());
            Check.That(cube.Ready(), "cube must be ready after rebuild");
        }
        else if (frame <= frames)
        {
            // If it was not reasserted, the draw after the sweep would use a stale handle.
            // Check with a lookup before the draw, and print a message before it fails.
            Check.That(Gfx.LookupBuffer("mesh_reassert_cube/vb") != null, "frame " + frame + ": cube vb was swept");
            Check.That(Gfx.LookupBuffer("mesh_reassert_text/v:65") != null, "frame " + frame + ": glyph vb was swept");
        }
        else if (frame == frames + idleFrames + 1)
        {
            Check.That(Gfx.LookupBuffer("mesh_reassert_cube/vb") == null, "cube vb was not swept while the cube was not drawn");
            Check.That(Gfx.LookupBuffer("mesh_reassert_cube/ib") == null, "cube ib was not swept while the cube was not drawn");
            Check.That(cube!.Ready(), "cube must be ready again from the kept vertices and indices");
            Check.That(Gfx.LookupBuffer("mesh_reassert_cube/vb") != null, "cube vb was not made again");
            Check.That(Gfx.LookupBuffer("mesh_reassert_cube/ib") != null, "cube ib was not made again");
        }
        Draw(frame <= frames || frame > frames + idleFrames);
        if (frame == frames + idleFrames + 1)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

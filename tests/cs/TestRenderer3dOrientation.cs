// Renderer3d が中で持つ絵 (key の下の ldr、hdr など) は、ほかのテクスチャと同じく先頭の行が画面の上端になる。
// 画面の上の方に置いた cube が、ldr でも上の方 (Camera3d.Project が返す px) に写っていることを readback で見る。
using System;
using Lub;
using Lubx;

public static class TestRenderer3dOrientation
{
    const int W = 64;
    const int H = 64;
    static int frame;
    static Renderer3d? ren;
    static Mesh3d? cube;
    static Readback? rb;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = W,
            Height = H,
        });
        ren = new Renderer3d("r3d_orient");
        ren.Background = Color.Rgb(0, 0, 0);
        ren.Ssao.Enabled = false;
        ren.Bloom.Enabled = false;
        ren.Aa.Enabled = false;
        ren.Dither = false;
        cube = new Mesh3d("r3d_orient_cube");
        rb = Gfx.Readback("r3d_orient_rb");
    }

    // ldr の (x, y) の明るさ (r + g + b)
    static int Lum(Bytes b, int stride, int x, int y)
    {
        var at = y * stride + x * 4;
        return b.Get(at) + b.Get(at + 1) + b.Get(at + 2);
    }

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame == 1)
            cube!.Rebuild(Shapes3d.Cube());
        // cube は視野の上の方 (world の +Y) に置く
        var at = new Vec3(0, 1.2f, 0);
        ren!.Begin(new Camera { Eye = new Vec3(0, 0, -5), Target = new Vec3(0, 0, 0) });
        ren.Draw(cube, Mat4.Translate(at));
        ren.End();
        var ldr = Gfx.LookupTexture("r3d_orient/ldr");
        Check.That(ldr != null, "r3d_orient/ldr was not declared");
        if (ldr == null)
            return;
        int? id = frame == 3 ? 1 : null;
        Gfx.ReadTexture(rb!, ldr, id, out var st, out var bytes, out var w, out var h, out _, out var stride, out _, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st == Gfx.ReadbackStatus.Ready && bytes != null)
        {
            var p = Camera3d.Project(ren.ViewProj!.Value, at.X, at.Y, at.Z, w, h);
            Check.That(p != null, "the cube is behind the camera");
            int x = (int)p!.Value.X;
            int y = (int)p.Value.Y;
            Check.That(y < h / 2, "the cube is projected to row " + y + ", want the upper half");
            int lit = Lum(bytes, stride, x, y);
            int mirrored = Lum(bytes, stride, x, h - 1 - y);
            Check.That(lit > 30 && mirrored < 10,
                "ldr row " + y + " (where the cube is on screen) has " + lit + " and row " + (h - 1 - y) + " has " + mirrored + ", want the cube at row " + y);
            Check.Pass();
            return;
        }
        Check.That(frame < 20, "the readback did not arrive");
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

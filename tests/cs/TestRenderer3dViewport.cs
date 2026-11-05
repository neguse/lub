// Renderer3d.Viewport の検査: 中の絵 (ldr) は範囲の大きさになり、カメラの縦横比は範囲の比になる。
// 範囲の中に置いた cube が、範囲の幅と高さで Camera3d.Project した px に写っていることを ldr の readback で見る。
// 画面のどこに出るかは TestRenderer3dViewportDraw (golden) が見る。
using System;
using Lub;
using Lubx;

public static class TestRenderer3dViewport
{
    const int VW = 40;
    const int VH = 24;
    static int frame;
    static Renderer3d? ren;
    static Mesh3d? cube;
    static Readback? rb;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 64,
            Height = 64,
        });
        ren = new Renderer3d("r3d_viewport");
        ren.Background = Color.Rgb(0, 0, 0);
        ren.Ssao.Enabled = false;
        ren.Bloom.Enabled = false;
        ren.Aa.Enabled = false;
        ren.Dither = false;
        ren.Viewport = new Rect(16, 8, VW, VH);
        cube = new Mesh3d("r3d_viewport_cube");
        rb = Gfx.Readback("r3d_viewport_rb");
    }

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame == 1)
            cube!.Rebuild(Shapes3d.Cube());
        // 範囲の右上の方 (縦横比が範囲の比でないと、写る位置がずれる)
        var at = new Vec3(2.0f, 0.8f, 0);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Gfx.EndPass();
        ren!.Begin(new Camera { Eye = new Vec3(0, 0, -5), Target = new Vec3(0, 0, 0) });
        ren.Draw(cube, Mat4.Translate(at));
        ren.End();
        var ldr = Gfx.LookupTexture("r3d_viewport/ldr");
        Check.That(ldr != null, "r3d_viewport/ldr was not declared");
        if (ldr == null)
            return;
        int? id = frame == 3 ? 1 : null;
        Gfx.ReadTexture(rb!, ldr, id, out var st, out var bytes, out var w, out var h, out _, out var stride, out _, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st == Gfx.ReadbackStatus.Ready && bytes != null)
        {
            Check.That(w == VW && h == VH, "ldr is " + w + "x" + h + ", want the viewport size " + VW + "x" + VH);
            var vp = ren.ViewProj!.Value;
            Check.That(Math.Abs(vp.M11 / vp.M00 - (float)VW / VH) < 0.001f,
                "the camera aspect is " + (vp.M11 / vp.M00) + ", want " + ((float)VW / VH));
            var p = Camera3d.Project(vp, at.X, at.Y, at.Z, VW, VH);
            Check.That(p != null, "the cube is behind the camera");
            int x = (int)p!.Value.X;
            int y = (int)p.Value.Y;
            Check.That(x > VW / 2 && x < VW && y >= 0 && y < VH / 2, "the cube is projected to " + x + "," + y + ", want the upper right of the viewport");
            var o = y * stride + x * 4;
            int lit = bytes.Get(o) + bytes.Get(o + 1) + bytes.Get(o + 2);
            Check.That(lit > 30, "ldr at " + x + "," + y + " (where the cube is projected) has " + lit + ", want the cube");
            Check.Pass();
            return;
        }
        Check.That(frame < 20, "the readback did not arrive");
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

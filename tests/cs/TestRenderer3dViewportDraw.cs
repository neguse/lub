// Visual test of Renderer3d.Viewport.
// After clearing the screen with gray, draws with Renderer3d only in the bottom-right region (48, 32)-(112, 96).
// Expected: outside the region stays gray, and the background color (blue) and a sphere appear inside the region. The sphere is round with the aspect ratio of the region (1:1).
// Pass/fail is decided by the rendering golden (tests/golden/TestRenderer3dViewportDraw_*.png).
using System;
using Lub;
using Lubx;

public static class TestRenderer3dViewportDraw
{
    static Renderer3d? ren;
    static Mesh3d? ball;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 128,
            Height = 96,
        });
        ren = new Renderer3d("r3d_viewport_draw");
        ren.Background = Color.Rgb(0.1f, 0.2f, 0.6f);
        ren.Viewport = new Rect(48, 32, 64, 64);
        ball = new Mesh3d("r3d_viewport_draw_ball");
    }

    public static void OnFrame(float dt)
    {
        if (!ball!.Ready())
            ball.Rebuild(Shapes3d.Sphere(12, 24));
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.5f, 0.5f, 0.5f, 1 } });
        Gfx.EndPass();
        ren!.Begin(new Camera { Eye = new Vec3(0, 0, -3), Target = new Vec3(0, 0, 0) });
        ren.Draw(ball, Mat4.Identity());
        ren.End();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

// Renderer3d.Viewport の visual test。
// 画面を灰色で消した後、右下の範囲 (48, 32)-(112, 96) だけに Renderer3d で描く。
// 期待: 範囲の外は灰色のまま残り、範囲の中に背景色 (青) と球が写る。球は範囲の縦横比 (1:1) で丸い。
// 合否は描画の golden (tests/golden/TestRenderer3dViewportDraw_*.png) で決まる。
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

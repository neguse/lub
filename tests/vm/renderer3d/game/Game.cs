using Lub;
using Lubx;

public static class Game
{
    public static Renderer3d? Ren;
    public static int Drawn;
    static Mesh3d? ball;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        Ren = new Renderer3d("vm_renderer3d");
        ball = new Mesh3d("vm_renderer3d_ball");
    }

    public static void OnFrame(float dt)
    {
        if (!ball!.Ready())
            ball.Rebuild(Shapes3d.Sphere(4, 8));
        Ren!.Begin(new Camera { Eye = new Vec3(0, 0, -3), Target = new Vec3(0, 0, 0) });
        Ren.Draw(ball, Mat4.Identity());
        if (ball.Ready())
            Drawn++;
        Ren.End();
    }
}

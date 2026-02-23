using Sokol.App;
using Sokol.Gfx;
using Sokol.Time;

public class Game
{
    private static float passedTime;

    public static void Init()
    {
        Time.Setup();
    }

    public static void Frame()
    {
        passedTime = passedTime + (float)App.FrameDuration();

        var t = passedTime;
        var r = (float)(System.Math.Sin(t) * 0.5 + 0.5);
        var g = (float)(System.Math.Sin(t + 2.0) * 0.5 + 0.5);
        var b = (float)(System.Math.Sin(t + 4.0) * 0.5 + 0.5);

        var pass = new Pass();
        var action = new PassAction();
        var color = new ColorAttachmentAction();
        color.LoadAction = LoadAction.CLEAR;
        color.ClearValue = new Color();
        color.ClearValue.R = r;
        color.ClearValue.G = g;
        color.ClearValue.B = b;
        color.ClearValue.A = 1.0f;

        Gfx.BeginPass(pass);
        Gfx.EndPass();
        Gfx.Commit();
    }

    public static void Cleanup()
    {
        Gfx.Shutdown();
    }

    public static int ScreenWidth()
    {
        return App.Width();
    }

    public static int ScreenHeight()
    {
        return App.Height();
    }
}

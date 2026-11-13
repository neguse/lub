using Lub;

public static class Game
{
    public static void OnInit() { }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(
            new PassOpts
            {
                Target = Gfx.MainTex,
                ClearColor = new float[] { 0.1f, 0.12f, 0.18f, 1 },
            }
        );
        Gfx.EndPass();
    }
}

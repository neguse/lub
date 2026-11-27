// Ui.Render draws using the UI shader of the runtime.
// On the export backend, that shader is also compiled for the export target (the export tests of tests/lub-test check this).
using System;
using Lub;

public static class TestUiRender
{
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    // lub export draws 1 frame and records the shaders and files used
    public static void OnExport() { OnFrame(0); }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        if (Ui.BeginWindow("ui"))
            Ui.Text("hello");
        Ui.EndWindow();
        Ui.Render();
        Gfx.EndPass();
        if (++frames >= 3)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

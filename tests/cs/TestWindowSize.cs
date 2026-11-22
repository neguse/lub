// Width / Height of App.Config become the rendering size (Gfx.Size).
// Specifies a portrait size and checks the aspect ratio (on high-DPI backends the pixel count is an integer multiple of the specified size).
// On web it prints a `canvas WxH` line, and the exported page check (export-page-check.mjs) checks that the canvas pixel count matches it.
using System;
using Lub;

public static class TestWindowSize
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan",
            Width = 360,
            Height = 640,
        });
    }

    public static void OnExport() { }

    public static void OnFrame(float dt)
    {
        Gfx.Size(out var w, out var h);
        Check.That(w >= 360 && w * 640 == h * 360,
            "test_window_size: Gfx.Size " + w + "x" + h + ", want the 360x640 aspect");
        Console.WriteLine("canvas " + w + "x" + h);
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

// App.Config の Width / Height が描画の大きさ (Gfx.Size) になる。縦長を指定して縦横比を見る (高 DPI の backend では画素数が指定の整数倍になる)。web では
// `canvas WxH` の行を出し、export した page の検査 (export-page-check.mjs) が canvas の画素数がそれと同じかを見る。
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

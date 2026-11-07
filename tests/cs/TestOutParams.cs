// out の引数を持つ API が、各 out にその out の値を返すこと。
// Ui.ColorEdit3 は触られていなければ入力の色をそのまま返し、Gfx.ResourceInfo は handle の key と実効 version を返す。
// どちらも out の順を入れ替えると値が合わなくなる組で呼ぶ。
using System;
using System.Collections.Generic;
using Lub;

public static class TestOutParams
{
    const string Key = "test_out_params";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        Ui.ColorEdit3("color", 0.25f, 0.5f, 0.75f, out var r, out var g, out var b);
        Check.That(r == 0.25f && g == 0.5f && b == 0.75f,
            "Ui.ColorEdit3: got (" + r + ", " + g + ", " + b + "), want (0.25, 0.5, 0.75)");

        // handle は 1 からの通し番号なので、この test が宣言した texture の handle は先頭の方にある
        Gfx.UseTexture(Key, 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 255, 0, 0, 255 }, 7);
        var found = 0;
        for (var handle = 1; handle <= 1024; handle++)
        {
            if (!Gfx.ResourceInfo(handle, out var key, out var version) || key != Key)
                continue;
            found++;
            Check.That(version == 7, "Gfx.ResourceInfo: version " + version + ", want 7");
        }
        Check.That(found == 1, "Gfx.ResourceInfo: " + found + " handles have the key " + Key + ", want 1");
        var stale = Gfx.ResourceInfo(0, out var staleKey, out var staleVersion);
        Check.That(!stale && staleKey == null && staleVersion == 0,
            "Gfx.ResourceInfo(0): got " + stale + " '" + staleKey + "' " + staleVersion + ", want False '' 0");
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

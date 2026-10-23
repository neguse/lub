// Lubx.Atlas の再主張が version を保つことと、sweep 後に作り直すことを見る。
// (Lua 版は pixels を配列でない値に差し替えて「読まれないこと」も見ていたが、型の違う値は C# では渡せないので移していない。)
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class TestAtlas
{
    const int sweepFrames = 2;

    static int frame;
    static Atlas? dynamicAtlas;
    static Atlas? fixedAtlas;
    static int firstVersion;

    static List<int> Pixels()
    {
        var px = new List<int>();
        for (int i = 0; i < 2 * 2 * 4; i++)
            px.Add(255);
        return px;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
            ResourceSweepAfterFrames = sweepFrames,
        });
        dynamicAtlas = Atlas.FromPixels("atlas_dynamic", 2, 2, Pixels());
        fixedAtlas = Atlas.FromPixels("atlas_fixed", 2, 2, Pixels(), 7);
    }

    public static void OnFrame(float dt)
    {
        frame++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        var d = dynamicAtlas!;
        var f = fixedAtlas!;

        if (frame == 1)
        {
            Check.That(d.Ensure() && f.Ensure(), "first ensure must upload");
            firstVersion = d.Texture!.Version;
        }
        else if (frame == 2)
        {
            Check.That(d.Ensure(), "reassertion must succeed");
            Check.That(d.Texture!.Version == firstVersion, "reassertion must keep the version");
            Check.That(f.Ensure(), "constant version must succeed");
        }
        else if (frame == 3 + sweepFrames + 1)
        {
            Check.That(Gfx.LookupTexture("atlas_dynamic") == null, "unused atlas must be swept");
            Check.That(d.Ensure(), "swept atlas must be recreated");
            Check.That(Gfx.LookupTexture("atlas_dynamic") != null, "recreated atlas must be live");
            d.UpdatePixels(Pixels());
            int before = d.Texture!.Version;
            d.Ensure();
            Check.That(d.Texture!.Version != before, "update_pixels must issue a new version");
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

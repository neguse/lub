using System;
using System.Collections.Generic;
using static Lub;

class BindingTest
{
    public static void Main()
    {
        Console.WriteLine(Gfx.MainTex.Version);
        var texture = Gfx.UseTexture("image", 1, 1, Gfx.PixelFormat.Rgba8,
            new List<int> { 255, 128, 0, 255 }, 7, new TextureOpts { Target = true });
        Console.WriteLine(texture.Version);
        Gfx.Draw(3, new Dictionary<string, object> {
            ["image"] = texture,
            ["uniforms"] = new Dictionary<string, object> { ["color"] = new float[] { 1, 2, 3, 4 } }
        }, new DrawOpts { Depth = false });
        var view = Xr.GetView(0, .05f, 500);
        Console.WriteLine(view.Width);
        Console.WriteLine(view.ViewProjection[15]);
        Console.WriteLine(Xr.GetView(1, .05f, 500) == null);
        Audio.Voice("voice", 12, new VoiceOpts { Volume = .25f, Loop = true });
    }
}

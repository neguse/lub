// Checks that the lubx drawing APIs do not allocate on per-frame calls (checked through the per-line allocations of Sys.ProfileLines).
// Opens ../child/Child.cs with App.Use and inspects it.
// Every frame, the OnFrame of the opened app creates an array at Child.cs:20 (a control that allocates), creates a Color.Rgb at Child.cs:21, draws 10 characters each with Text.Draw at Child.cs:24 and Child.cs:25, draws SpriteBatch.Rect at Child.cs:26 and Child.cs:27, and creates a Quat at Child.cs:30 and a Rect at Child.cs:31.
// 4 frames after counting starts, checks that Child.cs:20 has allocations and that Child.cs:21, Child.cs:25, Child.cs:27, Child.cs:30, and Child.cs:31 have none (glyphs and kerning are all created in the first frame).
// Child.cs:24 and Child.cs:26 include the SpriteBatch redeclaring its atlas texture at the start of the frame (Atlas.Ensure), so they are not checked.
using System;
using Lub;

public static class Game
{
    static int frame;

    static string ChildCsproj()
    {
        // GAME_CS is <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/Child.csproj");
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var rt = Gfx.UseTexture("child", 64, 64, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (rt == null) return;
        var child = App.Use("child", ChildCsproj());
        var ok = App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (Sys.Seconds() > 120)
        {
            Console.WriteLine("FAIL timeout after " + frame + " frames of the opened app");
            App.Quit(1);
            return;
        }
        // Step is false while waiting for the build
        if (!ok) return;
        frame++;
        if (frame == 2)
        {
            Sys.ProfileLines(true, child);
            return;
        }
        if (frame < 6)
            return;
        int control = 0, color = 0, text = 0, rect = 0, quat = 0, area = 0;
        foreach (var p in Sys.TakeProfile(child))
        {
            if (p.Site.EndsWith("Child.cs:20"))
                control = p.Allocs;
            if (p.Site.EndsWith("Child.cs:21"))
                color = p.Allocs;
            if (p.Site.EndsWith("Child.cs:25"))
                text = p.Allocs;
            if (p.Site.EndsWith("Child.cs:27"))
                rect = p.Allocs;
            if (p.Site.EndsWith("Child.cs:30"))
                quat = p.Allocs;
            if (p.Site.EndsWith("Child.cs:31"))
                area = p.Allocs;
        }
        Sys.ProfileLines(false, child);
        var got = "control " + control + ", Color.Rgb " + color + ", Text.Draw " + text + ", SpriteBatch.Rect " + rect
            + ", Quat " + quat + ", Rect " + area;
        if (control == 0 || color != 0 || text != 0 || rect != 0 || quat != 0 || area != 0)
        {
            Console.WriteLine("FAIL allocations over 4 frames: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS " + got);
        App.Quit(0);
    }
}

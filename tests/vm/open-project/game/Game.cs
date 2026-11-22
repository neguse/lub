// Checks opening a .csproj with App.Use: the opener does not stop while waiting for the build of the opened app, and the app draws from the frame in which the build finishes.
// When the source of the opened app is rewritten and saved, only the opened app draws the new color through hot reload, and its statics (the counted frames) remain.
// Checks through App.Status the reload count, the diagnostics of a failing build, and that the diagnostics disappear after the fix.
// Passing the opened app to App.Restart recreates only the opened app.
using System;
using Lub;

public static class Game
{
    static Readback? rb;
    static int frames;
    static bool edited;
    static int blueAtEdit;
    static int phase;
    static int blueAtRestart;

    static string ChildDir()
    {
        // GAME_CS is <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/");
    }

    // Rewrite and save the source of the opened app
    static void Edit(string from, string to)
    {
        var path = ChildDir() + "Child.cs";
        Io.LoadText(path, out var text, out _, out _, out _);
        if (text == null || !text.Contains(from))
        {
            Console.WriteLine("FAIL cannot edit " + path + ": " + from);
            App.Quit(1);
            return;
        }
        Io.SaveText(path, text.Replace(from, to));
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        rb = Gfx.Readback("child");
    }

    public static void OnFrame(float dt)
    {
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        if (rt == null) return;
        var child = App.Use("child", ChildDir() + "Child.csproj");
        App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Gfx.ReadTexture(rb!, rt, frames, out var st, out var bytes, out _, out _, out _, out _, out _, out _, out _);
        frames++;
        if (Sys.Seconds() > 120)
        {
            Console.WriteLine("FAIL the opened app did not draw the edited color");
            App.Quit(1);
            return;
        }
        if (frames == 1 && App.Status(child).Running)
        {
            Console.WriteLine("FAIL the opened app is running before its build");
            App.Quit(1);
            return;
        }
        // The alpha of the texture does not become 255 until the opened app draws
        if (st != Gfx.ReadbackStatus.Ready || bytes!.Get(3) != 255) return;
        int red = bytes.Get(0), green = bytes.Get(1), blue = bytes.Get(2);
        if (!edited && red == 255 && green == 0 && blue >= 3)
        {
            // The strings to replace are written split up, so this line is not rewritten
            var before = App.Status(child);
            if (!before.Running || before.Reloads != 0)
            {
                Console.WriteLine($"FAIL before the edit: running={before.Running} reloads={before.Reloads}");
                App.Quit(1);
                return;
            }
            Edit("{ 1.0f, " + "0.0f, blue", "{ 0.0f, " + "1.0f, blue");
            edited = true;
            blueAtEdit = blue;
        }
        if (edited && phase == 0 && red == 0 && green == 255)
        {
            if (blue < blueAtEdit)
            {
                Console.WriteLine("FAIL the opened app started again: blue " + blue + " after " + blueAtEdit);
                App.Quit(1);
                return;
            }
            var status = App.Status(child);
            if (!status.Running || status.Reloads != 1 || status.Restarts != 0 || status.BuildError != "")
            {
                Console.WriteLine($"FAIL after the reload: running={status.Running} reloads={status.Reloads} restarts={status.Restarts} error={status.BuildError}");
                App.Quit(1);
                return;
            }
            // Save a source that does not build
            Edit("frame" + "++;", "frame" + "++ oops;");
            phase = 1;
        }
        if (phase == 1 && App.Status(child).BuildError != "")
        {
            var status = App.Status(child);
            if (!status.BuildError.Contains("Child.cs") || status.Reloads != 1 || !status.Running)
            {
                Console.WriteLine($"FAIL after the failed build: reloads={status.Reloads} running={status.Running} error={status.BuildError}");
                App.Quit(1);
                return;
            }
            // Fix it, and change the color too
            Edit("frame" + "++ oops;", "frame" + "++;");
            Edit("{ 0.0f, " + "1.0f, blue", "{ 0.5f, " + "1.0f, blue");
            phase = 2;
        }
        if (phase == 2)
        {
            var status = App.Status(child);
            // Before recreating, wait until the counted frames appear in blue
            if (status.Reloads >= 2 && status.BuildError == "" && blue >= 30)
            {
                blueAtRestart = blue;
                App.Restart(child);
                phase = 3;
            }
        }
        else if (phase == 3 && App.Status(child).Restarts == 1 && blue < blueAtRestart)
        {
            // Only the opened app starts over from OnInit, and its statics (the counted frames) return to their initial values
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }
}

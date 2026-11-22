// Compiles a small game with the tcs built into the lider player (Tcs.ref.cs, TcsSession) and applies it to the app opened with App.Use.
//   - Opening writes the module, and the opened app runs
//   - Changing the text writes a batch, which is applied without recreating the app (the module of the applied version is also written, but that does not recreate the app)
//   - Text that does not compile is an error, returns diagnostics, and writes nothing
//   - Removing a static initializer is a restart, and the app is recreated
//   - A session with another name is compiled separately
//   - Adding a file and replacing text are compiled in one go, and removing a file is a restart (because types disappear)
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestTcs
{
    const string Body = "Count += Step;";
    const string Source = "public static class Tiny\n{\n    public static int Step = 1;\n    public static int Count;\n\n"
        + "    public static void OnFrame(float dt)\n    {\n        " + Body + "\n    }\n}\n";
    static string dir = "";
    static readonly TcsSession a = new TcsSession("a");
    static readonly TcsSession b = new TcsSession("b");
    static bool opened;
    static int stage;
    static int frames;
    static int before;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
        dir = Environment.GetEnvironmentVariable("LIDER_ROOT") + "/.lub";
    }

    static string ModuleOf(string name)
    {
        return dir + "/" + name + "/Tiny.tcsb";
    }

    static string BatchOf(string name)
    {
        return dir + "/" + name + "/Tiny.batch";
    }

    static void Open(TcsSession s)
    {
        s.Open("Tiny", ModuleOf(s.Name), BatchOf(s.Name), new List<string>(), new List<string>(), new List<string>(),
            new List<string> { "Tiny.cs" }, new List<string> { Source });
    }

    public static void OnFrame(float dt)
    {
        var target = Gfx.UseTexture("tiny_target", 64, 64, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        AppRef? app = null;
        if (opened && target != null)
        {
            app = App.Use("tiny", ModuleOf("a"));
            App.Step(app, target, dt);
        }
        frames++;
        if (frames > 600)
            Fail("stopped at stage " + stage);
        if (stage == 0)
        {
            Open(a);
            var r = One(a, "module");
            if (r == null)
                return;
            Check(Disk.Stat(ModuleOf("a")) != null, "the module was not written");
            opened = true;
            stage = 1;
        }
        else if (stage == 1 && app != null && Count(app) >= 10)
        {
            a.Update("Tiny.cs", Source.Replace(Body, "Count += Step * 100;"));
            if (One(a, "batch") == null)
                return;
            Check(Disk.Stat(BatchOf("a")) != null, "the batch was not written");
            before = Count(app);
            stage = 2;
            frames = 0;
        }
        else if (stage == 2 && app != null && frames == 3)
        {
            var status = App.Status(app);
            Check(status.Reloads == 1 && status.Restarts == 0, "the batch was not applied in place: reloads " + status.Reloads + ", restarts " + status.Restarts);
            Check(Disk.Stat(BatchOf("a")) == null, "the batch was not consumed");
            Check(Count(app) >= before + 100, "the edit is not running: count " + Count(app) + " after " + before);
            a.Update("Tiny.cs", Source.Replace(Body, "Count += ;"));
            var r = One(a, "error");
            if (r == null)
                return;
            Check(r.Errors.Count > 0 && r.Revision == 2, "a broken edit should report errors and keep revision 2: " + r.Errors.Count + " errors, revision " + r.Revision);
            Check(Disk.Stat(BatchOf("a")) == null, "a broken edit wrote a batch");
            a.Update("Tiny.cs", Source.Replace("Step = 1;", "Step;"));
            r = One(a, "restart");
            if (r == null)
                return;
            Check(r.Reasons.Count > 0, "a restart should say why");
            before = Count(app);
            stage = 3;
            frames = 0;
        }
        else if (stage == 3 && app != null && frames == 3)
        {
            var status = App.Status(app);
            Check(status.Restarts == 1 && Count(app) < before, "the restart did not start the game again: restarts " + status.Restarts + ", count " + Count(app) + " after " + before);
            Open(b);
            if (One(b, "module") == null)
                return;
            a.Update(new List<string>(), new List<string> { "Tiny.cs", "Extra.cs" },
                new List<string> { Source.Replace(Body, "Count += Step * 1000;"), "public static class Extra\n{\n    public static int X;\n}\n" });
            var r = One(a, "batch");
            if (r == null)
                return;
            Check(r.Revision == 4, "the other session changed this one, or one update compiled twice: revision " + r.Revision);
            a.Update(new List<string> { "Extra.cs" }, new List<string>(), new List<string>());
            r = One(a, "restart");
            if (r == null)
                return;
            Check(r.Revision == 5, "removing a file should compile once: revision " + r.Revision);
            Console.WriteLine("PASS");
            App.Quit(0);
            stage = 4;
        }
    }

    // Current value of the game's Count (-1 if it cannot be read)
    static int Count(AppRef app)
    {
        foreach (var n in Sys.Inspect("Tiny", 0, 100, app))
            if (n.Name == "Count" && int.TryParse(n.Text, out var count))
                return count;
        return -1;
    }

    // Receive exactly one result, and stop if it is not kind
    static TcsResult? One(TcsSession s, string kind)
    {
        var got = s.Take();
        if (got.Count != 1 || got[0].Kind != kind)
        {
            Fail("session " + s.Name + ": want one " + kind + ", got " + got.Count + (got.Count > 0 ? " (" + got[0].Kind + " " + string.Join(" | ", got[0].Errors) + ")" : ""));
            return null;
        }
        return got[0];
    }

    static void Check(bool ok, string message)
    {
        if (!ok)
            Fail(message);
    }

    static void Fail(string message)
    {
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

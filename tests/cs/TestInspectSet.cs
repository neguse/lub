// Sys.InspectSet: the opener rewrites values of an app opened with App.Use.
// It can write int, float, bool, string, null for references, struct fields, List elements, and fields of class instances, and the next frame runs from those values.
// Things that cannot be written (an object itself, a struct itself, a path that cannot be followed, text that cannot be read as a value) return false with a reason, and the value does not change.
using System;
using System.Collections.Generic;
using Lub;

public struct TestInspectSetPoint
{
    public int X;
    public float Y;
}

public class TestInspectSetThing
{
    public int Hp = 3;
    public string Name = "a";
    public TestInspectSetThing? Next;
}

public static class TestInspectSetChild
{
    static int counter = 100;
    static float speed = 1.5f;
    static bool on;
    static string label = "start";
    static TestInspectSetPoint at;
    static List<int> items = new List<int> { 1, 2, 3 };
    static TestInspectSetThing thing = new TestInspectSetThing { Next = new TestInspectSetThing() };

    public static void OnFrame(float dt)
    {
        counter++;
        // Statics that are never read are not included in the module, so read all of them
        if (on && speed > 1000 && label == "" && at.X + at.Y > 1000 && items.Count + thing.Hp > 1000)
            counter = 0;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
    }
}

public static class TestInspectSet
{
    static int frame;
    const string Child = "TestInspectSetChild";

    static string Read(AppRef child, string parent, string name)
    {
        foreach (var n in Sys.Inspect(parent, 0, 100, child))
        {
            if (n.Name == name) return n.Text;
        }
        return "(none)";
    }

    static void Set(AppRef child, string path, string text)
    {
        var ok = Sys.InspectSet(path, text, out var error, child);
        Check.That(ok, $"InspectSet {path} = {text} failed: {error}");
    }

    static void Refused(AppRef child, string path, string text, string why)
    {
        var ok = Sys.InspectSet(path, text, out var error, child);
        Check.That(!ok && error != null && error.Contains(why), $"InspectSet {path} = {text} should be refused with '{why}': {ok} {error}");
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = Gfx.UseTexture("child", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", Child);
        if (frame == 0)
        {
            // Cannot write to an app that has not loaded a module yet
            Check.That(!Sys.InspectSet(Child + ".counter", "1", out var early, child) && early != null, "InspectSet before the first Step must fail");
        }
        Check.That(App.Step(child, rt, dt), $"frame {frame}: Step failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        frame++;
        if (frame == 1)
        {
            Set(child, Child + ".counter", "50");
            Set(child, Child + ".speed", "-2.25");
            Set(child, Child + ".on", "true");
            Set(child, Child + ".label", "\"he said \\\"hi\\\"\"");
            Set(child, Child + ".at.X", "7");
            Set(child, Child + ".at.Y", "0.5");
            Set(child, Child + ".items[1]", "20");
            Set(child, Child + ".thing.Hp", "9");
            Set(child, Child + ".thing.Next.Name", "\"b\"");
            Check.That(Read(child, Child, "counter") == "50", "counter is " + Read(child, Child, "counter"));
            Check.That(Read(child, Child, "speed") == "-2.25", "speed is " + Read(child, Child, "speed"));
            Check.That(Read(child, Child, "on") == "true", "on is " + Read(child, Child, "on"));
            Check.That(Read(child, Child, "label") == "\"he said \"hi\"\"", "label is " + Read(child, Child, "label"));
            Check.That(Read(child, Child + ".at", "X") == "7" && Read(child, Child + ".at", "Y") == "0.5", "at is " + Read(child, Child, "at"));
            Check.That(Read(child, Child + ".items", "[1]") == "20", "items[1] is " + Read(child, Child + ".items", "[1]"));
            Check.That(Read(child, Child + ".thing", "Hp") == "9", "thing.Hp is " + Read(child, Child + ".thing", "Hp"));
            Check.That(Read(child, Child + ".thing.Next", "Name") == "\"b\"", "thing.Next.Name is " + Read(child, Child + ".thing.Next", "Name"));
            // Refuses things that cannot be written, and the value does not change
            Refused(child, Child + ".counter", "many", "cannot read");
            Refused(child, Child + ".counter", "1.5", "cannot read");
            Refused(child, Child + ".on", "1", "cannot read");
            Refused(child, Child + ".label", "bare", "cannot read");
            Refused(child, Child + ".thing", "5", "is an object");
            Refused(child, Child + ".at", "5", "is a struct");
            Refused(child, Child + ".missing", "5", "no value at");
            Refused(child, Child, "5", "no value at");
            Check.That(Read(child, Child, "counter") == "50", "a refused write changed counter: " + Read(child, Child, "counter"));
            // References can be set to null
            Set(child, Child + ".thing.Next", "null");
            Check.That(Read(child, Child + ".thing", "Next") == "(none)", "thing.Next is still there");
        }
        if (frame == 2)
        {
            // The next frame runs from the written values
            Check.That(Read(child, Child, "counter") == "51", "the next frame did not start from the written value: " + Read(child, Child, "counter"));
            // Omitting app means the caller itself (the caller can follow the statics of TestInspectSetChild, but they are separate from the values of the opened app)
            Check.That(Sys.InspectSet("TestInspectSet.frame", "2", out var own), "InspectSet on the own app failed: " + own);
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

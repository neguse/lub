// TestTrace の overlay。
using System.Collections.Generic;
using Lub;

public static class TestTraceTool
{
    static int frames;

    public static void OnFrame(float dt)
    {
        frames++;
        var hit = false;
        var miss = false;
        foreach (var key in Sys.TakeTrace())
        {
            if (key.EndsWith("TestTrace.cs:" + TestTrace.HitLine))
                hit = true;
            if (key.EndsWith("TestTrace.cs:" + TestTrace.MissLine))
                miss = true;
        }
        Check.That(hit, "frame " + frames + ": the executed line " + TestTrace.HitLine + " was not traced");
        Check.That(!miss, "the line not taken was traced");
        var values = new Dictionary<string, string>();
        foreach (var v in Sys.TakeValues())
        {
            var at = v.Site.IndexOf("TestTrace.cs:");
            if (at >= 0)
                values[v.Site.Substring(at)] = v.Text;
        }
        // ゲームの OnFrame は overlay の前に走るので、この frame の値
        Expect(values, "TestTrace.cs:31:9:frames", "" + TestTrace.frames);
        Expect(values, "TestTrace.cs:34:9:label", "\"frame " + TestTrace.frames + "\"");
        Expect(values, "TestTrace.cs:35:9:at", "Spot{X=" + TestTrace.frames + " Y=2}");
        Expect(values, "TestTrace.cs:36:9:seen", "[2]");
        Check.That(!values.ContainsKey("TestTrace.cs:33:13:frames"), "the assignment not taken was recorded");
        // 2 回目も同じ行と値が返る (読んだ記録は空になり、次の frame でまた積まれる)
        if (frames == 2)
            Check.Pass();
    }

    static void Expect(Dictionary<string, string> values, string site, string text)
    {
        Check.That(values.ContainsKey(site), "frame " + frames + ": " + site + " was not recorded");
        if (values.ContainsKey(site))
            Check.That(values[site] == text, site + " is " + values[site] + ", want " + text);
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}

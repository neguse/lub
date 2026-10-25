// TestTrace の overlay。
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
        // 2 回目も同じ行が返る (読んだ記録は空になり、次の frame でまた積まれる)
        if (frames == 2)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}

using System;
using System.Collections.Generic;
using Lub;

static class EventTest
{
    static List<EventData> seen = new List<EventData>();
    static int frames;

    public static void OnInit()
    {
        Console.WriteLine("init");
    }

    public static void OnEvent(EventData e)
    {
        seen.Add(e);
        Console.WriteLine("event " + (int)e.Kind + " key=" + e.Key + " button=" + e.Button + " x=" + e.X + " y=" + e.Y + " dy=" + e.Dy);
    }

    // 前の frame までに受け取った record を毎 frame 読み直す (GC の後も中身が残ること)
    public static void OnFrame(float dt)
    {
        frames++;
        var kept = "";
        foreach (var e in seen) kept += " " + (int)e.Kind + ":" + e.Key + ":" + e.X;
        Console.WriteLine("frame " + frames + kept);
    }

    public static void OnQuit()
    {
        Console.WriteLine("quit frames=" + frames + " seen=" + seen.Count);
    }
}

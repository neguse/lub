using System;
using System.Collections.Generic;
using Lub;

class Box
{
    public int Value;
}

// callback の fault が OnError に届き、Log なら次の callback へ進み、OnError 自身の fault は Abort になること。
// event の列は tests/c/tcs_event_host.c のもの。
static class ErrorTest
{
    static Box none;
    static int zero;
    static int errors;
    static List<string> messages = new List<string>();

    public static void OnInit()
    {
        Console.WriteLine("init");
    }

    public static void OnEvent(EventData e)
    {
        if (e.Kind == EventKind.KeyDown) Console.WriteLine("unreachable " + none.Value);
        Console.WriteLine("event " + (int)e.Kind);
    }

    public static void OnFrame(float dt)
    {
        var kept = "";
        foreach (var m in messages) kept += " " + m;
        Console.WriteLine("frame" + kept);
        Console.WriteLine("unreachable " + 1 / zero);
    }

    public static ErrorAction OnError(string message)
    {
        errors++;
        messages.Add(message);
        Console.WriteLine("error " + errors + " " + message);
        if (errors == 3) Console.WriteLine("unreachable " + none.Value);
        return ErrorAction.Log;
    }

    public static void OnQuit()
    {
        Console.WriteLine("quit errors=" + errors + " kept=" + messages.Count);
    }
}

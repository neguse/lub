// lub の Lubx.Jamo (かな漢字変換) で、かなを漢字に変換して確定する。
// 辞書は env LIDER_JAMO_DICT、無ければ lider の data/mozc.dict。
// jamo の無い lub か、辞書が無ければ確かめずに通す。
using System;
using Lub;
using Lubx;

public static class TestJamo
{
    static bool ran;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var dict = Environment.GetEnvironmentVariable("LIDER_JAMO_DICT");
        if (dict == null || dict == "")
            dict = (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/mozc.dict";
        if (!Jamo.Available() || Disk.Stat(dict) == null)
        {
            Console.WriteLine("SKIP no jamo or no dictionary at " + dict);
            Console.WriteLine("PASS");
            App.Quit(0);
            return;
        }
        var got = "";
        if (Jamo.Open(dict) && Jamo.Input("kyouhaiitenkidesune") && Jamo.Convert(9) && Jamo.Commit(out var text))
            got = text ?? "";
        if (got != "今日はいい天気ですね")
        {
            Console.WriteLine("FAIL converted to '" + got + "' with " + dict);
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    public static ErrorAction OnError(string message)
    {
        Console.WriteLine("FAIL " + message);
        return ErrorAction.Abort;
    }
}

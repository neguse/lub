// lider が探す file の一覧を確かめる。
//   - 足した .cs は、lider を出している間に探し直した一覧に入る
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestFiles
{
    static int frames;
    static float waited;
    static string added = "";
    static bool failed;

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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work;
        var root = Environment.GetEnvironmentVariable("LIDER_ROOT");
        Check(w != null && root != null, "LIDER_ROOT or the workspace is missing");
        if (failed)
            return;
        if (frames == 1)
        {
            added = root + "/added/Added.cs";
            Check(!w!.Files.Contains(added), "the workspace lists " + added + " before it is saved");
            Io.SaveText(added, "public static class Added\n{\n}\n");
            return;
        }
        waited += dt;
        if (!w!.Files.Contains(added))
        {
            Check(waited < 10, "the workspace did not list " + added + " in 10 seconds");
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // 最初の失敗だけを出して終わる
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

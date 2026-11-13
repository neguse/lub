// file の窓を 1 枚も開いていないうちから、窓が使う shader を宣言しておく。
// 初めて file を開いた frame で shader を作り始めると、その frame が作る間だけ止まる。
using System;
using Lub;
using LiderCore;

public static class TestPanelShaders
{
    static int frames;

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
        frames++;
        if (frames < 3)
            return;
        var w = Lider.Work;
        if (w == null || w.Windows.Count != 0)
        {
            Console.WriteLine("FAIL the workspace is missing or already has a window");
            App.Quit(1);
            return;
        }
        if (Gfx.LookupShader("lider_panel_clear") == null || Gfx.LookupShader("lider_panel_copy") == null)
        {
            Console.WriteLine("FAIL the window shaders are not declared before the first window opens");
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }
}

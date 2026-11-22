// Tab names in the title bar are drawn even for tabs toward the right of the bar.
// Opens 4 files, shows the first tab, reads back the title texture, and checks that every tab name area has text pixels
// (the background of tabs not shown is transparent, so the pixels are 0 if the name is not drawn).
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestTabLabels
{
    static int frames;
    static bool failed;
    static Window? window;
    static Readback? queue;
    static bool asked;

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
        frames++;
        if (failed)
            return;
        Check(frames < 600, "the readback did not arrive");
        var w = Lider.Work!;
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!;
        if (frames == 1)
        {
            w.FocusOn(target);
            w.FocusOn(target.Replace("Target.cs", "SecondLongName.cs"));
            w.FocusOn(target.Replace("Target.cs", "ThirdLongName.cs"));
            w.FocusOn(target.Replace("Target.cs", "FourthLongName.cs"));
            window = w.FocusWindow!;
            Lider.ShowTab(window, 0);
            return;
        }
        // Wait until the glyph atlas is complete and the title is redrawn
        if (frames < 20)
            return;
        var panel = Lider.TitleOf(window!);
        var labels = panel.Tabs;
        Check(labels.Count == 4 && panel.Tab == 0, "the title has " + string.Join("|", labels) + " at tab " + panel.Tab);
        if (failed)
            return;
        if (!asked)
        {
            queue = Gfx.Readback("test_tablabels_rb");
            Read(panel, 1);
            asked = true;
            return;
        }
        Read(panel, null);
    }

    static void Read(TitleView panel, int? id)
    {
        Gfx.ReadTexture(queue!, panel.Texture!, id, out var st, out var bytes, out var tw, out var th, out _, out var stride, out _, out _, out var err);
        Check(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready || bytes == null)
            return;
        var density = tw / panel.PxW;
        for (var i = 1; i < panel.Tabs.Count; i++)
        {
            // About the first 3 columns of the name
            var from = (int)(panel.TabX(i) * density);
            var to = from + 32 * density;
            var ink = 0;
            for (var y = 0; y < th; y++)
                for (var x = from; x < to && x < tw; x++)
                    if (bytes.Get(y * stride + x * 4 + 3) > 0)
                        ink++;
            Check(ink > 0, "tab " + i + " (" + panel.Tabs[i] + " at x " + panel.TabX(i) + ") has no letters");
        }
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

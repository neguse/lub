// 見出しの帯のタブの名前は、帯の右の方にあるタブでも描く。
// 4 つの file を開いて最初のタブを出し、見出しの texture を読み戻して、どのタブの名前の所にも文字の画素があることを確かめる
// (出していないタブの下地は透明なので、名前を描かなければ画素は 0)。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestTabLabels
{
    static int frames;
    static bool failed;
    static CodeWindow? window;
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
        // 文字の atlas が揃って見出しを描き直すまで待つ
        if (frames < 20)
            return;
        var panel = Lider.Panel(window!);
        var labels = panel.TabLabels;
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

    static void Read(PanelView panel, int? id)
    {
        Gfx.ReadTexture(queue!, panel.TitleTexture!, id, out var st, out var bytes, out var tw, out var th, out _, out var stride, out _, out _, out var err);
        Check(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready || bytes == null)
            return;
        var density = tw / panel.PxW;
        for (var i = 1; i < panel.TabLabels.Count; i++)
        {
            // 名前の頭の 3 桁ほど
            var from = (int)(panel.TabX(i) * density);
            var to = from + 32 * density;
            var ink = 0;
            for (var y = 0; y < th; y++)
                for (var x = from; x < to && x < tw; x++)
                    if (bytes.Get(y * stride + x * 4 + 3) > 0)
                        ink++;
            Check(ink > 0, "tab " + i + " (" + panel.TabLabels[i] + " at x " + panel.TabX(i) + ") has no letters");
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

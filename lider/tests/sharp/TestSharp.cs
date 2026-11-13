// 文字の密度を上げると、窓の板の層の texture は整数倍になり、窓の中の桁と行の高さは変わらず、文字は同じ所に細かく描かれる。
// 密度を上げた板でも、打った行の帯だけの描き直しと scroll は、新しく全部を描いた板と同じ画素になる。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestSharp
{
    const int Density = 2;
    static int frames;
    static bool failed;
    static MonoText? text;
    static MonoText? coarse;
    static Document? doc;
    static PanelView? low;
    static PanelView? partial;
    static PanelView? full;
    static readonly TraceLog log = new TraceLog();
    static int lastVersion = -1;
    static int step;
    static int rounds;
    static List<TextureRef> targets = new List<TextureRef>();
    static List<Readback> queues = new List<Readback>();
    static List<int> hashes = new List<int>();
    static List<int> widths = new List<int>();
    static List<int> rights = new List<int>();
    static int pending;

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
        if (failed || frames < 3)
            return;
        Check(frames < 1200, "the readback did not arrive");
        if (step == 0)
        {
            Check(PanelView.DensityFor(720) == 1, "720p should not raise the density");
            Check(PanelView.DensityFor(1080) == 2, "1080p should double the density");
            Check(PanelView.DensityFor(2160) == 3, "2160p should triple the density");
            Check(PanelView.DensityFor(8640) == 3, "the density should be capped");
            var ttf = (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/yutapon_coding_081d.ttf";
            coarse = new MonoText("test_sharp_coarse", ttf, 15);
            text = new MonoText("test_sharp_fine", ttf, 15, Density);
            Check(Math.Abs(text.CellW - coarse.CellW) < 0.001f, "the cell width changed: " + text.CellW + " " + coarse.CellW);
            Check(Math.Abs(text.LineH - coarse.LineH) < 0.001f, "the line height changed: " + text.LineH + " " + coarse.LineH);
            Document.Boot(coarse.CellW, coarse.LineH);
            doc = Document.Open(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!)!;
            doc.MoveTo(1, Utf8.Bytes(doc.Text(1)));
            low = new PanelView("test_sharp_low", doc.Id);
            partial = new PanelView("test_sharp_partial", doc.Id);
            step = 1;
            return;
        }
        EditorHost.Step();
        low!.Prepare(doc!, coarse!, log);
        partial!.Prepare(doc!, text!, log);
        full?.Prepare(doc!, text!, log);
        if (step == 1)
        {
            // 文字の atlas が揃うまで描き、atlas の届いた frame で描き直す
            var stable = text!.Version == lastVersion;
            lastVersion = text.Version;
            if (!stable)
                return;
            Want(new List<TextureRef> { low.FaceTexture!, partial.FaceTexture! });
            step = 2;
            return;
        }
        if (pending > 0)
        {
            Poll();
            return;
        }
        if (step == 2)
        {
            Check(widths[0] == PanelView.BasePxW, "the coarse texture is " + widths[0] + " wide");
            Check(widths[1] == PanelView.BasePxW * Density, "the fine texture is " + widths[1] + " wide");
            Check(rights[0] > 40, "the coarse panel drew nothing");
            // 右の端の文字は同じ所に出る (texture の px で倍。文字の形の差で数 px ずれる)
            Check(Math.Abs(rights[1] - rights[0] * Density) <= 6, "text ends at " + rights[1] + " and " + rights[0]);
            doc!.Type("1");
            EditorHost.Step();
            partial.Prepare(doc, text!, log);
            Check(partial.RedrawnLines == 1, "typing one character redrew " + partial.RedrawnLines + " lines (-1 is all)");
            full = new PanelView("test_sharp_full", doc.Id);
            full.Prepare(doc, text!, log);
            Compare();
            step = 3;
            return;
        }
        if (step == 3)
        {
            Same("typing");
            var top = EditorHost.FirstLine(doc!.Id);
            EditorHost.Scroll(doc.Id, -1.3f);
            EditorHost.Step();
            partial.Prepare(doc, text!, log);
            Check(EditorHost.FirstLine(doc.Id) > top, "scrolling did not move the view");
            Check(partial.RedrawnLines >= 0, "scrolling redrew everything");
            full = new PanelView("test_sharp_scrolled", doc.Id);
            full.Prepare(doc, text!, log);
            Compare();
            step = 4;
            return;
        }
        Same("scrolling");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Compare()
    {
        Want(new List<TextureRef> { partial!.FaceTexture!, full!.FaceTexture!, partial.FrontTexture!, full.FrontTexture!, partial.BackTexture!, full.BackTexture! });
    }

    static void Same(string after)
    {
        for (var i = 0; i < hashes.Count; i += 2)
            Check(hashes[i] == hashes[i + 1], "layer " + i / 2 + " differs after " + after);
    }

    static void Want(List<TextureRef> textures)
    {
        rounds++;
        targets = textures;
        queues = new List<Readback>();
        hashes = new List<int>();
        widths = new List<int>();
        rights = new List<int>();
        for (var i = 0; i < targets.Count; i++)
        {
            queues.Add(Gfx.Readback("test_sharp_rb_" + rounds + "_" + i)!);
            hashes.Add(0);
            widths.Add(0);
            rights.Add(0);
        }
        pending = targets.Count;
        for (var i = 0; i < targets.Count; i++)
            Read(i, i + 1);
    }

    static void Poll()
    {
        for (var i = 0; i < targets.Count; i++)
            if (hashes[i] == 0)
                Read(i, null);
    }

    static void Read(int i, int? id)
    {
        Gfx.ReadTexture(queues[i], targets[i], id, out var st, out var bytes, out var w, out var h, out _, out var stride, out _, out _, out var err);
        Check(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready || bytes == null)
            return;
        var hash = 17;
        var right = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var a = bytes.Get(y * stride + x * 4 + 3);
                hash = hash * 31 + a;
                if (a > 0 && x > right)
                    right = x;
            }
        hashes[i] = hash == 0 ? 1 : hash;
        widths[i] = w;
        rights[i] = right;
        pending--;
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

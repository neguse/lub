// 打って 1 行だけ変わったとき、窓の板は変わった行の帯だけを描き直し、全部を描き直したのと同じ画素になる。
// 同じ エディタの view を、帯だけを描き直した板と、新しく全部を描いた板で描き、3 つの層の texture を読み戻して比べる。
// 打たずに lub が使われない resource を捨てる frame の数 (既定 120) より長く待っても、描き直しに使う shader と文字の atlas は残っている (次に打ったときに compile や upload をし直さない)。
// scroll すると、texture をずらして出てきた行だけを描き直し、新しく全部を描いた板と同じ画素になる。
// 閉じた窓の板 (閉じる動きの間は描く) は、描き直さずに Declare だけを呼んでも、texture が残る。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestRedraw
{
    static int frames;
    static bool failed;
    static MonoText? text;
    static Document? doc;
    static PanelView? partial;
    static PanelView? warm;
    static PanelView? closing;
    static PanelView? full;
    static readonly TraceLog log = new TraceLog();
    static int stableAt;
    static int lastVersion = -1;
    static List<Readback> queues = new List<Readback>();
    static List<TextureRef> targets = new List<TextureRef>();
    static List<int> hashes = new List<int>();
    static int pending;
    static int comparedAt;
    static int scrolledAt;
    static PanelView? scrolled;

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
        Check(frames < 900, "the readback did not arrive");
        if (frames == 3)
        {
            Lider.SetActive(false);
            text = new MonoText("test_redraw_mono", (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/yutapon_coding_081d.ttf", 15);
            Document.Boot(text.CellW, text.LineH);
            doc = Document.Open(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!)!;
            doc.MoveTo(10, Utf8.Bytes(doc.Text(10)));
            warm = new PanelView("test_redraw_warm", doc.Id);
            return;
        }
        if (stableAt == 0)
        {
            // 文字の atlas が揃う (新しい文字が出なくなる) まで、別の板で描く
            EditorHost.Step();
            warm!.Prepare(doc!, text!, log);
            if (text!.Version == lastVersion)
            {
                stableAt = frames;
                partial = new PanelView("test_redraw_partial", doc!.Id);
                partial.Prepare(doc, text, log);
                closing = new PanelView("test_redraw_closing", doc.Id);
                closing.Prepare(doc, text, log);
            }
            lastVersion = text.Version;
            return;
        }
        if (frames == stableAt + 1)
        {
            doc!.Type("1");
            EditorHost.Step();
            partial!.Prepare(doc, text!, log);
            Check(text!.Version == lastVersion, "typing drew a new glyph");
            Check(partial.RedrawnLines == 1, "typing one character redrew " + partial.RedrawnLines + " lines (-1 is all)");
            full = new PanelView("test_redraw_full", doc.Id);
            full.Prepare(doc, text, log);
            Check(full.RedrawnLines == -1, "a new panel did not draw everything");
            Want(partial.FaceTexture!, full.FaceTexture!, partial.FrontTexture!, full.FrontTexture!, partial.BackTexture!, full.BackTexture!);
            return;
        }
        if (frames <= stableAt + 1)
            return;
        partial!.Prepare(doc!, text!, log);
        full!.Prepare(doc!, text!, log);
        Poll();
        if (pending > 0 || failed)
            return;
        if (comparedAt == 0)
        {
            Check(hashes[0] == hashes[1], "the face layer differs after redrawing one line");
            Check(hashes[2] == hashes[3], "the front layer differs after redrawing one line");
            Check(hashes[4] == hashes[5], "the back layer differs after redrawing one line");
            comparedAt = frames;
            return;
        }
        closing!.Declare(text!);
        if (frames < comparedAt + 150)
            return;
        if (scrolledAt == 0)
        {
            Check(Gfx.LookupShader("lider_panel_clear") != null && Gfx.LookupShader("lubx/sprite/instanced") != null,
                "the shaders for redrawing were dropped while nothing was typed");
            Check(Gfx.LookupTexture("test_redraw_mono") != null, "the glyph atlas was dropped while nothing was typed");
            Check(Gfx.LookupTexture("test_redraw_closing_face") != null, "the textures of a panel kept by Declare were dropped");
            var top = EditorHost.FirstLine(doc!.Id);
            EditorHost.Scroll(doc.Id, -1.3f);
            EditorHost.Step();
            partial!.Prepare(doc, text!, log);
            Check(EditorHost.FirstLine(doc.Id) > top, "scrolling did not move the view");
            Check(partial.RedrawnLines >= 0, "scrolling redrew everything");
            var after = new PanelView("test_redraw_scrolled", doc.Id);
            after.Prepare(doc, text!, log);
            scrolledAt = frames;
            queues = new List<Readback>();
            hashes = new List<int>();
            Want(partial.FaceTexture!, after.FaceTexture!, partial.FrontTexture!, after.FrontTexture!, partial.BackTexture!, after.BackTexture!);
            scrolled = after;
            return;
        }
        scrolled!.Prepare(doc!, text!, log);
        Poll();
        if (pending > 0 || failed)
            return;
        Check(hashes[0] == hashes[1], "the face layer differs after scrolling");
        Check(hashes[2] == hashes[3], "the front layer differs after scrolling");
        Check(hashes[4] == hashes[5], "the back layer differs after scrolling");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Want(TextureRef a, TextureRef b, TextureRef c, TextureRef d, TextureRef e, TextureRef f)
    {
        targets = new List<TextureRef> { a, b, c, d, e, f };
        for (var i = 0; i < targets.Count; i++)
        {
            queues.Add(Gfx.Readback("test_redraw_rb_" + (scrolledAt > 0 ? "s" : "") + i)!);
            hashes.Add(0);
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
        for (var k = 0; k < bytes.Length; k++)
            hash = hash * 31 + bytes.Get(k);
        hashes[i] = hash == 0 ? 1 : hash;
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

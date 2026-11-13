// 本文が動いている版と違う間の、インラインの形 (F9 で左右から切り替える)。
//   - 変えた行の上に、動いている版の対応しない行の数だけ行を空ける。消した行は、その次の行の上に挟む
//   - 空けた所の下の行は、その分だけ下に描く。押した所の行も、空けた行を数えて決める
//   - 左右の板は出さない。reload が当たって違いが無くなると、空けた行も無くなる
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestDiffInline
{
    static int frames;
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
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        frames++;
        var w = Lider.Work!;
        if (frames == 2)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        if (frames < 6)
            return;
        var window = w.FocusWindow!;
        var doc = window.Code.Doc;
        var panel = Lider.Panel(window);
        var id = window.Code.ViewId;
        var lh = EditorHost.LineHeight(id);
        if (frames == 6)
        {
            Press(Keys.F9);
            // Hp = 1 (8 行目) を消し、Hp = 2 を Hp = 20 にする
            doc.MoveTo(6, Utf8.Bytes(doc.Text(6)));
            for (var i = 0; i < Utf8.Bytes(doc.Text(7)) + 1; i++)
            {
                EditorHost.Key(Keys.Delete, true);
                EditorHost.Key(Keys.Delete, false);
            }
            doc.MoveTo(7, Utf8.Bytes(doc.Text(7)) - 1);
            doc.Type("0");
            EditorHost.Step();
        }
        if (frames == 8)
        {
            Check(doc.Text(7) == "        Hp = 20;" && doc.Text(8) == "    }", "the edit went wrong: " + doc.Text(7) + " / " + doc.Text(8));
            Check(!PanelView.SideBySide && panel.Inline && !panel.OldShown, "F9 did not switch to inline");
            var olds = doc.InlineOld();
            Check(olds.Count == 2 && olds[0] == 7 && olds[1] == 8 && doc.InlineAt()[0] == 8 && doc.InlineAt()[1] == 8,
                "the live lines Hp = 1 and Hp = 2 are not above line 8");
            Check(Math.Abs(EditorHost.LineY(id, 8) - EditorHost.LineY(id, 7) - 3 * lh) < 0.01f, "line 8 is not 2 rows lower");
            Check(Math.Abs(EditorHost.LineY(id, 9) - EditorHost.LineY(id, 8) - lh) < 0.01f, "line 9 does not follow line 8");
            Check(Math.Abs(panel.InlineY(doc, 0) - (EditorHost.LineY(id, 8) - 2 * lh)) < 0.01f
                && Math.Abs(panel.InlineY(doc, 1) - (EditorHost.LineY(id, 8) - lh)) < 0.01f, "the live lines are not in the gap");
            EditorHost.Click(id, EditorHost.TextX(id) + 4, EditorHost.LineY(id, 8) + lh * 0.5f);
            Check(EditorHost.CursorLine(id) == 8, "clicking line 8 went to line " + EditorHost.CursorLine(id));
            EditorHost.Click(id, EditorHost.TextX(id) + 4, EditorHost.LineY(id, 9) + lh * 0.5f);
            Check(EditorHost.CursorLine(id) == 9, "clicking line 9 went to line " + EditorHost.CursorLine(id));
            doc.Save();
            doc.Reloaded();
        }
        if (frames == 10)
        {
            Check(!panel.Inline && Math.Abs(EditorHost.LineY(id, 8) - EditorHost.LineY(id, 7) - lh) < 0.01f, "the gap is left after the reload");
            Press(Keys.F9);
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }

    static void Press(int key)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = 0, Typed = 0, Plain = 0 });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = 0, Typed = 0, Plain = 0 });
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

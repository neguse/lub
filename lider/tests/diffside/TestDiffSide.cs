// 本文がゲームの動いている版と違う間の、動いている版の板 (左右の形)。
//   - 違いが無い間は出さない。打って違いができると focus のある窓に出し、値の木と足あとの板は板の幅だけ左へ寄る
//   - 板の行は、カーソルの近くで対応する行を窓の同じ高さに揃える
//   - F9 で左右とインラインを切り替え、インラインの間は板を出さない。違いがある間は案内に F9 が出る
//   - reload が当たって違いが無くなると、板を消す
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestDiffSide
{
    static int frames;
    static bool failed;
    // 打つ前の値の木の板の角度と、動いている版の板と窓の間の幅 (角度)
    static float boardBefore;
    static float oldSpan;

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
        // lider が窓を置いた後に、窓が広がり終わるまで 1 秒分進める (開いた frame の重さで進む分に頼らない)
        if (frames == 4)
            Settle(w);
        // 窓が開いて動き終わるまで待つ (値の木の板の角度は、窓の動いている角度と大きさから決まる)
        if (frames < 60)
            return;
        var window = w.FocusWindow!;
        var doc = window.Doc;
        var panel = Lider.Panel(window);
        if (frames == 60)
        {
            Check(!doc.Differs() && !panel.OldShown, "the board is shown before editing");
            Check(!HasF9(), "F9 is in the hints without a difference");
            boardBefore = Lider.BoardAngle;
            // Step の '{' (7 行目) の下に 1 行足す
            doc.MoveTo(6, Utf8.Bytes(doc.Text(6)));
            doc.Type("\n        var x = 0;");
            EditorHost.Step();
        }
        // 動いている版の板が出た後に、板と窓が寄り終わるまで進める
        if (frames == 61)
            Settle(w);
        if (frames == 62)
        {
            Check(doc.Differs() && panel.OldShown, "the board is not shown after typing");
            Check(HasF9(), "F9 is not in the hints with a difference");
            // カーソルは足した行 (対応しない) にあるので、その上の '{' を揃える
            var lh = EditorHost.LineHeight(window.ViewId);
            Check(Math.Abs(panel.OldTop(doc) + 6 * lh - EditorHost.LineY(window.ViewId, 7)) < 0.5f,
                "the live line 7 is at " + (panel.OldTop(doc) + 6 * lh) + ", want " + EditorHost.LineY(window.ViewId, 7));
            var shift = boardBefore - Lider.BoardAngle;
            oldSpan = (PanelView.OldW * window.Scale + PanelView.OldGap) / Space.R;
            // 窓の大きさは動きで寄せているので、少しの差は許す
            Check(Math.Abs(shift - oldSpan) < oldSpan * 0.01f, "the left boards moved by " + shift + ", want " + oldSpan);
            Press(Keys.F9);
        }
        if (frames == 64)
        {
            Check(!PanelView.SideBySide && !panel.OldShown, "F9 did not hide the board");
            Check(Math.Abs(Lider.BoardAngle - boardBefore) < oldSpan * 0.01f, "the left boards did not come back: " + Lider.BoardAngle + ", want " + boardBefore);
            Press(Keys.F9);
        }
        if (frames == 66)
        {
            Check(PanelView.SideBySide && panel.OldShown, "F9 did not bring the board back");
            doc.Save();
            doc.Reloaded();
        }
        if (frames == 68)
        {
            Check(!doc.Differs() && !panel.OldShown, "the board is still shown after the reload");
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }

    static bool HasF9()
    {
        foreach (var h in Hints.For(new HintContext { Differs = Lider.Work!.FocusWindow!.Doc.Differs(), SideBySide = PanelView.SideBySide }))
            if (h.Key == "F9")
                return true;
        return false;
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

    // 窓の動きを 1 秒分進める
    static void Settle(Workspace w)
    {
        for (var i = 0; i < 60; i++)
            Lider.Animate(w, 1.0f / 60);
    }
}

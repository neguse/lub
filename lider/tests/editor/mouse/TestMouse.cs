// view の中をマウスで押して選ぶ。
//   - 押した所の文字の前に cursor が動く (文字の右半分なら後ろ)
//   - 押して動かして離すと、押した所からそこまでを選ぶ。離した後に動かしても変わらない
//   - 続けて 2 回押すと語、3 回押すと行を選ぶ
//   - gutter を押すと行の先頭に動く
//   - scroll の位置は LineY に入る
using System;
using Lub;
using LiderEditor;

public static class TestMouse
{
    const string Word = "abcdefghijklmnopqrstuvwxyz";
    static int stage;
    static float next;
    static int id;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    // line 行目の col 桁 (1 から、ASCII の行) の文字の左の端から dx 桁の所の px
    static float X(int col, float dx)
    {
        return EditorHost.TextX(id) + (col - 1 + dx) * Ed.CellW;
    }

    static float Y(int line)
    {
        return EditorHost.LineY(id, line) + Ed.LineH * 0.5f;
    }

    public static void OnFrame(float dt)
    {
        if (Ed.Failed || Sys.Seconds() < next)
            return;
        // 0.4 秒より間をあけて、前の stage の押しと続けて押したことにしない
        next = Sys.Seconds() + 0.5f;
        if (stage == 0)
        {
            Ed.Boot();
            var text = "";
            for (var i = 0; i < 40; i++)
                text += "// " + Word + " " + Word + "\n";
            id = Ed.Open(Ed.Write("Mouse.cs", text));
            EditorHost.Click(id, X(5, 0.25f), Y(3));
            Ed.CheckCursor(id, 3, 5);
            EditorHost.Click(id, X(5, 0.75f), Y(4));
            Ed.CheckCursor(id, 4, 6);
            // gutter
            EditorHost.Click(id, 5, Y(6));
            Ed.CheckCursor(id, 6, 1);
        }
        else if (stage == 1)
        {
            EditorHost.Press(id, X(4, 0), Y(2));
            EditorHost.Drag(id, X(8, 0), Y(4));
            EditorHost.Release(id, X(8, 0), Y(4));
            var s = EditorHost.Selection(id);
            Ed.Check(s == Word + " " + Word + "\n// " + Word + " " + Word + "\n// " + "abcd", "dragging selected '" + s + "'");
            EditorHost.Drag(id, X(8, 0), Y(9));
            Ed.Check(EditorHost.Selection(id) == s, "moving after the release changed the selection");
        }
        else if (stage == 2)
        {
            EditorHost.Click(id, X(10, 0), Y(2));
            EditorHost.Click(id, X(10, 0), Y(2));
            Ed.Check(EditorHost.Selection(id) == Word, "a double click selected '" + EditorHost.Selection(id) + "'");
            EditorHost.Click(id, X(10, 0), Y(2));
            Ed.Check(EditorHost.Selection(id) == "// " + Word + " " + Word + "\n", "a triple click selected '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 3)
        {
            // 11 行目が上の余白の下 (y = 7) に来る。上の余白に半分見える 10 行目も見えている行に数える (Lite XL の get_visible_line_range と同じ)
            EditorHost.ScrollToLine(id, 11);
            Ed.Check(EditorHost.FirstLine(id) == 10 && EditorHost.LineY(id, 11) == 7, "ScrollToLine 11: first line " + EditorHost.FirstLine(id) + " at " + EditorHost.LineY(id, 11));
            EditorHost.Click(id, X(1, 0), Y(12));
            Ed.CheckCursor(id, 12, 1);
            // 押して Step しても、見えている所は動かない
            EditorHost.Step();
            Ed.Check(EditorHost.FirstLine(id) == 10, "the click scrolled to line " + EditorHost.FirstLine(id));
            Ed.Pass();
            return;
        }
        stage++;
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

// cursor の動きと、keymap の編集の command を確かめる。
//   - 上下の移動は見た目の桁 (全角の文字は 2 桁) を保ち、UTF-8 の文字の途中に止まらない。短い行を通っても前の桁に戻る
//   - 左右に動くか打つと、その桁を保つ
//   - Ctrl+[ と Ctrl+] は空行で区切った塊の端へ、Ctrl+左右は語の端へ動く
//   - Home は字下げの後ろ、もう一度で行の先頭へ
//   - Return は字下げを引き継ぎ、Backspace は空白 4 つを 1 度に消す
//   - Tab と Shift+Tab で字下げを足し引きし、Ctrl+/ で行の注釈を切り替える
//   - Ctrl+D で語を選び、もう一度で次の同じ語に cursor を足す (打つと両方に入る)
//   - Ctrl+X で行を切り取り、Ctrl+V で貼る
using System;
using Lub;
using LiderEditor;

public static class TestMoves
{
    const string Text = "class A\n{\n    // 日本語のコメント\n    {\n        return 1;\n    }\n\n    int x = 1; int y = x;\n}\n";
    static bool ran;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        Ed.Boot();
        var id = Ed.Open(Ed.Write("Moves.cs", Text));
        // 5 行目の 11 桁目 ("        re" の後) から上へ: 短い行 "    {" を通って、全角の行の同じ見た目の桁へ
        EditorHost.SetCursor(id, 5, 11);
        Ed.Press(Ed.Up);
        Ed.CheckCursor(id, 4, 6);
        Ed.Press(Ed.Up);
        // 10 桁目は "    // 日" (9 桁) の後の 本 の真ん中より左なので、本 の前 (byte の位置 11)
        Ed.CheckCursor(id, 3, 11);
        Ed.Press(Ed.Down);
        Ed.Press(Ed.Down);
        Ed.CheckCursor(id, 5, 11);
        // 左へ 1 つ動くと桁は 9 になり、上の行では 日 の真ん中より右なので、やはり 本 の前
        Ed.Press(Ed.Left);
        Ed.Press(Ed.Up);
        Ed.Press(Ed.Up);
        Ed.CheckCursor(id, 3, 11);
        Ed.Press(Ed.Right);
        Ed.CheckCursor(id, 3, 14);
        // 塊と語の端
        Ed.Ctrl(48);
        Ed.CheckCursor(id, 7, 1);
        EditorHost.SetCursor(id, 9, 1);
        Ed.Ctrl(47);
        Ed.CheckCursor(id, 8, 5);
        Ed.Ctrl(Ed.Right);
        Ed.CheckCursor(id, 8, 8);
        Ed.Ctrl(Ed.Right);
        Ed.CheckCursor(id, 8, 10);
        Ed.Ctrl(Ed.Left);
        Ed.CheckCursor(id, 8, 9);
        Ed.Press(Ed.Home);
        Ed.CheckCursor(id, 8, 5);
        Ed.Press(Ed.Home);
        Ed.CheckCursor(id, 8, 1);
        // 字下げを引き継ぐ改行と、空白 4 つの Backspace
        EditorHost.SetCursor(id, 5, 18);
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Line(id, 6) == "        ", "newline indent: '" + EditorHost.Line(id, 6) + "'");
        Ed.Press(Ed.Backspace);
        Ed.Check(EditorHost.Line(id, 6) == "    ", "backspace in the indent: '" + EditorHost.Line(id, 6) + "'");
        Ed.Type("z");
        Ed.Check(EditorHost.Line(id, 6) == "    z", "typed: '" + EditorHost.Line(id, 6) + "'");
        Ed.Press(Ed.Tab);
        Ed.Check(EditorHost.Line(id, 6) == "    z    ", "tab after text: '" + EditorHost.Line(id, 6) + "'");
        Ed.Shift(Ed.Tab);
        Ed.Check(EditorHost.Line(id, 6) == "z    ", "shift+tab: '" + EditorHost.Line(id, 6) + "'");
        Ed.Ctrl(56);
        Ed.Check(EditorHost.Line(id, 6) == "// z    ", "ctrl+/: '" + EditorHost.Line(id, 6) + "'");
        Ed.Ctrl(56);
        Ed.Check(EditorHost.Line(id, 6) == "z    ", "ctrl+/ again: '" + EditorHost.Line(id, 6) + "'");
        // Ctrl+D: x を選び、次の x を足す
        EditorHost.SetCursor(id, 9, 9);
        Ed.Ctrl(7);
        Ed.Check(EditorHost.Selection(id) == "x", "ctrl+d selected '" + EditorHost.Selection(id) + "'");
        Ed.Ctrl(7);
        Ed.Type("q");
        Ed.Check(EditorHost.Line(id, 9) == "    int q = 1; int y = q;", "ctrl+d twice and typing: " + EditorHost.Line(id, 9));
        Ed.Press(Ed.Escape);
        EditorHost.Draw(id, 640, 360);
        Ed.Check(EditorHost.CaretCount(id) == 1, "escape kept " + EditorHost.CaretCount(id) + " cursors");
        // 行を切り取って貼る
        var lines = EditorHost.LineCount(id);
        EditorHost.SetCursor(id, 1, 3);
        Ed.Ctrl(27);
        Ed.Check(EditorHost.LineCount(id) == lines - 1 && EditorHost.Line(id, 1) == "{", "ctrl+x cut the line: " + EditorHost.Line(id, 1));
        Ed.Ctrl(25);
        Ed.Check(EditorHost.Line(id, 1) == "class A" && EditorHost.Line(id, 2) == "{", "ctrl+v pasted the line above: " + EditorHost.Line(id, 1));
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

// エディタの core に key と文字を流し込み、本文、cursor、undo、保存、描いた命令を確かめる (前の TestLiteXl と同じ操作)。
//   - Ctrl+Home と、続けて打った ASCII と日本語は、同じ frame に積んでも本文に入る
//   - 上下左右と End で動き、日本語の文字の途中に止まらない
//   - Shift+End で選んだ所を、打った文字で置き換える
//   - Ctrl+Z で戻し、Ctrl+Y でやり直す。0.3 秒の内の編集は 1 つにまとまるので、操作の間を 0.4 秒あける
//   - Ctrl+S で保存した file の中身
//   - 描いた命令の列に、打った行が gutter の右の 1 行目の位置に、全角を 2 桁とした幅で出る
//   - wheel の notch で scroll すると、見える最初の行が動く
// 最後に 1280 x 720 の view の Step と Draw にかかる時間を出す。
using System;
using Lub;
using LiderEditor;

public static class TestBasics
{
    const int S = 22;
    const int Y = 28;
    const int Z = 29;
    const string Original = "// TestBasics が開いて書き換える file。\npublic static class Target\n{\n    public static int Value()\n    {\n        return 1;\n    }\n}\n";

    static int stage;
    static float next;
    static int id;
    static string path = "";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (Ed.Failed || Sys.Seconds() < next)
            return;
        next = Sys.Seconds() + 0.4f;
        if (stage == 0)
        {
            Ed.Boot();
            path = Ed.Write("Target.cs", Original);
            id = Ed.Open(path);
            Ed.Check(EditorHost.LineCount(id) == 8, "opened " + EditorHost.LineCount(id) + " lines");
            Ed.Check(!EditorHost.Dirty(id), "dirty after opening");
        }
        else if (stage == 1)
        {
            Ed.Ctrl(Ed.Home);
            EditorHost.Text("// abc ");
            EditorHost.Text("日本語");
            Ed.Press(Ed.Return);
            EditorHost.Step();
            Ed.Check(EditorHost.Line(id, 1) == "// abc 日本語", "typed line 1: " + EditorHost.Line(id, 1));
            Ed.Check(EditorHost.Line(id, 2) == "// TestBasics が開いて書き換える file。", "line 2: " + EditorHost.Line(id, 2));
            Ed.CheckCursor(id, 2, 1);
        }
        else if (stage == 2)
        {
            // 行の末尾から左へ 2 文字で、本 の前 (byte の位置 11)
            Ed.Press(Ed.Up);
            Ed.Press(Ed.End);
            Ed.Press(Ed.Left);
            Ed.Press(Ed.Left);
            EditorHost.Step();
            Ed.CheckCursor(id, 1, 11);
        }
        else if (stage == 3)
        {
            Ed.Shift(Ed.End);
            EditorHost.Step();
            Ed.Check(EditorHost.Selection(id) == "本語", "selected '" + EditorHost.Selection(id) + "'");
            EditorHost.Text("ほんご");
            EditorHost.Step();
            Ed.Check(EditorHost.Line(id, 1) == "// abc 日ほんご", "replaced line 1: " + EditorHost.Line(id, 1));
        }
        else if (stage == 4)
        {
            Ed.Ctrl(Z);
            EditorHost.Step();
            Ed.Check(EditorHost.Line(id, 1) == "// abc 日本語", "undo: " + EditorHost.Line(id, 1));
            Ed.Check(EditorHost.Selection(id) == "本語", "undo restored the selection '" + EditorHost.Selection(id) + "'");
        }
        else if (stage == 5)
        {
            Ed.Ctrl(Y);
            EditorHost.Step();
            Ed.Check(EditorHost.Line(id, 1) == "// abc 日ほんご", "redo: " + EditorHost.Line(id, 1));
            Ed.Check(EditorHost.Dirty(id), "not dirty before saving");
        }
        else if (stage == 6)
        {
            var saves = EditorHost.SaveCount(id);
            Ed.Ctrl(S);
            EditorHost.Step();
            Ed.Check(!EditorHost.Dirty(id), "still dirty after Ctrl+S");
            Ed.Check(EditorHost.SaveCount(id) == saves + 1, "Ctrl+S was not counted");
            var text = Ed.Read(path);
            Ed.Check(text == "// abc 日ほんご\n" + Original, "saved file: " + text);
        }
        else if (stage == 7)
            CheckDraw();
        else
        {
            Measure();
            Ed.Check(EditorHost.LastError() == "", "error: " + EditorHost.LastError());
            Ed.Pass();
            return;
        }
        stage++;
    }

    // 1 行目の text の命令: gutter の右、1 行目の高さ、幅は "// abc " の 7 桁と 日ほんご の 4 文字 x 2 桁
    static void CheckDraw()
    {
        var n = EditorHost.Draw(id, 1280, 720);
        Ed.Check(n > 0 && EditorHost.CmdKind(0) == 3 && EditorHost.CmdW(0) == 1280 && EditorHost.CmdH(0) == 720, "the first command is not the clip of the view");
        var found = -1;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdText(i) == "// abc 日ほんご")
                found = i;
        Ed.Check(found >= 0, "no text command for the typed line in " + n + " commands");
        if (Ed.Failed)
            return;
        var x = EditorHost.CmdX(found);
        var y = EditorHost.CmdY(found);
        var w = EditorHost.CmdW(found);
        // gutter は 1 桁の行の番号と左右の余白 14 px
        Ed.Check(x == Ed.CellW + 28, "typed line at x " + x);
        Ed.Check(y == 7, "typed line at y " + y);
        Ed.Check(EditorHost.CmdLine(found) == 1, "typed line is tagged " + EditorHost.CmdLine(found));
        Ed.Check(w == 15 * Ed.CellW, "typed line is " + w + " px wide, want " + 15 * Ed.CellW);
        Ed.Check(EditorHost.FontName(EditorHost.CmdFont(found)) == "JetBrainsMono-Regular.ttf", "typed line in font " + EditorHost.FontName(EditorHost.CmdFont(found)));
        Ed.Check(EditorHost.TextX(id) == x && EditorHost.LineY(id, 1) == y && EditorHost.LineHeight(id) == Ed.LineH, "TextX, LineY or LineHeight do not match the command");
        // 2 行目は 1 行の高さだけ下
        var below = -1;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdText(i) == "// TestBasics が開いて書き換える file。")
                below = i;
        Ed.Check(below >= 0 && EditorHost.CmdY(below) == y + Ed.LineH && EditorHost.CmdX(below) == x, "line 2 is not one line below line 1");
        // 行の番号は gutter に右揃え、行に付かない
        var number = -1;
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdText(i) == "9")
                number = i;
        Ed.Check(number >= 0 && EditorHost.CmdLine(number) == 0 && EditorHost.CmdX(number) == 14, "the line number 9 is not in the gutter");
        // cursor は命令にしない (1 行目の 本 の前)
        Ed.Check(EditorHost.CaretCount(id) == 1, "carets: " + EditorHost.CaretCount(id));
        Ed.Check(EditorHost.CaretY(id, 0) == y, "caret at y " + EditorHost.CaretY(id, 0));
    }

    // 長い file を 1280 x 720 の view に開き、Step と Draw の 1 回にかかる時間を測る
    static void Measure()
    {
        var text = "";
        for (var i = 0; i < 2000; i++)
            text += "    public static int Value" + i + "() { return \"text\" + " + i + " /* note */; } // comment\n";
        var big = Ed.Open(Ed.Write("Big.cs", text));
        EditorHost.Draw(big, 1280, 720);
        var top = EditorHost.FirstLine(big);
        EditorHost.Scroll(big, -3);
        EditorHost.Step();
        EditorHost.Draw(big, 1280, 720);
        Ed.Check(EditorHost.FirstLine(big) > top, "scrolling 3 notches down kept the first line at " + EditorHost.FirstLine(big) + " (was " + top + ")");
        Ed.Check(EditorHost.ScrollY(big) == 150, "scrolled to " + EditorHost.ScrollY(big) + " px");
        const int rounds = 100;
        var t0 = Sys.Seconds();
        for (var i = 0; i < rounds; i++)
            EditorHost.Step();
        var t1 = Sys.Seconds();
        var cmds = 0;
        for (var i = 0; i < rounds; i++)
            cmds = EditorHost.Draw(big, 1280, 720);
        var t2 = Sys.Seconds();
        for (var i = 0; i < rounds; i++)
        {
            EditorHost.Text("x");
            EditorHost.Step();
            EditorHost.Draw(big, 1280, 720);
        }
        var t3 = Sys.Seconds();
        // 末尾へ飛ぶと、そこまでの行を色分けする
        Ed.Ctrl(77);
        EditorHost.Step();
        EditorHost.Draw(big, 1280, 720);
        var t4 = Sys.Seconds();
        Console.WriteLine("cost: step " + Ms(t1 - t0, rounds) + " ms, draw " + Ms(t2 - t1, rounds) + " ms (" + cmds
            + " commands), type+step+draw " + Ms(t3 - t2, rounds) + " ms, jump to the end " + Ms(t4 - t3, 1) + " ms, " + EditorHost.LineCount(big) + " lines");
        EditorHost.Close(big);
    }

    static string Ms(float seconds, int rounds)
    {
        return ((float)Math.Round(seconds * 1000 / rounds * 100) / 100).ToString();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

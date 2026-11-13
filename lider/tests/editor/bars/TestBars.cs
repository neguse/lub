// 窓の下の帯 (CommandView) と上の帯 (NagView) を確かめる。
//   - Ctrl+F で検索の帯が窓の下に出て、打った文字で探し、Return で見つけた所を選ぶ。F3 で次を、Shift+F3 で前を探す
//   - 帯の Escape は帯を閉じ、検索を始めた所の選びに戻す
//   - Ctrl+G で行へ移動する
//   - Ctrl+R で置き換える (選んでいなければ全部)
//   - NagView は窓の上に出て、左右で選んで Return で答え、y と n でも答えられる。答えると本文に打てる
//   - Ctrl+N は名前の無い doc を開き (TakeOpened)、Ctrl+S は名前を尋ねて保存する
//   - Ctrl+O で path を打って開き、Ctrl+P で名前の一部を打って開く
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

public static class TestBars
{
    const string Text = "class A\n{\n    int F()\n    {\n        return 1;\n    }\n    int G()\n    {\n        return 2;\n    }\n}\n";
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
        var path = Ed.Write("Bars.cs", Text);
        Ed.Write("other.cs", "// other\npublic static class Other\n{\n}\n");
        var id = Ed.Open(path);

        Ed.Ctrl(9);
        Ed.Check(EditorHost.Modal(), "Ctrl+F did not open the find bar");
        var label = Ed.Command(id, "Find Text: ", 0);
        Ed.Check(label >= 0 && EditorHost.CmdLine(label) == -1 && EditorHost.CmdY(label) > 360 / 2, "the find bar is not drawn at the bottom");
        Ed.Type("return");
        Ed.Check(EditorHost.Selection(id) == "return" && EditorHost.CursorLine(id) == 5, "typing did not preview the match");
        Ed.Press(Ed.Return);
        Ed.Check(!EditorHost.Modal(), "Return did not close the find bar");
        Ed.Check(EditorHost.CursorLine(id) == 5 && EditorHost.Selection(id) == "return", "found '" + EditorHost.Selection(id) + "' on line " + EditorHost.CursorLine(id));
        Ed.Press(Ed.F3);
        Ed.Check(EditorHost.CursorLine(id) == 9 && EditorHost.Selection(id) == "return", "F3 found '" + EditorHost.Selection(id) + "' on line " + EditorHost.CursorLine(id));
        Ed.Shift(Ed.F3);
        Ed.Check(EditorHost.CursorLine(id) == 5, "Shift+F3 moved to line " + EditorHost.CursorLine(id));

        // Escape は帯を閉じ、選びを戻す
        EditorHost.SetCursor(id, 1, 1);
        Ed.Ctrl(9);
        Ed.Type("int");
        Ed.Check(EditorHost.CursorLine(id) == 3, "the preview did not move to line 3");
        Ed.Press(Ed.Escape);
        Ed.Check(!EditorHost.Modal() && EditorHost.CursorLine(id) == 1 && EditorHost.CursorCol(id) == 1, "Escape did not restore the cursor");

        Ed.Ctrl(10);
        Ed.Type("7");
        Ed.Press(Ed.Return);
        Ed.CheckCursor(id, 7, 1);

        // 置き換え (選んでいないので全部)
        EditorHost.SetCursor(id, 1, 1);
        Ed.Ctrl(21);
        Ed.Type("int");
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Modal(), "the replace bar did not ask for the new text");
        Ed.Mods(false, false);
        // 打った int が選ばれているので、打つと置き換わる
        Ed.Type("long");
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Line(id, 3) == "    long F()" && EditorHost.Line(id, 7) == "    long G()", "replaced: " + EditorHost.Line(id, 3) + " / " + EditorHost.Line(id, 7));

        // 確認の帯
        EditorHost.Ask(id, "Check", "Answer this?", new List<string> { "Yes", "No" });
        EditorHost.Step();
        Ed.Check(EditorHost.Modal(), "the NagView did not take the keys");
        var message = Ed.Command(id, "Answer this?", 0);
        Ed.Check(message >= 0 && EditorHost.CmdLine(message) == -1 && EditorHost.CmdY(message) < 360 / 2, "the NagView is not drawn at the top");
        Ed.Press(Ed.Right);
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.LastAnswer() == "No", "answered '" + EditorHost.LastAnswer() + "', want 'No'");
        Ed.Check(!EditorHost.Modal(), "the NagView kept the keys after the answer");
        EditorHost.Ask(id, "Check", "Again?", new List<string> { "Yes", "No" });
        Ed.Type("y");
        Ed.Check(EditorHost.LastAnswer() == "Yes" && !EditorHost.Modal(), "y answered '" + EditorHost.LastAnswer() + "'");
        EditorHost.SetCursor(id, 1, 1);
        Ed.Type("x");
        Ed.Check(EditorHost.Line(id, 1) == "xclass A", "typed after the NagView: " + EditorHost.Line(id, 1));

        // Ctrl+N と、名前を付けて保存
        Ed.Ctrl(17);
        var untitled = EditorHost.TakeOpened();
        Ed.Check(untitled > 0 && untitled != id && EditorHost.Path(untitled) == "", "Ctrl+N did not open a new doc");
        Ed.Type("z");
        Ed.Check(EditorHost.Line(untitled, 1) == "z" && EditorHost.Line(id, 1) == "xclass A", "typed into the new doc: " + EditorHost.Line(untitled, 1));
        Ed.Check(EditorHost.Dirty(untitled), "the new doc is not dirty");
        Ed.Ctrl(22);
        Ed.Check(EditorHost.Modal(), "Ctrl+S on the new doc did not ask for a name");
        Ed.Type("made.cs");
        Ed.Press(Ed.Return);
        Ed.Check(EditorHost.Path(untitled).EndsWith("/made.cs") && !EditorHost.Dirty(untitled), "saved the new doc as '" + EditorHost.Path(untitled) + "'");
        Ed.Check(Ed.Read("made.cs") == "z\n", "made.cs has '" + Ed.Read("made.cs") + "'");

        // Ctrl+O と Ctrl+P
        EditorHost.Focus(id);
        Ed.Ctrl(18);
        Ed.Check(EditorHost.Modal(), "Ctrl+O did not open the open bar");
        Ed.Type("other.cs");
        Ed.Press(Ed.Return);
        var other = EditorHost.TakeOpened();
        Ed.Check(other > 0 && EditorHost.Line(other, 1) == "// other", "Ctrl+O opened '" + EditorHost.Path(other) + "'");
        EditorHost.Focus(id);
        Ed.Ctrl(19);
        Ed.Type("othe");
        Ed.Press(Ed.Return);
        var found = EditorHost.TakeOpened();
        Ed.Check(found > 0 && EditorHost.Path(found) == EditorHost.Path(other), "Ctrl+P othe opened '" + EditorHost.Path(found) + "'");
        Ed.Check(EditorHost.LastError() == "", "error: " + EditorHost.LastError());
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

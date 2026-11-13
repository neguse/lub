// 横の scroll。
//   - cursor が窓の右に出ると、cursor が見える所まで横に scroll する
//   - cursor が scroll しなくても見える所へ戻ると、行の頭が欠けないよう左の端に戻す
using System;
using Lub;
using LiderEditor;

public static class TestHscroll
{
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
        var text = "using System;\nclass A\n{\n    // xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n    int x = 1;\n}\n";
        var id = Ed.Open(Ed.Write("Hscroll.cs", text));
        var left = EditorHost.TextX(id);
        // 4 行目 (窓より長い行) の末尾へ
        EditorHost.SetCursor(id, 4, 78);
        EditorHost.Step();
        EditorHost.Draw(id, 640, 360);
        Ed.Check(EditorHost.TextX(id) < left, "the end of the long line did not scroll: text at " + EditorHost.TextX(id) + ", left " + left);
        // 下の短い行の末尾 (9 桁目) へ: scroll しなくても見える
        Ed.Press(Ed.Down);
        EditorHost.Step();
        EditorHost.Draw(id, 640, 360);
        Ed.CheckCursor(id, 5, 15);
        Ed.Check(EditorHost.TextX(id) == left, "the view stayed scrolled on a short line: text at " + EditorHost.TextX(id) + ", want " + left);
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

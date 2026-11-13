// C# の色分け: 注釈、keyword、型、文字列、数、関数の名前、ふつうの名前が、描いた命令の色で分かれる。
// 行をまたぐ注釈と文字列は次の行も同じ色にし、閉じた後は元に戻す。.cs でない file は色分けしない。
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

public static class TestSyntax
{
    const string Text = "// note\npublic static class Target\n{\n    static string Name = \"text\\\"q\";\n    public static int Value() { return 0x1F + 2.5f; }\n    /* open\n    still */ int after;\n    #if DEBUG\n    char c = '\\'';\n}\n";
    static bool ran;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static Dictionary<string, int> Colors(int id)
    {
        var colors = new Dictionary<string, int>();
        var n = EditorHost.Draw(id, 640, 360);
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdLine(i) > 0)
                colors[EditorHost.CmdText(i).Trim()] = EditorHost.CmdColor(i);
        return colors;
    }

    public static void OnFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        Ed.Boot();
        var id = Ed.Open(Ed.Write("Target.cs", Text));
        var colors = Colors(id);
        var names = new List<string> { "Target", "// note", "public", "string", "\"text\\\"q\"", "0x1F", "Value", "+" };
        foreach (var name in names)
            Ed.Check(colors.ContainsKey(name), "no text command for '" + name + "'");
        if (Ed.Failed)
            return;
        for (var a = 0; a < names.Count; a++)
            for (var b = a + 1; b < names.Count; b++)
                Ed.Check(colors[names[a]] != colors[names[b]], "'" + names[a] + "' and '" + names[b] + "' have the same color");
        Ed.Check(colors["static"] == colors["public"], "'static' is not colored as a keyword like 'public'");
        Ed.Check(colors.ContainsKey("2.5f") && colors["2.5f"] == colors["0x1F"], "2.5f is not a number");
        Ed.Check(colors.ContainsKey("/* open") && colors.ContainsKey("still */") && colors["still */"] == colors["// note"], "the block comment does not span two lines");
        Ed.Check(colors.ContainsKey("int") && colors["int"] == colors["string"], "after the block comment: " + (colors.ContainsKey("int") ? "" : "no int"));
        Ed.Check(colors.ContainsKey("#if") && colors["#if"] == colors["public"], "#if is not a keyword");
        Ed.Check(colors.ContainsKey("'\\''") && colors["'\\''"] == colors["\"text\\\"q\""], "the escaped char literal is not a string");
        // 色分けは行の命令を語ごとに分け、前の空白は語に付く
        Ed.Check(Ed.Command(id, "    public", 5) >= 0, "the indent is not merged into 'public'");
        // 注釈を開くと、後ろの行が注釈の色になる
        EditorHost.SetCursor(id, 3, 1);
        EditorHost.Insert(id, "/*");
        colors = Colors(id);
        Ed.Check(colors.ContainsKey("/*{") && colors.ContainsKey("public static int Value() { return 0x1F + 2.5f; }"), "opening a comment did not color the following lines");
        var plain = Ed.Open(Ed.Write("notes.txt", "public static int x = 1;\n"));
        Ed.Check(Ed.Command(plain, "public static int x = 1;", 1) >= 0, "a .txt file was colored");
        Ed.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}

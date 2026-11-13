// エディタの core (src/Editor) の test が共に使う道具。scripts/test-editor.sh が test ごとに一緒に変換する。
// key は lider の Workspace と同じ順に渡す: 修飾 key の押し離し、key の押し (EventData.Plain の文字で)、Ctrl を押していなければ打った文字、key の離し。
// 失敗は最初の 1 つだけを出して App.Quit(1) で終わる。
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class Ed
{
    public const int Return = 40;
    public const int Escape = 41;
    public const int Backspace = 42;
    public const int Tab = 43;
    public const int Space = 44;
    public const int Home = 74;
    public const int End = 77;
    public const int Delete = 76;
    public const int Right = 79;
    public const int Left = 80;
    public const int Down = 81;
    public const int Up = 82;
    public const int F3 = 60;
    public const int F6 = 63;
    public const int F7 = 64;
    public const int F9 = 66;
    public const int F10 = 67;
    public const int CellW = 10;
    public const int LineH = 20;

    public static bool Failed;
    static bool ctrl;
    static bool shift;

    // 最初の失敗だけを出して終わる
    public static void Check(bool cond, string message)
    {
        if (cond || Failed)
            return;
        Failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    public static void Pass()
    {
        if (Failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    public static void Boot()
    {
        var err = EditorHost.Init(CellW, LineH);
        Check(err == "", "Init failed: " + err);
    }

    // 開いて focus を移した view の id
    public static int Open(string path)
    {
        var id = EditorHost.Open(path);
        Check(id > 0, "cannot open " + path);
        EditorHost.Focus(id);
        EditorHost.Step();
        EditorHost.Draw(id, 640, 360);
        return id;
    }

    public static void Mods(bool c, bool s)
    {
        if (c != ctrl)
            EditorHost.Key(Keys.LeftCtrl, c);
        if (s != shift)
            EditorHost.Key(Keys.LeftShift, s);
        ctrl = c;
        shift = s;
    }

    public static void Press(int key)
    {
        var typed = Keys.UsChar(key, shift);
        var plain = Keys.UsChar(key, false);
        EditorHost.KeyNamed(key, true, plain);
        if (!ctrl && typed != 0)
            EditorHost.Text(Utf8.Encode(typed));
        EditorHost.KeyNamed(key, false, plain);
    }

    public static void Ctrl(int key)
    {
        Mods(true, false);
        Press(key);
        Mods(false, false);
    }

    public static void Shift(int key)
    {
        Mods(false, true);
        Press(key);
        Mods(false, false);
    }

    public static void CtrlShift(int key)
    {
        Mods(true, true);
        Press(key);
        Mods(false, false);
    }

    // 英小文字と数字と . / を、key を押して打つ
    public static void Type(string s)
    {
        foreach (var r in s.EnumerateRunes())
        {
            var c = r.Value;
            var key = 0;
            if (c >= 'a' && c <= 'z')
                key = 4 + (c - 'a');
            else if (c >= '1' && c <= '9')
                key = 30 + (c - '1');
            else if (c == '0')
                key = 39;
            else if (c == '.')
                key = 55;
            else if (c == '/')
                key = 56;
            else if (c == ' ')
                key = Space;
            Check(key != 0, "cannot type '" + Utf8.Encode(c) + "'");
            Press(key);
        }
    }

    // 描いた命令のうち、text の文字の命令 (無ければ -1)。line が 0 でなければ、その行の命令だけを見る
    public static int Command(int id, string text, int line)
    {
        var n = EditorHost.Draw(id, 640, 360);
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdText(i) == text && (line == 0 || EditorHost.CmdLine(i) == line))
                return i;
        return -1;
    }

    public static void CheckCursor(int id, int line, int col)
    {
        Check(EditorHost.CursorLine(id) == line && EditorHost.CursorCol(id) == col,
            "cursor at " + EditorHost.CursorLine(id) + ":" + EditorHost.CursorCol(id) + ", want " + line + ":" + col);
    }

    // test の file を書いて、その path
    public static string Write(string name, string text)
    {
        var dir = Environment.GetEnvironmentVariable("EDITOR_TEST_DIR") ?? ".";
        var path = dir + "/" + name;
        Io.SaveText(path, text);
        return path;
    }

    public static string Read(string path)
    {
        Io.LoadText(path, out var text, out _, out _, out _);
        return text ?? "";
    }

    public static ErrorAction OnError(string message)
    {
        Console.WriteLine("FAIL " + message);
        return ErrorAction.Abort;
    }
}

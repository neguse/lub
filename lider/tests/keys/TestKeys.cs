// lider の key 操作を流し込み、Target.cs を開いた Document の行を確かめる。
//   - 上下の移動は見た目の桁 (全角の文字は 2 桁) を保ち、UTF-8 の文字の途中に止まらない
//   - lider の桁の数え方 (Utf8.Cells) が、エディタの描いた幅と合う
//   - 短い行を通っても、上下に動く前の桁に戻る。
//     左右に動くか打つと、その桁を保つ
//   - Ctrl を押したまま F1 で隠して離すと、次に出したとき Ctrl が押したままになっていない
//   - 隠している間に打った key は Document に入らない
//   - 文字と エディタの key の名前は、物理の位置でなく lub の渡す文字で決まる (JIS 配列)
//   - F1 を押しっぱなしにして繰り返しの KeyDown が届いても、切り替えるのは 1 度だけ
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestKeys
{
    static bool ran;
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
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    // 最初の frame で 1 度だけ流す
    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(path != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        w!.FocusOn(path!);
        var doc = w.Focus;
        Check(doc != null, "cannot open " + path);
        Check(doc!.Find("static int"), "no 'static int' in " + path);
        if (failed)
            return;

        // ASCII の行の 11 桁目から、短い行 `    {` を通って `        return 1;` へ下がる
        var line = doc.Line;
        Press(w, Keys.Down);
        Press(w, Keys.Down);
        Check(doc.Line == line + 2 && doc.Col == 11, "Down twice moved to " + doc.Line + ":" + doc.Col + ", want " + (line + 2) + ":11");

        // 日本語の行へ上がって打つ。11 桁目は「日本」の後 (全角は 2 桁)
        Press(w, Keys.Up);
        Press(w, Keys.Up);
        Press(w, Keys.Up);
        Press(w, Keys.A);
        Check(doc.Line == line - 1, "Up moved to line " + doc.Line + ", want " + (line - 1));
        Check(doc.Text(doc.Line) == "    // 日本a語のコメント", "typed after Up: " + doc.Text(doc.Line));
        CheckCells(doc, doc.Text(doc.Line), 2);

        // 打った後の桁 12 で下がり、左へ 1 つ動いた桁 11 で上がる
        Press(w, Keys.Down);
        Check(doc.Col == 12, "Down after typing moved to column " + doc.Col + ", want 12");
        Press(w, Keys.Left);
        Press(w, Keys.Up);
        Press(w, Keys.Right);
        Check(doc.Line == line - 1 && doc.Col == 14, "Left, Up, Right moved to " + doc.Line + ":" + doc.Col + ", want " + (line - 1) + ":14");

        // どの物理の key でも、Mods が Ctrl なら Ctrl として効く (CapsLock を Ctrl にしている環境)
        var caps = 57;
        Send(EventKind.KeyDown, caps, Keys.ModCtrl);
        Check(w.Ctrl, "Mods did not make Ctrl down");
        Send(EventKind.KeyUp, caps);
        Check(!w.Ctrl, "Mods did not make Ctrl up");

        // lub から届く順に流す。
        // 隠している間に打った a はゲームのもので、Document には入らない
        Send(EventKind.KeyDown, Keys.LeftCtrl, Keys.ModCtrl);
        Send(EventKind.KeyDown, Keys.F1, Keys.ModCtrl);
        Send(EventKind.KeyUp, Keys.LeftCtrl);
        Check(!w.Ctrl, "Ctrl stayed down after it was released while hidden");
        Send(EventKind.KeyUp, Keys.F1);
        Send(EventKind.KeyDown, Keys.A);
        Send(EventKind.KeyUp, Keys.A);
        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyUp, Keys.F1);
        Check(w.Active, "lider is not active after pressing F1 twice");
        Send(EventKind.KeyDown, Keys.A);
        Send(EventKind.KeyUp, Keys.A);
        Check(doc.Text(doc.Line) == "    // 日本aa語のコメント", "typed after F1: " + doc.Text(doc.Line));

        // JIS 配列: US の [ の位置は @、Shift+2 は "、¥ と ろ の key。半角/全角は文字を打たない
        Type(47, 0, '@', '@');
        Type(31, Keys.ModShift, '"', '2');
        Type(137, 0, '\\', '\\');
        Type(135, Keys.ModShift, '_', '\\');
        Type(53, 0, 0, 0);
        Type(137, 0, 0xA5, 0xA5);
        Check(doc.Text(doc.Line) == "    // 日本aa@\"\\_¥語のコメント", "typed on JIS: " + doc.Text(doc.Line));

        // keymap も論理の key で引く: JIS の @ の Ctrl は何もせず、US の ] の位置の [ の Ctrl は前の block へ動く
        var jisLine = doc.Line;
        Type(47, Keys.ModCtrl, '@', '@');
        Check(doc.Line == jisLine, "Ctrl+@ ran the binding of Ctrl+[: line " + doc.Line);
        var deep = doc.Find("return 1;");
        Check(deep, "no 'return 1;'");
        var bodyLine = doc.Line;
        Type(48, Keys.ModCtrl, '[', '[');
        Check(doc.Line < bodyLine, "Ctrl+[ on JIS did not move from line " + bodyLine);
        Send(EventKind.KeyUp, Keys.LeftCtrl);

        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyDown, Keys.F1);
        Send(EventKind.KeyUp, Keys.F1);
        Check(!w.Active, "holding F1 toggled lider more than once");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    // lider の桁 (Utf8.Cells、MonoText が文字を置く桁) と、エディタが描いた幅が合う: 全角の行の幅 = 桁の数 x ASCII の行 (asciiLine 行目、1 から) から引いた桁の幅。
    // ASCII の行は色分けで語ごとの命令に分かれるので、その行の text の命令の幅を足す
    static void CheckCells(Document doc, string wide, int asciiLine)
    {
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        var ascii = doc.Text(asciiLine - 1);
        var wideW = -1.0f;
        var asciiW = 0.0f;
        for (var i = 0; i < n; i++)
        {
            if (EditorHost.CmdKind(i) != 2)
                continue;
            if (EditorHost.CmdText(i) == wide)
                wideW = EditorHost.CmdW(i);
            if (EditorHost.CmdLine(i) == asciiLine)
                asciiW += EditorHost.CmdW(i);
        }
        var cell = asciiW / Utf8.Bytes(ascii);
        Check(wideW > 0 && cell > 0 && System.Math.Abs(wideW - Utf8.Cells(wide, Utf8.Bytes(wide)) * cell) < 0.01f,
            "'" + wide + "' is " + wideW + " px wide in the editor, " + Utf8.Cells(wide, Utf8.Bytes(wide)) + " cells of " + cell + " px in lider");
    }

    static void Send(EventKind kind, int key, int mods = 0)
    {
        Lider.OnEvent(new EventData
        {
            Kind = kind, Key = key, Mods = mods,
            Typed = Keys.UsChar(key, (mods & Keys.ModShift) != 0), Plain = Keys.UsChar(key, false),
        });
    }

    static void Type(int key, int mods, int typed, int plain)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = key, Mods = mods, Typed = typed, Plain = plain });
        Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = key, Mods = mods, Typed = typed, Plain = plain });
    }

    static void Press(Workspace w, int key)
    {
        w.Key(key, true);
        w.Key(key, false);
    }

    // 最初の失敗だけを出して終わる
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

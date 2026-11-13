// lider に編集操作を流し込んで保存すると、ゲームの状態を保ったまま反映されることを確かめる。
// ゲーム (game/Game.cs) は毎 frame 数を数え、Target.Value() を読む。数と値は lider がゲームを見るのと同じ道 (GameApp.Inspect) で読む。
// ゲームが動き始めたら、lider で game/Target.cs の `return 1;` を `return 2;` に書き換えて保存し、reload chunk が当たったら次を確かめる。
//   - Target.Value() が 2 を返す (反映された)
//   - lider がゲームの reload を受け取った (Lider.Reloads)
//   - ゲームの数が初期値に戻っていない
//   - 保存した file の中身と、Document の保存済みの状態
//   - 書き換えた行は、保存しても reload chunk が当たるまで浮いたまま (LineChanged、窓の手前の層に描く) で、当たると沈む (面に描く)
//   - 改行が CRLF の file は、CRLF のまま保存する
//   - 縮小図は、開いた file の板に打った内容を出す
// scripts/test.sh が一時ディレクトリに写して起動する (game/Target.cs を書き換えるため)。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestEdit
{
    static int countAt;
    static Document? edited;
    static float waited;
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

    static void TestFrame(float dt)
    {
        if (failed)
            return;
        waited += dt;
        // ゲームの build を待つ
        if (GameApp.Frames == 0)
        {
            Check(waited < 60, "the game did not start in 60 seconds");
            return;
        }
        if (edited == null)
        {
            Edit();
            waited = 0;
            return;
        }
        var value = Game("Value");
        if (value == 1)
        {
            Check(Lider.Reloads == 0, "lider got a reload before the edit was applied");
            Check(edited!.LineChanged(edited.Line), "the saved line sank before the reload");
            Check(InFront(edited, "return 2;"), "the saved line is not drawn in the front layer before the reload");
            Check(waited < 60, "the edit was not applied in 60 seconds");
            return;
        }
        Check(value == 2, "Target.Value() is " + value + ", want 2");
        Check(Lider.Reloads >= 1, "lider did not get the reload");
        Check(Game("Count") > countAt, "the game frame count went back to its initial value: " + Game("Count") + " after " + countAt);
        Check(!edited.LineChanged(edited.Line), "the saved line is still floating after the reload");
        Check(!InFront(edited, "return 2;"), "the saved line is still drawn in the front layer after the reload");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Edit()
    {
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(path != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        Check(w!.Files.Contains(path!), "the workspace does not list " + path);
        w.FocusOn(path!);
        var doc = w.Focus;
        Check(doc != null, "cannot open " + path);
        Check(doc!.Find("return 1;"), "no 'return 1;' in " + path);
        // `return ` の後ろへ動き、1 を消して 2 を打ち、Ctrl+S で保存する
        for (var i = 0; i < 7; i++)
            Press(w, Keys.Right);
        Press(w, Keys.Delete);
        Press(w, 31);
        Check(doc.Dirty, "the document is not dirty after typing");
        w.SetMods(Keys.ModCtrl);
        Press(w, Keys.S);
        w.SetMods(0);
        Check(!doc.Dirty, "the document is still dirty after saving");
        Io.LoadText(path!, out var text, out _, out _, out _);
        Check(text != null && text.Contains("return 2;") && !text.Contains("return 1;"), "the saved file was not edited");
        countAt = Game("Count");
        Check(countAt > 0, "cannot read the game frame count");
        edited = doc;
        SaveCrlf(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/crlf.txt");
    }

    // ゲームの static の今の値 (読めなければ -1)
    static int Game(string name)
    {
        foreach (var n in GameApp.Inspect("Game", 0, 100))
            if (n.Name == name && int.TryParse(n.Text, out var value))
                return value;
        return -1;
    }

    static void SaveCrlf(string path)
    {
        Io.SaveText(path, "a\r\nb\r\n");
        var doc = Document.Open(path);
        Check(doc != null, "cannot open " + path);
        doc!.Type("x");
        doc.Save();
        Io.LoadText(path, out var text, out _, out _, out _);
        Check(text == "xa\r\nb\r\n", "saved a CRLF file as " + (text ?? "null").Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    // 窓の大きさに描いた エディタの命令のうち、text を含む行の文字を手前の層に描くか
    // (色分けで行は語ごとの命令に分かれるので、行の番号で命令を探す)
    static bool InFront(Document doc, string text)
    {
        var line = -1;
        for (var l = 0; l < doc.LineCount() && line < 0; l++)
            if (doc.Text(l).Contains(text))
                line = l + 1;
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        for (var i = 0; i < n; i++)
            if (line > 0 && EditorHost.CmdKind(i) == 2 && EditorHost.CmdLine(i) == line)
                return PanelView.Front(doc, EditorHost.CmdLine(i));
        Check(false, "no text command for '" + text + "'");
        return false;
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

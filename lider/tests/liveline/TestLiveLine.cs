// 本文の行と、ゲームが動かしている版 (LiveLines) の行の対応を確かめる。
//   - LineDiff.Match は、足した行、消した行、変えた行があっても、中身が同じ行を前の版の行に対応させる。対応は増える向きで、数は一番多い
//   - 上に行を足しても、下の行は動いている版の同じ行に対応し、変えた行とみなさない。足した行と変えた行だけが変えた行
//   - 窓は実行した行と値を動いている版の行の番号から引くので、行を足しても値はその行についてくる。変えた行と足した行には出さない
//   - reload が当たると、本文の行がそのまま動いている版の行になる
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestLiveLine
{
    static int frames;
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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        frames++;
        if (failed || frames != 3)
            return;
        Lider.SetActive(false);
        CheckMatch();
        CheckDocument();
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void CheckMatch()
    {
        Same(LineDiff.Match(L("a b c"), L("a x b c")), new int[] { 0, -1, 1, 2 }, "an added line");
        Same(LineDiff.Match(L("a b c"), L("a c")), new int[] { 0, 2 }, "a removed line");
        Same(LineDiff.Match(L("a b c"), L("a x c")), new int[] { 0, -1, 2 }, "a changed line");
        Same(LineDiff.Match(L("a b c d e"), L("a x c y e")), new int[] { 0, -1, 2, -1, 4 }, "two changed lines");
        Same(LineDiff.Match(L(""), L("a")), new int[] { -1 }, "an empty before");
        Same(LineDiff.Match(L("a"), L("")), new int[0], "an empty after");

        // 少ない種類の行を並べた版どうしで、対応が正しく、一番多いか
        var seed = 1;
        for (var round = 0; round < 200 && !failed; round++)
        {
            var before = new List<string>();
            var after = new List<string>();
            seed = (seed * 75 + 74) % 65537;
            var n = seed % 12;
            seed = (seed * 75 + 74) % 65537;
            var m = seed % 12;
            for (var i = 0; i < n; i++)
            {
                seed = (seed * 75 + 74) % 65537;
                before.Add("" + seed % 3);
            }
            for (var i = 0; i < m; i++)
            {
                seed = (seed * 75 + 74) % 65537;
                after.Add("" + seed % 3);
            }
            var map = LineDiff.Match(before, after);
            var last = -1;
            var count = 0;
            for (var i = 0; i < m; i++)
            {
                if (map[i] < 0)
                    continue;
                Check(map[i] > last && after[i] == before[map[i]], "round " + round + ": line " + i + " is matched to " + map[i]);
                last = map[i];
                count++;
            }
            Check(count == Lcs(before, after), "round " + round + ": matched " + count + " lines, want " + Lcs(before, after));
        }
    }

    static void CheckDocument()
    {
        var text = new MonoText("test_liveline_mono", (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/yutapon_coding_081d.ttf", 15);
        Document.Boot(text.CellW, text.LineH);
        var doc = Document.Open(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!)!;
        // 動いている版の 8、9 行目 (1 から) が Hp = 1 と Hp = 2
        var log = new TraceLog();
        log.Take();
        log.Hit("Target.cs:8");
        log.Value("Target.cs:8:9:Hp", "1");
        log.Value("Target.cs:9:9:Hp", "2");
        Check(Shown(doc, log, 8) == "1" && Shown(doc, log, 9) == "2", "values before editing are " + Shown(doc, log, 8) + " and " + Shown(doc, log, 9));

        // Step の '{' の下に 1 行足す: Hp = 1 は 9 行目、Hp = 2 は 10 行目になる
        doc.MoveTo(6, Utf8.Bytes(doc.Text(6)));
        doc.Type("\n        var x = 0;");
        EditorHost.Step();
        Check(doc.Text(8) == "        Hp = 1;", "line 9 is " + doc.Text(8));
        Check(doc.LineChanged(7), "the added line 8 is not changed");
        Check(!doc.LineChanged(8) && !doc.LineChanged(9) && !doc.LineChanged(10), "lines below the added line are changed");
        Check(doc.LiveLine(8) == 7 && doc.LiveLine(10) == 9, "line 9 is live line " + doc.LiveLine(8) + ", want 7");
        Check(Shown(doc, log, 8) == "", "the added line 8 shows " + Shown(doc, log, 8));
        Check(Shown(doc, log, 9) == "1" && Shown(doc, log, 10) == "2", "values after adding are " + Shown(doc, log, 9) + " and " + Shown(doc, log, 10));
        Check(PanelView.AgeAt(doc, log, 9) == 0 && PanelView.AgeAt(doc, log, 8) == -1, "the hit line 8 did not follow to line 9");

        // Hp = 2 を書き換える: その行には値を出さない
        doc.MoveTo(9, Utf8.Bytes(doc.Text(9)) - 1);
        doc.Type("0");
        EditorHost.Step();
        Check(doc.Text(9) == "        Hp = 20;", "line 10 is " + doc.Text(9));
        Check(doc.LineChanged(9) && Shown(doc, log, 10) == "", "the changed line 10 shows " + Shown(doc, log, 10));
        Check(Shown(doc, log, 9) == "1", "the untouched line 9 shows " + Shown(doc, log, 9));

        // reload が当たると、本文がそのまま動いている版になる
        doc.Save();
        doc.Reloaded();
        Check(doc.LiveLine(9) == 9 && !doc.LineChanged(7) && !doc.LineChanged(9), "lines are still changed after the reload");
    }

    // 窓の line 行目 (1 から) に出す値 (無ければ "")
    static string Shown(Document doc, TraceLog log, int line)
    {
        var values = PanelView.ValuesAt(doc, log, line);
        return values.Count > 0 ? values[0].Text : "";
    }

    static List<string> L(string s)
    {
        var list = new List<string>();
        foreach (var w in s.Split(' '))
            if (w != "")
                list.Add(w);
        return list;
    }

    static int Lcs(List<string> a, List<string> b)
    {
        var t = new int[(a.Count + 1) * (b.Count + 1)];
        var w = b.Count + 1;
        for (var i = 1; i <= a.Count; i++)
            for (var j = 1; j <= b.Count; j++)
                t[i * w + j] = a[i - 1] == b[j - 1] ? t[(i - 1) * w + j - 1] + 1 : Math.Max(t[(i - 1) * w + j], t[i * w + j - 1]);
        return t[a.Count * w + b.Count];
    }

    static void Same(int[] got, int[] want, string what)
    {
        var ok = got.Length == want.Length;
        for (var i = 0; ok && i < got.Length; i++)
            ok = got[i] == want[i];
        Check(ok, what + ": got [" + string.Join(",", got) + "], want [" + string.Join(",", want) + "]");
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}

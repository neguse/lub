// 開いた file 1 つ。本文、カーソル、編集はエディタの core の view (EditorHost、Id) が持ち、ここは lider の見る所を出す。
// lider が持つのは、ゲームが動かしている版 (LiveLines) と、保存してまだ reload chunk が当たっていない版。
// 保存は エディタの Ctrl+S でも Save でも、Sync が エディタの保存した回数の変化で拾う。
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class Document
{
    public string Path;
    // エディタの view の id
    public readonly int Id;
    // ゲームが動かしている版: 最後に読んだ内容か、保存した後に reload chunk が当たった内容 (奥の層に、変えた行の古い版として出す)
    public List<string> LiveLines;
    // 保存して、まだ reload chunk が当たっていない内容
    List<string>? saved;
    int saves;
    int changeId;
    // 本文か LiveLines が変わるたびに上がる (窓の板と検索はそれで読み直す)
    public int Revision;
    // 保存を拾った回数と、保存した内容がゲームに入った (reload が当たった) 回数
    public int Saved;
    public int Landed;

    Document(string path, int id)
    {
        Path = path;
        Id = id;
        LiveLines = Lines();
        saves = EditorHost.SaveCount(id);
        changeId = EditorHost.ChangeId(id);
    }

    public static bool Booted;

    // エディタの core を起こす (2 度目からは桁の幅と行の高さだけを変える)。
    // かな漢字変換の辞書は env LIDER_JAMO_DICT、無ければ LIDER_HOME の data/mozc.dict を裏で読み始める
    public static void Boot(float cellW, float lineH)
    {
        var err = EditorHost.Init(cellW, lineH);
        if (err != "")
            System.Console.WriteLine("editor: " + err);
        if (Booted)
            return;
        Booted = true;
        var dict = System.Environment.GetEnvironmentVariable("LIDER_JAMO_DICT");
        if (dict == null || dict == "")
            dict = (System.Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/mozc.dict";
        EditorHost.ImeDict(dict);
    }

    // エディタが開いた view (id) の doc。名前の無い doc の path は ""
    public static Document Adopt(string path, int id)
    {
        return new Document(path, id);
    }

    public static Document? Open(string path)
    {
        if (!Booted)
            Boot(8, 16);
        var id = EditorHost.Open(path);
        if (id == 0)
            return null;
        return new Document(path, id);
    }

    public bool Dirty => EditorHost.Dirty(Id);
    // カーソルの行と byte の桁 (どちらも 0 から)
    public int Line => EditorHost.CursorLine(Id) - 1;
    public int Col => EditorHost.CursorCol(Id) - 1;

    public int LineCount()
    {
        return EditorHost.LineCount(Id);
    }

    // line 行目 (0 から、改行を除く)
    public string Text(int line)
    {
        return EditorHost.Line(Id, line + 1);
    }

    public List<string> Lines()
    {
        var lines = new List<string>();
        var n = LineCount();
        for (var i = 0; i < n; i++)
            lines.Add(Text(i));
        return lines;
    }

    // エディタの本文の変化と保存を拾う (key を渡した後と、毎 frame)
    public void Sync()
    {
        var id = EditorHost.ChangeId(Id);
        if (id != changeId)
        {
            changeId = id;
            Revision++;
        }
        var n = EditorHost.SaveCount(Id);
        if (n != saves)
        {
            saves = n;
            saved = Lines();
            Saved++;
            Revision++;
        }
    }

    public void Save()
    {
        EditorHost.Save(Id);
        Sync();
    }

    // lub が reload chunk を当てた (OnReload)。
    // lub は変換や reload の失敗を知らせないので、保存した後の最初の OnReload で保存した内容がゲームに入ったとみなす
    public void Reloaded()
    {
        Sync();
        if (saved == null)
            return;
        LiveLines = saved;
        saved = null;
        Landed++;
        Revision++;
    }

    // 本文の行 → LiveLines の行 (変えた行と足した行は -1)。本文か LiveLines が変わったら作り直す
    int[] live = new int[0];
    bool differs;
    // インラインの形で挟む、動いている版の対応しない行 (0 から) と、それを上に挟む本文の行 (1 から、行の数 + 1 は最後の行の下)。
    // gaps[line] は line 行目の上に挟む行の数
    readonly List<int> inlineOld = new List<int>();
    readonly List<int> inlineAt = new List<int>();
    int[] gaps = new int[0];
    int liveChange = -1;
    int liveLanded = -1;

    void MatchLive()
    {
        var change = EditorHost.ChangeId(Id);
        if (change == liveChange && Landed == liveLanded)
            return;
        live = LineDiff.Match(LiveLines, Lines());
        liveChange = change;
        liveLanded = Landed;
        // 対応は増える向きなので、行の数が同じで全部が対応すれば同じ内容
        differs = live.Length != LiveLines.Count;
        foreach (var l in live)
            if (l < 0)
                differs = true;
        // 対応する行の間の、動いている版の対応しない行を、次に対応する行 (無ければ最後の行の下) の側の塊の上に挟む
        inlineOld.Clear();
        inlineAt.Clear();
        gaps = new int[live.Length + 2];
        var prevNew = -1;
        var prevOld = -1;
        for (var line = 0; line <= live.Length; line++)
        {
            var o = line < live.Length ? live[line] : LiveLines.Count;
            if (o < 0)
                continue;
            for (var k = prevOld + 1; k < o; k++)
            {
                inlineOld.Add(k);
                inlineAt.Add(prevNew + 2);
                gaps[prevNew + 2]++;
            }
            prevNew = line;
            prevOld = o;
        }
    }

    // line 行目 (0 から) の、ゲームが動かしている版での行 (0 から、変えた行と足した行は -1)。
    // 行を足したり消したりしても、中身が同じ行は動いている版の同じ行に対応させる
    public int LiveLine(int line)
    {
        MatchLive();
        return line >= 0 && line < live.Length ? live[line] : -1;
    }

    // 本文の全部の行の LiveLine
    public int[] LiveMap()
    {
        MatchLive();
        return live;
    }

    // インラインの形で line 行目 (1 から) の上に挟む行の数 (EditorHost.SetGaps に渡す)
    public int[] InlineGaps()
    {
        MatchLive();
        return gaps;
    }

    // インラインの形で挟む、動いている版の行 (0 から、上から順) と、それぞれを上に挟む本文の行 (1 から)
    public List<int> InlineOld()
    {
        MatchLive();
        return inlineOld;
    }

    public List<int> InlineAt()
    {
        MatchLive();
        return inlineAt;
    }

    // 本文がゲームの動いている版と違うか (変えた行、足した行、消した行がある)
    public bool Differs()
    {
        MatchLive();
        return differs;
    }

    // ゲームが動かしている版と違う行か (変えた行と足した行)
    public bool LineChanged(int line)
    {
        return LiveLine(line) < 0;
    }

    // カーソルの所に打つ (改行を含んでよい)
    public void Type(string s)
    {
        EditorHost.Insert(Id, s);
        Sync();
    }

    public void MoveTo(int line, int col)
    {
        EditorHost.SetCursor(Id, line + 1, col + 1);
    }

    // 最初に現れる text の位置へ動く (無ければ false)
    public bool Find(string text)
    {
        var n = LineCount();
        for (var i = 0; i < n; i++)
        {
            var at = Utf8.IndexOf(Text(i), text);
            if (at >= 0)
            {
                MoveTo(i, at);
                return true;
            }
        }
        return false;
    }
}

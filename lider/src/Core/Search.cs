// file と宣言を 1 つの欄で引く、インクリメンタルなあいまい検索。
// 上下で候補を選ぶと、その場所を窓に出す (窓が無ければ仮に開く)。Return で確定し、Escape で開く前に戻す。
// 確定すると、開いたときにいた場所を Workspace の履歴に残す (Ctrl+- で戻る)。
using TinySystem.Text;
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class SearchItem
{
    // 引く文字 (file は Root からの path、宣言は Outer.Member)
    public string Text = "";
    public string Path = "";
    // 宣言の行 (0 から)。file は -1
    public int Line = -1;
    // 右に出す場所 (宣言の file と行)
    public string Where = "";
    // 確定すると、この file を作る
    public bool Create;
    // 画像か音の file (窓でなく板に出す)
    public bool Asset;
    // 保存していない編集のある file
    public bool Dirty;
}

// file 1 つの宣言と、読んだ時の版
class ScannedFile
{
    public int Version = -1;
    public int Revision = -1;
    // 打っている間に変わった版と、それを見つけた時刻
    public int ChangedRevision = -1;
    public float ChangedAt;
    public List<SearchItem> Items = new List<SearchItem>();
    public List<Declaration> Declarations = new List<Declaration>();
}

public class Search
{
    public const int MaxResults = 10;

    public bool Shown;
    public string Query = "";
    public int Selected;
    public readonly List<SearchItem> Results = new List<SearchItem>();
    readonly Dictionary<string, ScannedFile> scanned = new Dictionary<string, ScannedFile>();
    // 検索の前に focus があったタブと、選んで仮に開いたタブ
    Document? origin;
    Document? opened;
    // 選んだ候補を出す前の、その file のカーソルと先頭の行
    Document? moved;
    int movedLine;
    int movedCol;
    int movedFirst;
    bool dirty;
    // 打ち終わるのを待たずに読み直すとき
    bool forced;
    const float QuietSeconds = 0.5f;
    // 検索を開いたときにいた場所
    Place? from;
    // まだ読み終えていない file があるか
    bool pending;
    // 定義へ飛ぶ語 (読み終えて宣言が 1 つに決まれば、そのまま飛ぶ)
    string? jumpWord;

    // カーソルの語の宣言へ飛ぶ。1 つに決まらなければ、その語を入れた検索を開いたままにする
    public void Jump(Workspace w)
    {
        var doc = w.Focus;
        if (doc == null)
            return;
        var word = WordAt(doc.Text(doc.Line), doc.Col);
        Open(w, word);
        if (word != "")
            jumpWord = word;
        Refresh(w);
    }

    // line の col の所 (か、そのすぐ左) の語
    public static string WordAt(string line, int col)
    {
        var start = Math.Min(col, Utf8.Bytes(line));
        if (start == Utf8.Bytes(line) || !IsWordChar(Utf8.At(line, start)))
            start--;
        if (start < 0 || !IsWordChar(Utf8.At(line, start)))
            return "";
        var end = start;
        while (start > 0 && IsWordChar(Utf8.At(line, start - 1)))
            start--;
        while (end + 1 < Utf8.Bytes(line) && IsWordChar(Utf8.At(line, end + 1)))
            end++;
        return Utf8.Sub(line, start, end - start + 1);
    }

    static bool IsWordChar(int c)
    {
        return char.IsLetterOrDigit((char)c) || c == '_';
    }

    // path の file の宣言 (読んでいなければ空)
    public List<Declaration> DeclarationsIn(string path)
    {
        return scanned.TryGetValue(path, out var file) ? file.Declarations : new List<Declaration>();
    }

    // path の line 行目 (1 から) を含むメソッドの名前 (読んでいないか、メソッドの外なら "")
    public string MethodAt(string path, int line)
    {
        var name = "";
        foreach (var d in DeclarationsIn(path))
            if (d.IsCode && d.Line + 1 <= line)
                name = d.Name;
        return name;
    }

    // 名前の宣言 (Outer.Inner の Inner でも引ける)。毎 frame 呼ばれるので、宣言ごとに文字列を作らない
    public SearchItem? Find(string name)
    {
        var n = Utf8.Bytes(name);
        // Values は値の List を作るので、辞書をそのまま回す
        foreach (var pair in scanned)
            foreach (var item in pair.Value.Items)
            {
                if (item.Text == name)
                    return item;
                var t = Utf8.Bytes(item.Text);
                if (t > n && Utf8.At(item.Text, t - n - 1) == '.' && item.Text.EndsWith(name))
                    return item;
            }
        return null;
    }

    // path の line 行目 (0 から。-1 は file を開くだけ) へ飛ぶ
    public void GoToPlace(Workspace w, string path, int line)
    {
        origin = w.Focus;
        from = w.Here();
        Preview(w, new SearchItem { Path = path, Line = line });
        Finish(w);
    }

    // 名前の宣言へ飛ぶ (無ければ何もしない)
    public void GoTo(Workspace w, string name)
    {
        forced = true;
        Refresh(w);
        forced = false;
        var item = Find(name);
        if (item == null)
            return;
        origin = w.Focus;
        from = w.Here();
        Preview(w, item);
        Finish(w);
    }

    public void Open(Workspace w, string query = "")
    {
        Shown = true;
        Query = query;
        jumpWord = null;
        from = w.Here();
        Selected = 0;
        origin = w.Focus;
        opened = null;
        moved = null;
        dirty = true;
        Refresh(w);
    }

    // 出している間、毎 frame 呼ぶ (読み終えた file と変わった file の宣言を拾う)
    public void Refresh(Workspace w)
    {
        pending = false;
        foreach (var path in w.Files)
            dirty = Scan(w, path) || dirty;
        if (dirty)
        {
            dirty = false;
            Filter(w);
        }
        if (jumpWord != null && !pending)
            TryJump(w, jumpWord);
    }

    // word がちょうど名前の宣言が 1 つなら、そこへ飛ぶ。無いか幾つもあれば、検索を開いたままにする
    void TryJump(Workspace w, string word)
    {
        jumpWord = null;
        SearchItem? found = null;
        foreach (var path in w.Files)
            if (scanned.TryGetValue(path, out var file))
                foreach (var item in file.Items)
                    if (item.Text == word || item.Text.EndsWith("." + word))
                    {
                        if (found != null)
                            return;
                        found = item;
                    }
        if (found == null)
            return;
        Preview(w, found);
        Finish(w);
    }

    bool Scan(Workspace w, string path)
    {
        if (!scanned.TryGetValue(path, out var file))
        {
            file = new ScannedFile();
            scanned[path] = file;
        }
        List<string> lines;
        if (w.Docs.TryGetValue(path, out var doc))
        {
            if (file.Revision == doc.Revision)
                return false;
            // 検索を出していない間は、打ち終わるまで読み直さない (初めて読むときは待たない)
            if (file.ChangedRevision != doc.Revision)
            {
                file.ChangedRevision = doc.Revision;
                file.ChangedAt = Sys.Seconds();
            }
            if (!Shown && !forced && file.Revision >= 0 && Sys.Seconds() - file.ChangedAt < QuietSeconds)
                return false;
            file.Revision = doc.Revision;
            lines = doc.Lines();
        }
        else
        {
            Io.LoadText(path, out var text, out var version, out var status, out _);
            if (status == Io.Status.Pending)
                pending = true;
            if (status != Io.Status.Ready || text == null || version == file.Version)
                return false;
            file.Version = version;
            lines = TextBuffer.Lines(text);
        }
        file.Items.Clear();
        file.Declarations = Declarations.Scan(lines);
        foreach (var d in file.Declarations)
            file.Items.Add(new SearchItem { Text = d.Name, Path = path, Line = d.Line, Where = w.Relative(path) + ":" + (d.Line + 1) });
        return true;
    }

    // 点の高い順に MaxResults まで残す。字が空なら file だけを並べる
    void Filter(Workspace w)
    {
        var scores = new List<int>();
        Results.Clear();
        foreach (var path in w.Files)
            Rank(new SearchItem { Text = w.Relative(path), Path = path, Dirty = w.Docs.TryGetValue(path, out var doc) && doc.Dirty }, scores);
        foreach (var path in w.AssetFiles)
            Rank(new SearchItem { Text = w.Relative(path), Path = path, Asset = true }, scores);
        if (Query.Trim() != "")
            foreach (var path in w.Files)
                if (scanned.TryGetValue(path, out var file))
                    foreach (var item in file.Items)
                        Rank(item, scores);
        OfferCreate(w);
        Selected = Math.Min(Selected, Math.Max(0, Results.Count - 1));
    }

    // 打った字が新しい file の名前になるなら、作る候補を最後に足す
    void OfferCreate(Workspace w)
    {
        var name = Query.Trim();
        if (name.EndsWith(".cs"))
            name = Utf8.Sub(name, 0, Utf8.Bytes(name) - 3);
        if (!IsTypeName(name))
            return;
        var path = w.PathOf(name + ".cs");
        if (w.Files.Contains(path))
            return;
        if (Results.Count == MaxResults)
            Results.RemoveAt(MaxResults - 1);
        Results.Add(new SearchItem { Text = name + ".cs", Path = path, Where = "新しく作る", Create = true });
    }

    static bool IsTypeName(string name)
    {
        if (name == "" || char.IsDigit((char)Utf8.At(name, 0)))
            return false;
        var u = new Utf8Bytes(name);
        for (var i = 0; i < u.Length; i++)
            if (u[i] >= 128 || !IsWordChar(u[i]))
                return false;
        return true;
    }

    void Rank(SearchItem item, List<int> scores)
    {
        var score = EditorHost.Fuzzy(item.Text, Query);
        if (score == EditorHost.NoMatch)
            return;
        var at = Results.Count;
        while (at > 0 && scores[at - 1] < score)
            at--;
        if (at >= MaxResults)
            return;
        if (Results.Count == MaxResults)
        {
            Results.RemoveAt(MaxResults - 1);
            scores.RemoveAt(MaxResults - 1);
        }
        Results.Add(item);
        scores.Add(score);
        for (var i = Results.Count - 1; i > at; i--)
        {
            Results[i] = Results[i - 1];
            scores[i] = scores[i - 1];
        }
        Results[at] = item;
        scores[at] = score;
    }

    public void Type(Workspace w, string text)
    {
        Query += text;
        Selected = 0;
        Filter(w);
    }

    public void Backspace(Workspace w)
    {
        if (Query == "")
            return;
        // UTF-8 の 1 文字を消す必要は無い (打てるのは ASCII だけ)
        Query = Utf8.Sub(Query, 0, Utf8.Bytes(Query) - 1);
        Selected = 0;
        Filter(w);
    }

    // 候補を 1 つ上か下へ選び、その場所を出す
    public void Move(Workspace w, int by)
    {
        if (Results.Count == 0)
            return;
        Selected = (Selected + by + Results.Count) % Results.Count;
        Preview(w, Results[Selected]);
    }

    // 選んでいる候補へ移って閉じる
    public void Commit(Workspace w)
    {
        if (Results.Count == 0)
        {
            Cancel(w);
            return;
        }
        var item = Results[Selected];
        if (item.Create)
        {
            Cancel(w);
            w.Create(item.Path, Utf8.Sub(item.Text, 0, Utf8.Bytes(item.Text) - 3));
            return;
        }
        Preview(w, item);
        Finish(w);
    }

    void Finish(Workspace w)
    {
        w.Remember(from);
        w.Jumps++;
        moved = null;
        opened = null;
        Shown = false;
    }

    public void Cancel(Workspace w)
    {
        Undo(w);
        w.ShownAsset = "";
        Restore(w);
        moved = null;
        opened = null;
        Shown = false;
    }

    // 開いたときのタブに focus を戻す (もう閉じていれば、今のまま)
    void Restore(Workspace w)
    {
        if (origin != null && w.WindowOf(origin) != null)
            w.Show(origin);
    }

    void Preview(Workspace w, SearchItem item)
    {
        Undo(w);
        w.ShownAsset = item.Asset ? item.Path : "";
        if (item.Create || item.Asset)
            return;
        var had = w.Docs.TryGetValue(item.Path, out var open) && w.WindowOf(open) != null;
        w.FocusOn(item.Path);
        var doc = w.Focus;
        if (doc == null)
            return;
        opened = had ? null : doc;
        if (item.Line < 0)
            return;
        moved = doc;
        movedLine = doc.Line;
        movedCol = doc.Col;
        movedFirst = EditorHost.FirstLine(doc.Id);
        doc.MoveTo(item.Line, 0);
        EditorHost.ScrollToLine(doc.Id, Math.Max(1, item.Line + 1 - Workspace.ContextLines));
    }

    void Undo(Workspace w)
    {
        if (moved != null)
        {
            moved.MoveTo(movedLine, movedCol);
            EditorHost.ScrollToLine(moved.Id, Math.Max(1, movedFirst));
            moved = null;
        }
        if (opened != null)
        {
            var window = w.WindowOf(opened);
            if (window != null)
                w.CloseTab(window, window.Code.Tabs.IndexOf(opened));
            opened = null;
            Restore(w);
        }
    }
}

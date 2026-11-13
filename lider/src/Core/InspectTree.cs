// インスペクタの行: ゲームの今の値 (lub の Sys.Inspect) と、最近実行したメソッド (TraceLog) を、開いた節だけ辿って並べる。
// class の下には、そのソースの宣言の順に、メソッド (実行の印) と static のフィールド (値) を並べる。instance の下はフィールドを宣言の順に並べる。
// 要素が同じ class の instance だけの並び (List と配列) は表にし、1 要素を 1 行、field を列にする。
// 要素が ChunkSize を超える並びは、ChunkSize ずつの節に区切る。
// 値は Refresh ごとに引き直し、しばらく (SteadyFrames) 変わらなかった値が変わると、その後 SteadyFrames の間 Glow が立つ。
// 絞り込み: 全体 / class / 個体 / その下の節。絞り込んだ所の子だけを並べ、パンくず (Crumbs) で上の段に戻る。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class InspectRow
{
    // 開閉の key (節の Path、区切りは並びの Path + "#" + 区切りの番号、メソッドは "class.method()")
    public string Key = "";
    public int Depth;
    public string Name = "";
    // instance の個体の Id (それ以外は "")
    public string Id = "";
    // Id の個体を選んでいるか (InspectView が決める)
    public bool Selected;
    // 名前と、個体の目印
    public string Label => Id == "" ? Name : Name + " " + FieldValues.Tag(Id);
    public string Text = "";
    // 飛ぶ先の宣言の名前 (class と instance は class、field とメソッドは "class.member"。無ければ "")
    public string Target = "";
    // Target の class の file が実行中か (InspectView が決める)
    public bool Running;
    public bool Openable;
    public bool Open;
    // 祖先と同じ table (開けない)
    public bool Cycle;
    // 表の見出しなら列の名前、表の行なら列ごとの値 (無い field は "")。どちらでもなければ null
    public List<string>? Cells;
    public bool Header;
    // メソッドの行 (Glow は最近通った強さ)
    public bool Method;
    // ≫ で絞り込める行 (class、instance、子のある節)
    public bool Scopable;
    // Text と Cells の値が変わって光る強さ、メソッドなら最近通った強さ (0 から 1)
    public float Glow;
    public List<float>? CellGlow;
    // 描いた値 (Text か、表の行の列ごとの値。幅に収まらなかった物は … で終わる。InspectView が決める)
    public readonly List<string> Shown = new List<string>();
}

public class InspectTree
{
    public const int ChunkSize = 100;
    public const int MaxColumns = 8;
    // 絞り込んだ個体を弱く覚える key (Sys.InspectPin。TraceLog.Take もその個体の分の行を引くのに使う)
    public const string ScopePin = "lider_scope";

    public readonly List<InspectRow> Rows = new List<InspectRow>();
    readonly Dictionary<string, bool> opened = new Dictionary<string, bool>();
    readonly ValueFlash flash = new ValueFlash();
    Workspace? work;
    Search? search;

    // 絞り込み: class (ScopeClass)、個体 (ScopeBase がその Path、ScopeId が Id)、その下の節 (ScopePath)。全体はどれも ""
    public string ScopeClass = "";
    public string ScopeBase = "";
    public string ScopeId = "";
    public string ScopePath = "";

    public bool IsOpen(string key)
    {
        return opened.ContainsKey(key);
    }

    public void Toggle(string key)
    {
        if (!opened.Remove(key))
            opened[key] = true;
    }

    // パンくずの名前 (先頭は全体)
    public List<string> Crumbs()
    {
        var crumbs = new List<string> { "全体" };
        if (ScopeClass == "")
            return crumbs;
        crumbs.Add(ScopeClass);
        if (ScopeBase != "")
            crumbs.Add(FieldValues.Tag(ScopeId));
        var under = ScopeBase != "" ? ScopeBase : ScopeClass;
        if (ScopePath != "" && ScopePath != under)
            crumbs.Add(ScopePath.StartsWith(under + ".") ? Utf8.Sub(ScopePath, Utf8.Bytes(under) + 1) : ScopePath);
        return crumbs;
    }

    // パンくずの i 番目の段に戻る
    public void ScopeTo(int i)
    {
        if (i <= 0)
            ScopeClass = "";
        if (i <= 1)
        {
            ScopeBase = "";
            ScopeId = "";
            ScopePath = "";
            GameApp.InspectPin(ScopePin, "");
        }
        else if (i == 2 && ScopeBase != "")
            ScopePath = ScopeBase;
    }

    // 行に絞り込む (class の根は class に、instance は個体に、それ以外の節はその節に)
    public void Enter(InspectRow row)
    {
        if (!row.Scopable)
            return;
        if (row.Id != "")
        {
            EnterInstance(row.Target, row.Id, row.Key);
            return;
        }
        if (row.Depth == 0 && ScopeClass == "")
        {
            ScopeTo(1);
            ScopeClass = row.Name;
            return;
        }
        if (ScopeClass == "")
            ScopeClass = Root(row.Key);
        ScopePath = row.Key;
    }

    // path の個体 (class cls、Id id) に絞り込む
    public void EnterInstance(string cls, string id, string path)
    {
        ScopeClass = cls;
        ScopeBase = path;
        ScopePath = path;
        ScopeId = id;
        GameApp.InspectPin(ScopePin, path);
    }

    // 絞り込んだ個体の path が変わった (並びの要素が増減した) ときに、その下の節ごと移す
    public void Rebase(string path)
    {
        if (ScopeBase == "" || path == ScopeBase)
            return;
        ScopePath = path + Utf8.Sub(ScopePath, Utf8.Bytes(ScopeBase));
        ScopeBase = path;
    }

    // 根から開いた節を辿って Rows を作り直す。
    // w と search があれば class の下にメソッドを並べ、フィールドを宣言の順にする (無ければ Sys.Inspect の順)
    public void Refresh(Workspace? w = null, Search? s = null)
    {
        work = w;
        search = s;
        flash.Tick();
        Rows.Clear();
        // 絞り込んだ個体が回収されるか辿れなくなったら、class に戻す (別の個体へ移らない)
        if (ScopeId != "" && !GameApp.InspectPinned(ScopePin, ScopeId))
            ScopeTo(1);
        if (ScopePath != "")
        {
            // 個体の下には、その個体で通ったメソッドも並べる (TraceLog はその個体の分の行だけを持つ)
            if (ScopePath == ScopeBase)
                AddMembers(ScopeClass, ScopePath, 0, true);
            else
                AddRange(ScopePath, 0, 1000, 0, ScopeClass);
            return;
        }
        foreach (var n in GameApp.Inspect("", 0, 1000))
        {
            if (ScopeClass == "")
                AddNode(n, 0, "");
            else if (n.Name == ScopeClass)
                AddMembers(n.Name, n.Path, 0, true);
        }
    }

    // 持ち主の class (owner) の中の節 n を行にする
    void AddNode(InspectNode n, int depth, string owner)
    {
        var root = depth == 0 && ScopeClass == "";
        var target = root ? n.Name : n.Type != "" ? n.Type : owner != "" && !n.Name.StartsWith("[") ? owner + "." + n.Name : "";
        var row = new InspectRow
        {
            Key = n.Path,
            Depth = depth,
            Name = n.Name,
            Id = n.Type != "" ? n.Id : "",
            Target = target,
            // 根 (class) の値は class table を短くしたもので、読む所が無い
            Text = root ? "" : n.Text,
            Openable = n.Count > 0 || root,
            Open = (n.Count > 0 || root) && IsOpen(n.Path),
            Cycle = n.Cycle,
            Scopable = !n.Cycle && (n.Count > 0 || root),
            Glow = flash.Track(n.Path, n.Text),
        };
        Rows.Add(row);
        if (!row.Open)
            return;
        if (root)
        {
            AddMembers(n.Name, n.Path, depth + 1, true);
            return;
        }
        if (n.Type != "")
        {
            AddMembers(n.Type, n.Path, depth + 1, false);
            return;
        }
        if (n.Count <= ChunkSize)
        {
            AddRange(n.Path, 0, n.Count, depth + 1, owner);
            return;
        }
        for (var at = 0; at < n.Count; at += ChunkSize)
        {
            var len = Math.Min(ChunkSize, n.Count - at);
            var key = n.Path + "#" + at / ChunkSize;
            var chunk = new InspectRow
            {
                Key = key,
                Depth = depth + 1,
                Name = "[" + at + "…" + (at + len - 1) + "]",
                Text = len + " 個",
                Openable = true,
                Open = IsOpen(key),
            };
            Rows.Add(chunk);
            if (chunk.Open)
                AddRange(n.Path, at, len, depth + 2, owner);
        }
    }

    // class cls の path の節の子を、宣言の順に並べる。methods なら class のメソッドも並べる (実行の印)。
    // 宣言の見つからないフィールドは後ろに続ける
    void AddMembers(string cls, string path, int depth, bool methods)
    {
        var fields = GameApp.Inspect(path, 0, 1000);
        var used = new Dictionary<string, bool>();
        foreach (var m in Members(cls))
        {
            var name = Utf8.Sub(m.Decl.Name, Utf8.Bytes(cls) + 1);
            if (m.Decl.IsCode)
            {
                if (methods)
                    Rows.Add(new InspectRow
                    {
                        Key = cls + "." + name + "()",
                        Depth = depth,
                        Name = name + "()",
                        Target = m.Decl.Name,
                        Method = true,
                        Glow = m.Glow,
                    });
                continue;
            }
            foreach (var f in fields)
                if (f.Name == name && !used.ContainsKey(name))
                {
                    used[name] = true;
                    AddNode(f, depth, cls);
                }
        }
        foreach (var f in fields)
            if (!used.ContainsKey(f.Name))
                AddNode(f, depth, cls);
    }

    // class cls の直下の宣言 (メソッドとフィールド。型は除く) を、ソースの順に。
    // メソッドには最近通った強さを付ける
    List<Member> Members(string cls)
    {
        var members = new List<Member>();
        if (work == null || search == null)
            return members;
        foreach (var path in work.Files)
        {
            var decls = search.DeclarationsIn(path);
            List<float>? glows = null;
            for (var i = 0; i < decls.Count; i++)
            {
                var d = decls[i];
                if (d.IsType || !d.Name.StartsWith(cls + ".") || Utf8.IndexOf(d.Name, ".", Utf8.Bytes(cls) + 1) >= 0)
                    continue;
                if (glows == null)
                    glows = MethodGlows(work, path, decls);
                members.Add(new Member { Decl = d, Glow = glows[i] });
            }
        }
        return members;
    }

    // path の宣言ごとの、最近通った強さ (メソッドだけ。それ以外は 0)。
    // 実行した行は、その行より前で一番近いメソッドに数える (ローカル関数とラムダの中の行は外側のメソッドに数える)
    public static List<float> MethodGlows(Workspace w, string path, List<Declaration> decls)
    {
        var ages = new List<int>();
        foreach (var d in decls)
            ages.Add(TraceLog.Fade);
        var lines = w.Trace.LinesOf(path);
        if (lines != null)
            foreach (var pair in lines)
            {
                var at = -1;
                for (var i = 0; i < decls.Count; i++)
                    if (decls[i].IsCode && decls[i].Line + 1 <= pair.Key)
                        at = i;
                if (at >= 0)
                    ages[at] = Math.Min(ages[at], w.Trace.Frame - pair.Value);
            }
        var glows = new List<float>();
        foreach (var age in ages)
            glows.Add(age >= TraceLog.Fade ? 0 : TraceLog.Glow(age));
        return glows;
    }

    // path の子のうち offset から count 個を、表か木の行にして足す
    void AddRange(string path, int offset, int count, int depth, string owner)
    {
        var children = GameApp.Inspect(path, offset, count);
        if (!SameClass(children))
        {
            foreach (var c in children)
                AddNode(c, depth, owner);
            return;
        }
        var columns = new List<string>();
        foreach (var f in GameApp.Inspect(children[0].Path, 0, MaxColumns))
            columns.Add(f.Name);
        Rows.Add(new InspectRow { Key = path + "#columns", Depth = depth, Name = children[0].Type, Cells = columns, Header = true });
        foreach (var c in children)
        {
            var cells = new List<string>();
            var glow = new List<float>();
            var fields = GameApp.Inspect(c.Path, 0, MaxColumns);
            foreach (var col in columns)
            {
                var text = "";
                foreach (var f in fields)
                    if (f.Name == col)
                        text = f.Text;
                cells.Add(text);
                glow.Add(flash.Track(c.Path + "." + col, text));
            }
            var row = new InspectRow
            {
                Key = c.Path,
                Depth = depth,
                Name = c.Name,
                Id = c.Id,
                Target = c.Type,
                Text = c.Text,
                Openable = c.Count > 0,
                Open = c.Count > 0 && IsOpen(c.Path),
                Cycle = c.Cycle,
                Scopable = !c.Cycle && c.Count > 0,
                Cells = cells,
                CellGlow = glow,
            };
            Rows.Add(row);
            if (row.Open)
                AddMembers(c.Type, c.Path, depth + 1, false);
        }
    }

    // 要素の並びで、どの要素も同じ class の instance なら true
    static bool SameClass(List<InspectNode> children)
    {
        if (children.Count < 2 || children[0].Type == "")
            return false;
        foreach (var c in children)
            if (c.Type != children[0].Type || !c.Name.StartsWith("["))
                return false;
        return true;
    }

    // path の根の class の名前 ("Shelf.Crates[0]" なら "Shelf")
    static string Root(string path)
    {
        var dot = Utf8.IndexOf(path, ".");
        return dot < 0 ? path : Utf8.Sub(path, 0, dot);
    }
}

// class の直下の宣言と、メソッドなら最近通った強さ
class Member
{
    public Declaration Decl = new Declaration();
    public float Glow;
}

// フィールドの宣言の行に出す今の値を、ゲームの今の値 (lub の Sys.Inspect) から引く。
// static はその値。instance は static から辿って見つけ、1 つならその値、幾つもあれば選んだ個体の値 (選ぶまでは出さない)。
// 幾つもあれば、class の宣言の行に個体の目印を MaxMarks 個まで並べる。押すと選ぶ。
// 個体は table のアドレス (InspectNode.Id) で見分ける。並びの順番は要素の増減で変わるので、個体の目印にしない。
// 選んだ個体は lub に弱く覚えさせ、回収されるか static から辿れなくなったら選択を解く (別の個体へは移らない)。
using TinySystem.Text;
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

// class の宣言の行の、押せる印 1 つ
public class FieldMark
{
    public string Class = "";
    // 個体の Id。"" は続きを出す印
    public string Id = "";
    // 出す文字 ("#a3f2"、"… 全 9 個")
    public string Text = "";
    public bool Selected;
    // 窓の中で描いた所 (PanelView が描くときに決める)
    public float X0;
    public float X1;
}

public class FieldValue
{
    // 行 (1 から)
    public int Line;
    // 値 (class の行は "")
    public string Text = "";
    // 変わって光る強さ (0 から 1)
    public float Glow;
    // 数なら直近の frame の値 (古い順。数でなければ null)
    public List<float>? History;
    // フィールドの class か、宣言の class
    public string Class = "";
    // class の行の印 (幾つもある個体の目印と、続きを出す印)
    public readonly List<FieldMark> Marks = new List<FieldMark>();
}

// static から辿って見つけた個体
public class FoundInstance
{
    public string Class = "";
    public string Path = "";
    public string Id = "";
}

public class FieldValues
{
    // instance を探し直す間隔と、辿る深さと 1 節の子の数の上限
    const int DiscoverFrames = 15;
    const int MaxDepth = 8;
    const int MaxChildren = 1000;
    // class の行に並べる個体の数
    const int MaxMarks = 4;

    // class → 見つけた個体 (見つけた順)
    Dictionary<string, List<FoundInstance>> instances = new Dictionary<string, List<FoundInstance>>();
    // class → 選んだ個体の Id
    readonly Dictionary<string, string> selected = new Dictionary<string, string>();
    // class → 並べている先頭の個体の番号
    readonly Dictionary<string, int> pages = new Dictionary<string, int>();
    readonly ValueFlash flash = new ValueFlash();
    int frame;

    // 毎 frame 呼ぶ
    public void Refresh()
    {
        flash.Tick();
        if (frame++ % DiscoverFrames == 0)
        {
            Discover();
            Forget();
        }
    }

    // 数の値の、直近の frame の列 (ゲームが進んだ frame ごとに 1 つ。出している間だけ足す)
    class History
    {
        public List<float> Values = new List<float>();
        // 最後に足した frame (GameApp.Frames)
        public int Frame = -1;
    }

    readonly Dictionary<string, History> histories = new Dictionary<string, History>();

    // key の値の列に今の値を足して返す (数でなければ null)
    List<float>? Sample(string key, string text)
    {
        if (!float.TryParse(text, out var v))
            return null;
        if (!histories.TryGetValue(key, out var h))
        {
            h = new History();
            histories[key] = h;
        }
        if (h.Frame != GameApp.Frames)
        {
            h.Frame = GameApp.Frames;
            h.Values.Add(v);
            if (h.Values.Count > TraceLog.Fade)
                h.Values.RemoveAt(0);
        }
        return h.Values;
    }

    // しばらく出していない値の列を捨てる
    void Forget()
    {
        var gone = new List<string>();
        foreach (var pair in histories)
            if (GameApp.Frames - pair.Value.Frame > TraceLog.Fade)
                gone.Add(pair.Key);
        foreach (var key in gone)
            histories.Remove(key);
    }

    // static から辿って個体を探し (同じ個体は最初に見つけた path で 1 度)、回収されたか辿れなくなった個体の選択を解く
    void Discover()
    {
        instances = new Dictionary<string, List<FoundInstance>>();
        foreach (var n in GameApp.InspectInstances(MaxDepth, MaxChildren))
        {
            if (!instances.TryGetValue(n.Type, out var list))
            {
                list = new List<FoundInstance>();
                instances[n.Type] = list;
            }
            list.Add(new FoundInstance { Path = n.Path, Id = n.Id });
        }
        var gone = new List<string>();
        foreach (var pair in selected)
            if (!GameApp.InspectPinned(pair.Key, pair.Value) || !instances.TryGetValue(pair.Key, out var found) || IndexOf(found, pair.Value) < 0)
                gone.Add(pair.Key);
        foreach (var cls in gone)
        {
            selected.Remove(cls);
            GameApp.InspectPin(cls, "");
        }
    }

    static int IndexOf(List<FoundInstance> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return i;
        return -1;
    }

    // 宣言のうち first から last 行 (1 から) にあるフィールドの今の値
    public List<FieldValue> For(List<Declaration> declarations, int first, int last)
    {
        var shown = new List<FieldValue>();
        var read = new Dictionary<string, List<InspectNode>>();
        foreach (var d in declarations)
        {
            if (d.Const || d.Line + 1 < first || d.Line + 1 > last)
                continue;
            var parts = PartsOf(d.Name);
            if (d.IsType)
            {
                var type = parts.Last;
                if (instances.TryGetValue(type, out var all) && all.Count > 1)
                    shown.Add(ClassLine(d.Line + 1, type, all));
                continue;
            }
            if (parts.Class == "")
                continue;
            var cls = parts.Class;
            var field = parts.Last;
            var value = new FieldValue { Line = d.Line + 1, Class = cls };
            var node = Child(read, cls, field);
            if (node != null)
            {
                value.Text = node.Text;
                value.Glow = flash.Track(node.Path, node.Text);
                value.History = Sample(node.Path, node.Text);
                shown.Add(value);
                continue;
            }
            if (!instances.TryGetValue(cls, out var list) || list.Count == 0 || !IsField(read, list[0].Path, field))
                continue;
            var one = list.Count == 1 ? list[0] : null;
            if (one == null && selected.TryGetValue(cls, out var id) && IndexOf(list, id) >= 0)
                one = list[IndexOf(list, id)];
            if (one == null)
                continue;
            // 探し直すまでの間に並びがずれても、別の個体を読まない
            var path = Fresh(read, one);
            node = path != "" ? Child(read, path, field) : null;
            if (node == null)
                continue;
            value.Text = node.Text;
            // 並びがずれて path が変わっても光らないよう、個体で覚える
            value.Glow = flash.Track(one.Id + "." + field, node.Text);
            // 並びがずれても同じ個体を追うよう、個体で覚える
            value.History = Sample(one.Id + "." + field, node.Text);
            shown.Add(value);
        }
        return shown;
    }

    // 宣言の名前の、最後の段と、その前の段 (class。段が 1 つなら "")。毎 frame 切り出さないよう名前ごとに覚える
    class NameParts
    {
        public string Class = "";
        public string Last = "";
    }

    readonly Dictionary<string, NameParts> names = new Dictionary<string, NameParts>();

    NameParts PartsOf(string name)
    {
        if (names.TryGetValue(name, out var parts))
            return parts;
        parts = new NameParts();
        var dot = LastDot(name);
        parts.Last = Utf8.Sub(name, dot + 1);
        if (dot >= 0)
        {
            var start = LastDot(Utf8.Sub(name, 0, dot)) + 1;
            parts.Class = Utf8.Sub(name, start, dot - start);
        }
        names[name] = parts;
        return parts;
    }

    // class の行の印。選んでいる個体は、並べる範囲の外でも必ず入れる
    FieldValue ClassLine(int line, string cls, List<FoundInstance> list)
    {
        var row = new FieldValue { Line = line, Class = cls };
        var start = pages.TryGetValue(cls, out var page) && page < list.Count ? page : 0;
        var end = Math.Min(list.Count, start + MaxMarks);
        var chosen = selected.TryGetValue(cls, out var id) ? IndexOf(list, id) : -1;
        var inside = chosen >= start && chosen < end;
        for (var i = start; i < end; i++)
        {
            var found = !inside && chosen >= 0 && i == end - 1 ? list[chosen] : list[i];
            row.Marks.Add(new FieldMark { Class = cls, Id = found.Id, Text = Tag(found.Id), Selected = found.Id == id });
        }
        if (list.Count > MaxMarks)
            row.Marks.Add(new FieldMark { Class = cls, Text = "… 全 " + list.Count + " 個" });
        return row;
    }

    // 個体の今の path (親の子に同じ Id が無ければ "")
    static string Fresh(Dictionary<string, List<InspectNode>> read, FoundInstance found)
    {
        var path = found.Path;
        var cut = -1;
        if (path.EndsWith("]"))
        {
            for (var i = Utf8.Bytes(path) - 1; i >= 0 && cut < 0; i--)
                if (Utf8.At(path, i) == '[')
                    cut = i;
        }
        else
            cut = LastDot(path);
        if (cut <= 0)
            return path;
        var parent = Utf8.Sub(path, 0, cut);
        Child(read, parent, "");
        foreach (var c in read[parent])
            if (c.Id == found.Id)
            {
                found.Path = c.Path;
                return c.Path;
            }
        return "";
    }

    static bool IsField(Dictionary<string, List<InspectNode>> read, string path, string name)
    {
        return Child(read, path, name) != null;
    }

    // path の子のうち名前が name の節 (引いた子は read に覚える)
    static InspectNode? Child(Dictionary<string, List<InspectNode>> read, string path, string name)
    {
        if (!read.TryGetValue(path, out var children))
        {
            children = GameApp.Inspect(path, 0, MaxChildren);
            read[path] = children;
        }
        foreach (var c in children)
            if (c.Name == name)
                return c;
        return null;
    }

    static int LastDot(string s)
    {
        for (var i = Utf8.Bytes(s) - 1; i >= 0; i--)
            if (Utf8.At(s, i) == '.')
                return i;
        return -1;
    }

    // 個体の Id を短くした目印 ("#a3f2")。表示だけに使う (偶然同じになることがある)
    public static string Tag(string id)
    {
        const string digits = "0123456789abcdef";
        var h = 0;
        var u = new Utf8Bytes(id);
        for (var i = 0; i < u.Length; i++)
            h = (h * 33 + u[i]) % 65521;
        h = h * 31 % 65521;
        var s = "#";
        for (var shift = 12; shift >= 0; shift -= 4)
            s += Utf8.Sub(digits, (h >> shift) & 15, 1);
        return s;
    }

    // static から辿って見つけた個体のうち、Id が id の物 (無ければ null)
    public FoundInstance? Find(string id)
    {
        foreach (var pair in instances)
        {
            var at = IndexOf(pair.Value, id);
            if (at >= 0)
                return new FoundInstance { Class = pair.Key, Path = pair.Value[at].Path, Id = id };
        }
        return null;
    }

    // class cls の個体 id の path (並べていなければ "")
    public string PathOf(string cls, string id)
    {
        if (instances.TryGetValue(cls, out var list) && IndexOf(list, id) >= 0)
            return list[IndexOf(list, id)].Path;
        return "";
    }

    public bool IsSelected(string cls, string id)
    {
        return selected.TryGetValue(cls, out var now) && now == id;
    }

    void Choose(string cls, FoundInstance found)
    {
        selected[cls] = found.Id;
        GameApp.InspectPin(cls, found.Path);
    }

    // path か、その祖先のうち一番近い個体を選ぶ (インスペクタの行から)
    public void Select(string path)
    {
        var at = path;
        while (at != "")
        {
            foreach (var pair in instances)
                foreach (var found in pair.Value)
                    if (found.Path == at)
                    {
                        Choose(pair.Key, found);
                        return;
                    }
            var dot = LastDot(at);
            at = dot < 0 ? "" : Utf8.Sub(at, 0, dot);
        }
    }

    // 個体を選ぶ (選んでいれば解く)
    public void Toggle(string cls, string id)
    {
        if (!instances.TryGetValue(cls, out var list) || IndexOf(list, id) < 0)
            return;
        if (IsSelected(cls, id))
        {
            selected.Remove(cls);
            GameApp.InspectPin(cls, "");
            return;
        }
        Choose(cls, list[IndexOf(list, id)]);
    }

    // class の行に、続きの個体を並べる (最後の次は最初)
    public void NextPage(string cls)
    {
        if (!instances.TryGetValue(cls, out var list))
            return;
        var next = (pages.TryGetValue(cls, out var page) ? page : 0) + MaxMarks;
        pages[cls] = next < list.Count ? next : 0;
    }
}

// 値を覚え、しばらく (SteadyFrames) 変わらなかった値が変わると、その後 SteadyFrames の間 光る強さを返す
public class ValueFlash
{
    public const int SteadyFrames = 20;
    readonly Dictionary<string, SeenValue> seen = new Dictionary<string, SeenValue>();
    int frame;

    public void Tick()
    {
        frame++;
        // 引かなくなった値は忘れる
        if (frame % 60 != 0)
            return;
        var stale = new List<string>();
        foreach (var pair in seen)
            if (frame - pair.Value.LastFrame > 60)
                stale.Add(pair.Key);
        foreach (var k in stale)
            seen.Remove(k);
    }

    public float Track(string path, string text)
    {
        if (!seen.TryGetValue(path, out var v))
        {
            v = new SeenValue { Text = text, Since = frame };
            seen[path] = v;
        }
        v.LastFrame = frame;
        if (v.Text != text)
        {
            v.ChangedAt = v.Since <= frame - SteadyFrames ? frame : -1;
            v.Text = text;
            v.Since = frame;
        }
        if (v.ChangedAt < 0)
            return 0;
        return Math.Max(0, 1 - (float)(frame - v.ChangedAt) / SteadyFrames);
    }
}

class SeenValue
{
    public string Text = "";
    // 今の Text になった frame
    public int Since;
    // しばらく変わらなかった後に変わった frame (-1 は無い)
    public int ChangedAt = -1;
    public int LastFrame;
}

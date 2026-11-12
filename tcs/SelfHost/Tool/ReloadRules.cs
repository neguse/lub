using System.Collections.Generic;

namespace TcsSelf;

// hot reload で live に当てられない編集の規則 (doc/hot-reload-design.md §11 の規則表)。
// VM の reload が IL の metadata の型をこの形に写して判定する
public sealed class ReloadStatic
{
    public string Name;
    public string Type;
    public string? InitHash;
    // 初期化子が直に読む static と const ("型\nfield")
    public List<string> Reads = new List<string>();

    public ReloadStatic(string name, string type, string? initHash)
    {
        Name = name;
        Type = type;
        InitHash = initHash;
    }

    public ReloadStatic(ExField f)
    {
        Name = f.Name;
        Type = f.Type;
        InitHash = f.InitHash;
        Reads = f.Reads;
    }
}

public sealed class ReloadType
{
    public string Name;
    // class | record | struct | enum
    public string Kind;
    public string BaseName = "";
    public List<ReloadStatic> Statics = new List<ReloadStatic>();
    // instance の field (Notes が初期化子の変更を見る。restart の判定には使わない)
    public List<ReloadStatic> InstanceFields = new List<ReloadStatic>();
    public List<string> EnumMemberNames = new List<string>();
    public List<int> EnumMemberValues = new List<int>();

    public ReloadType(string name, string kind)
    {
        Name = name;
        Kind = kind;
    }
}

public static class ReloadRules
{
    // 戻り値は restart の理由で、空なら live。生きている instance の field は reload が移し、新しい static の初期化子は reload が評価するので、
    // instance の形の変更と static の追加は restart にしない。初期化子か型を変えた static は reload が評価し直す (ReinitStatics)
    public static List<string> RestartReasons(List<ReloadType> v1, List<ReloadType> v2)
    {
        var reasons = new List<string>();
        var next = new Dictionary<string, ReloadType>();
        foreach (var t in v2) next[t.Name] = t;
        foreach (var old in v1)
        {
            ReloadType? t;
            if (!next.TryGetValue(old.Name, out t))
            {
                reasons.Add("type removed: " + old.Name);
                continue;
            }
            if (old.Kind != t.Kind)
            {
                reasons.Add("type kind changed: " + t.Name);
                continue;
            }
            if (old.BaseName != t.BaseName) reasons.Add("base changed: " + t.Name);
            StaticReasons(old, t, reasons);
            EnumReasons(old, t, reasons);
        }
        return reasons;
    }

    // 初期化子を消した static は、評価し直す式が無いので restart にする (保った値は旧い初期化子のまま)
    private static void StaticReasons(ReloadType old, ReloadType t, List<string> reasons)
    {
        var oldStatics = new Dictionary<string, ReloadStatic>();
        foreach (var s in old.Statics) oldStatics[s.Name] = s;
        foreach (var s in t.Statics)
        {
            ReloadStatic? os;
            if (oldStatics.TryGetValue(s.Name, out os) && os.Type == s.Type && os.InitHash != null && s.InitHash == null)
                reasons.Add("static initializer removed: " + t.Name + "." + s.Name);
        }
    }

    // reload が新しい初期化子で評価し直す static ("型\nfield")。初期化子か型を変えた static と const、
    // それを初期化子で読む static (読む先を辿る)。ほかの static は今の値を保つ
    public static List<string> ReinitStatics(List<ReloadType> v1, List<ReloadType> v2)
    {
        var prev = new Dictionary<string, ReloadStatic>();
        foreach (var t in v1)
        {
            foreach (var s in t.Statics) prev[t.Name + "\n" + s.Name] = s;
        }
        var changed = new HashSet<string>();
        var order = new List<string>();
        foreach (var t in v2)
        {
            foreach (var s in t.Statics)
            {
                string key = t.Name + "\n" + s.Name;
                ReloadStatic? os;
                if (!prev.TryGetValue(key, out os)) continue;
                if (os.Type == s.Type && os.InitHash == s.InitHash) continue;
                changed.Add(key);
                order.Add(key);
            }
        }
        bool grew = order.Count > 0;
        while (grew)
        {
            grew = false;
            foreach (var t in v2)
            {
                foreach (var s in t.Statics)
                {
                    string key = t.Name + "\n" + s.Name;
                    if (changed.Contains(key) || !prev.ContainsKey(key)) continue;
                    bool reads = false;
                    foreach (var r in s.Reads)
                    {
                        if (changed.Contains(r)) reads = true;
                    }
                    if (!reads) continue;
                    changed.Add(key);
                    order.Add(key);
                    grew = true;
                }
            }
        }
        return order;
    }

    // live に当てられるが、host に知らせたい変化。生きている instance は field の今の値を保つので、
    // instance の field の初期化子を変えても、生きている instance には入らない (新しく作る instance から入る)
    public static List<string> Notes(List<ReloadType> v1, List<ReloadType> v2)
    {
        var notes = new List<string>();
        var next = new Dictionary<string, ReloadType>();
        foreach (var t in v2) next[t.Name] = t;
        foreach (var old in v1)
        {
            ReloadType? t;
            if (!next.TryGetValue(old.Name, out t)) continue;
            var oldFields = new Dictionary<string, ReloadStatic>();
            foreach (var f in old.InstanceFields) oldFields[f.Name] = f;
            foreach (var f in t.InstanceFields)
            {
                ReloadStatic? of;
                if (oldFields.TryGetValue(f.Name, out of) && of.Type == f.Type && of.InitHash != f.InitHash)
                    notes.Add("instance field initializer changed: " + t.Name + "." + f.Name);
            }
        }
        return notes;
    }

    // 保存した enum の値 (int) は読み替えられない。member の追加だけが live
    private static void EnumReasons(ReloadType old, ReloadType t, List<string> reasons)
    {
        var members = new Dictionary<string, int>();
        for (int i = 0; i < t.EnumMemberNames.Count; i++) members[t.EnumMemberNames[i]] = t.EnumMemberValues[i];
        for (int i = 0; i < old.EnumMemberNames.Count; i++)
        {
            int v;
            if (!members.TryGetValue(old.EnumMemberNames[i], out v) || v != old.EnumMemberValues[i])
                reasons.Add("enum member removed or renumbered: " + t.Name + "." + old.EnumMemberNames[i]);
        }
    }

    // IL の metadata の型 (--ref の class は host の型、interface は状態を持たないので除く)
    public static List<ReloadType> TypesOf(ExportResult export)
    {
        var types = new List<ReloadType>();
        foreach (var c in export.Classes)
        {
            if (c.IsExternal || c.IsInterface) continue;
            var t = new ReloadType(c.Name, c.IsRecord ? "record" : "class");
            t.BaseName = c.BaseName != null ? c.BaseName : "";
            foreach (var f in c.Fields)
            {
                if (f.IsStatic) t.Statics.Add(new ReloadStatic(f));
                else t.InstanceFields.Add(new ReloadStatic(f));
            }
            types.Add(t);
        }
        foreach (var s in export.Structs)
        {
            var t = new ReloadType(s.Name, "struct");
            foreach (var f in s.StaticFields) t.Statics.Add(new ReloadStatic(f));
            types.Add(t);
        }
        foreach (var e in export.EnumTypes)
        {
            var t = new ReloadType(e.Name, "enum");
            foreach (var n in e.MemberNames) t.EnumMemberNames.Add(n);
            foreach (var v in e.MemberValues) t.EnumMemberValues.Add(v);
            types.Add(t);
        }
        return types;
    }
}

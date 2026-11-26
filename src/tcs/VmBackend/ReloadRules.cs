using System.Collections.Generic;
using Tcs;

namespace TcsVm;

// Rules for edits that hot reload cannot apply live (the rule table in docs/tcs/hot-reload-design.md §11).
// The VM reload maps the types in the IL metadata into this shape to decide.
public sealed class ReloadStatic
{
    public string Name;
    public string Type;
    public string? InitHash;
    // Statics and consts that the initializer reads directly ("type\nfield")
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
    // Instance fields (Notes checks them for initializer changes. Not used to decide a restart)
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
    // The return value is the restart reasons; empty means live. Reload moves the fields of live instances and evaluates the initializers of new statics, so
    // instance shape changes and added statics do not cause a restart. Reload re-evaluates statics whose initializer or type changed (ReinitStatics).
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

    // A static whose initializer was removed has no expression to re-evaluate, so it causes a restart (the kept value would stay that of the old initializer).
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

    // Statics that reload re-evaluates with the new initializer ("type\nfield"): statics and consts whose initializer or type changed,
    // and statics whose initializers read them (following what they read). Other statics keep their current values.
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

    // Changes that can be applied live but that the host should be told about. Live instances keep the current values of their fields, so
    // changing an instance field's initializer does not reach live instances (it applies from newly created instances).
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

    // Saved enum values (int) cannot be remapped. Only adding members is live.
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

    // Types in the IL metadata (excluding --ref classes, which are host types, and interfaces, which have no state)
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

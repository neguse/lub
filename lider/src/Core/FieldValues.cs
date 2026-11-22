// Looks up the current values shown on field declaration lines from the game's current values (lub's Sys.Inspect).
// For a static, its value. Instances are found by walking from statics: if there is one, its value; if there are several, the value of the selected instance (nothing is shown until one is selected).
// If there are several, up to MaxMarks instance marks are listed on the class declaration line. Clicking one selects it.
// Instances are identified by their table address (InspectNode.Id). The index in a list changes as elements are added or removed, so it is not used to mark an instance.
// lub holds the selected instance weakly, and the selection is cleared when it is collected or no longer reachable from statics (it does not move to another instance).
using TinySystem.Text;
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

// One clickable mark on a class declaration line
public class FieldMark
{
    public string Class = "";
    // Instance Id. "" is the mark that shows more
    public string Id = "";
    // Text to show ("#a3f2", "… 全 9 個")
    public string Text = "";
    public bool Selected;
    // Where it was drawn in the window (set by PanelView when drawing)
    public float X0;
    public float X1;
}

public class FieldValue
{
    // Line (from 1)
    public int Line;
    // Value ("" for class lines)
    public string Text = "";
    // Path of the value node (the Path of Watched.Inspect; "" for class lines)
    public string Path = "";
    // Strength of the highlight after a change (0 to 1)
    public float Glow;
    // For numbers, the values of recent frames (oldest first; null if not a number)
    public List<float>? History;
    // The field's class, or the class of the declaration
    public string Class = "";
    // Marks on the class line (marks for the several instances, and the mark that shows more)
    public readonly List<FieldMark> Marks = new List<FieldMark>();
}

// An instance found by walking from statics
public class FoundInstance
{
    public string Class = "";
    public string Path = "";
    public string Id = "";
}

public class FieldValues
{
    // Interval for searching for instances again, and the limits on walk depth and children per node
    const int DiscoverFrames = 15;
    const int MaxDepth = 8;
    const int MaxChildren = 1000;
    // Number of instances listed on a class line
    const int MaxMarks = 4;

    // class → found instances (in the order found)
    Dictionary<string, List<FoundInstance>> instances = new Dictionary<string, List<FoundInstance>>();
    // class → Id of the selected instance
    readonly Dictionary<string, string> selected = new Dictionary<string, string>();
    // class → index of the first listed instance
    readonly Dictionary<string, int> pages = new Dictionary<string, int>();
    readonly ValueFlash flash = new ValueFlash();
    int frame;

    // Called every frame
    public void Refresh()
    {
        flash.Tick();
        if (frame++ % DiscoverFrames == 0)
        {
            Discover();
            Forget();
        }
    }

    // History of numeric values over recent frames (one per frame the game advanced; appended only while shown)
    class History
    {
        public List<float> Values = new List<float>();
        // The last frame appended (GameApp.Frames)
        public int Frame = -1;
    }

    readonly Dictionary<string, History> histories = new Dictionary<string, History>();

    // Appends the current value to key's history and returns it (null if not a number)
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

    // Discards histories that have not been shown for a while
    void Forget()
    {
        var gone = new List<string>();
        foreach (var pair in histories)
            if (GameApp.Frames - pair.Value.Frame > TraceLog.Fade)
                gone.Add(pair.Key);
        foreach (var key in gone)
            histories.Remove(key);
    }

    // Finds instances by walking from statics (each instance once, at the first path found), and clears the selection of instances that were collected or are no longer reachable
    void Discover()
    {
        instances = new Dictionary<string, List<FoundInstance>>();
        foreach (var n in Watched.InspectInstances(MaxDepth, MaxChildren))
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
            if (!Watched.InspectPinned(pair.Key, pair.Value) || !instances.TryGetValue(pair.Key, out var found) || IndexOf(found, pair.Value) < 0)
                gone.Add(pair.Key);
        foreach (var cls in gone)
        {
            selected.Remove(cls);
            Watched.InspectPin(cls, "");
        }
    }

    static int IndexOf(List<FoundInstance> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return i;
        return -1;
    }

    // Current values of the fields among the declarations on lines first to last (from 1)
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
                value.Path = node.Path;
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
            // Even if the order shifts before the next search, it does not read a different instance
            var path = Fresh(read, one);
            node = path != "" ? Child(read, path, field) : null;
            if (node == null)
                continue;
            value.Text = node.Text;
            value.Path = node.Path;
            // Remembered per instance so it does not light up when the order shifts and the path changes
            value.Glow = flash.Track(one.Id + "." + field, node.Text);
            // Remembered per instance so it follows the same instance even if the order shifts
            value.History = Sample(one.Id + "." + field, node.Text);
            shown.Add(value);
        }
        return shown;
    }

    // The last part of a declaration name and the part before it (the class; "" if there is only one part). Cached per name to avoid splitting every frame
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

    // Marks on the class line. The selected instance is always included, even outside the listed range
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

    // Current path of an instance ("" if the parent has no child with the same Id)
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

    // The child node of path named name (children looked up are recorded in read)
    static InspectNode? Child(Dictionary<string, List<InspectNode>> read, string path, string name)
    {
        if (!read.TryGetValue(path, out var children))
        {
            children = Watched.Inspect(path, 0, MaxChildren);
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

    // A mark made by shortening the instance Id ("#a3f2"). Used only for display (two can match by chance)
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

    // The instance found from statics whose Id is id (null if none)
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

    // Path of instance id of class cls ("" if not listed)
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
        Watched.InspectPin(cls, found.Path);
    }

    // Selects the nearest instance among path and its ancestors (from an inspector line)
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

    // Selects an instance (deselects it if already selected)
    public void Toggle(string cls, string id)
    {
        if (!instances.TryGetValue(cls, out var list) || IndexOf(list, id) < 0)
            return;
        if (IsSelected(cls, id))
        {
            selected.Remove(cls);
            Watched.InspectPin(cls, "");
            return;
        }
        Choose(cls, list[IndexOf(list, id)]);
    }

    // Lists the next instances on the class line (after the last comes the first)
    public void NextPage(string cls)
    {
        if (!instances.TryGetValue(cls, out var list))
            return;
        var next = (pages.TryGetValue(cls, out var page) ? page : 0) + MaxMarks;
        pages[cls] = next < list.Count ? next : 0;
    }
}

// Remembers values, and when a value that has not changed for a while (SteadyFrames) changes, returns a highlight strength for the following SteadyFrames
public class ValueFlash
{
    public const int SteadyFrames = 20;
    readonly Dictionary<string, SeenValue> seen = new Dictionary<string, SeenValue>();
    int frame;

    public void Tick()
    {
        frame++;
        // Forgets values that are no longer looked up
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
    // The frame when it became the current Text
    public int Since;
    // The frame when it changed after a steady period (-1 = none)
    public int ChangedAt = -1;
    public int LastFrame;
}

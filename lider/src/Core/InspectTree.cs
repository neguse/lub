// Inspector lines: lists the game's current values (lub's Sys.Inspect) and recently executed methods (TraceLog), walking only the open nodes.
// Under a class, methods (with execution marks) and static fields (with values) are listed in the order of declaration in the source. Under an instance, fields are listed in declaration order.
// Sequences (List and arrays) whose elements are all instances of the same class become a table, with one element per row and fields as columns.
// Sequences with more than ChunkSize elements are split into nodes of ChunkSize each.
// Values are looked up again on every Refresh, and when a value that has not changed for a while (SteadyFrames) changes, Glow is set for the following SteadyFrames.
// Scope: everything / class / instance / a node below it. Only the children of the scoped node are listed, and the breadcrumbs (Crumbs) go back up a level.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class InspectRow
{
    // Key for opening and closing (the node's Path; for a chunk, the sequence's Path + "#" + the chunk index; for a method, "class.method()")
    public string Key = "";
    public int Depth;
    public string Name = "";
    // Id of the instance ("" otherwise)
    public string Id = "";
    // Whether the instance with Id is selected (set by InspectView)
    public bool Selected;
    // Name, and the instance mark
    public string Label => Id == "" ? Name : Name + " " + FieldValues.Tag(Id);
    public string Text = "";
    // Name of the declaration to jump to (the class for class and instance, "class.member" for fields and methods; "" if none)
    public string Target = "";
    // Whether the file of Target's class is executing (set by InspectView)
    public bool Running;
    public bool Openable;
    public bool Open;
    // The same table as an ancestor (cannot be opened)
    public bool Cycle;
    // Column names for a table header, per-column values for a table row ("" for missing fields). null otherwise
    public List<string>? Cells;
    public bool Header;
    // A method line (Glow is the recent-execution strength)
    public bool Method;
    // A line that can be scoped into with ≫ (class, instance, node with children)
    public bool Scopable;
    // Highlight strength from a change in the Text and Cells values, or the recent-execution strength for methods (0 to 1)
    public float Glow;
    public List<float>? CellGlow;
    // The drawn value (Text, or the per-column values of a table row; values that did not fit the width end with …; set by InspectView)
    public readonly List<string> Shown = new List<string>();
}

public class InspectTree
{
    public const int ChunkSize = 100;
    public const int MaxColumns = 8;
    // Key that weakly holds the scoped instance (Sys.InspectPin; TraceLog.Take also uses it to look up the lines for that instance)
    public const string ScopePin = "lider_scope";

    public readonly List<InspectRow> Rows = new List<InspectRow>();
    readonly Dictionary<string, bool> opened = new Dictionary<string, bool>();
    readonly ValueFlash flash = new ValueFlash();
    Workspace? work;
    Search? search;

    // Scope: class (ScopeClass), instance (ScopeBase is its Path, ScopeId its Id), and a node below it (ScopePath). For everything, all are ""
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

    // Breadcrumb names (the first is everything)
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

    // Goes back to the i-th breadcrumb level
    public void ScopeTo(int i)
    {
        if (i <= 0)
            ScopeClass = "";
        if (i <= 1)
        {
            ScopeBase = "";
            ScopeId = "";
            ScopePath = "";
            Watched.InspectPin(ScopePin, "");
        }
        else if (i == 2 && ScopeBase != "")
            ScopePath = ScopeBase;
    }

    // Scopes into a line (a class root to the class, an instance to the instance, any other node to that node)
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

    // Scopes into the instance at path (class cls, Id id)
    public void EnterInstance(string cls, string id, string path)
    {
        ScopeClass = cls;
        ScopeBase = path;
        ScopePath = path;
        ScopeId = id;
        Watched.InspectPin(ScopePin, path);
    }

    // When the scoped instance's path changes (elements were added to or removed from the sequence), moves the scope along with the node below it
    public void Rebase(string path)
    {
        if (ScopeBase == "" || path == ScopeBase)
            return;
        ScopePath = path + Utf8.Sub(ScopePath, Utf8.Bytes(ScopeBase));
        ScopeBase = path;
    }

    // Rebuilds Rows by walking the open nodes from the roots.
    // With w and search, lists methods under classes and orders fields by declaration (otherwise in Sys.Inspect order)
    public void Refresh(Workspace? w = null, Search? s = null)
    {
        work = w;
        search = s;
        flash.Tick();
        Rows.Clear();
        // If the scoped instance is collected or no longer reachable, goes back to the class (does not move to another instance)
        if (ScopeId != "" && !Watched.InspectPinned(ScopePin, ScopeId))
            ScopeTo(1);
        if (ScopePath != "")
        {
            // Under an instance, also lists the methods hit on that instance (TraceLog holds only that instance's lines)
            if (ScopePath == ScopeBase)
                AddMembers(ScopeClass, ScopePath, 0, true);
            else
                AddRange(ScopePath, 0, 1000, 0, ScopeClass);
            return;
        }
        foreach (var n in Watched.Inspect("", 0, 1000))
        {
            if (ScopeClass == "")
                AddNode(n, 0, "");
            else if (n.Name == ScopeClass)
                AddMembers(n.Name, n.Path, 0, true);
        }
    }

    // Turns node n in the owning class (owner) into a line
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
            // The value of a root (class) is a shortened class table with nothing worth reading
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

    // Lists the children of the node at path of class cls in declaration order. With methods, also lists the class's methods (with execution marks).
    // Fields whose declaration is not found follow at the end
    void AddMembers(string cls, string path, int depth, bool methods)
    {
        var fields = Watched.Inspect(path, 0, 1000);
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

    // Direct declarations of class cls (methods and fields, not types), in source order.
    // Methods get their recent-execution strength
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

    // Recent-execution strength per declaration in path (methods only; 0 for others).
    // An executed line counts toward the nearest method before it (lines in local functions and lambdas count toward the enclosing method)
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

    // Adds count children of path starting at offset, as table or tree lines
    void AddRange(string path, int offset, int count, int depth, string owner)
    {
        var children = Watched.Inspect(path, offset, count);
        if (!SameClass(children))
        {
            foreach (var c in children)
                AddNode(c, depth, owner);
            return;
        }
        var columns = new List<string>();
        foreach (var f in Watched.Inspect(children[0].Path, 0, MaxColumns))
            columns.Add(f.Name);
        Rows.Add(new InspectRow { Key = path + "#columns", Depth = depth, Name = children[0].Type, Cells = columns, Header = true });
        foreach (var c in children)
        {
            var cells = new List<string>();
            var glow = new List<float>();
            var fields = Watched.Inspect(c.Path, 0, MaxColumns);
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

    // true for a sequence whose elements are all instances of the same class
    static bool SameClass(List<InspectNode> children)
    {
        if (children.Count < 2 || children[0].Type == "")
            return false;
        foreach (var c in children)
            if (c.Type != children[0].Type || !c.Name.StartsWith("["))
                return false;
        return true;
    }

    // Name of the root class of path ("Shelf" for "Shelf.Crates[0]")
    static string Root(string path)
    {
        var dot = Utf8.IndexOf(path, ".");
        return dot < 0 ? path : Utf8.Sub(path, 0, dot);
    }
}

// A direct declaration of a class and, for methods, its recent-execution strength
class Member
{
    public Declaration Decl = new Declaration();
    public float Glow;
}

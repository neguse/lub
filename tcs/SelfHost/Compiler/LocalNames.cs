using System.Collections.Generic;

namespace TcsSelf;

// IL names (IlName) of local bindings (local variables / parameters / foreach variables / pattern designations / lambda parameters).
// A name that matches a type name or a global that IL callees reference by bare name
// is mapped `x` → `x_`, and if the result is an identifier in the same member or a type name, another `_` is added. The same name in the same member maps to the same name
public static class LocalNames
{
    private static readonly string[] EmittedGlobalsList = ("_ENV _G assert error getmetatable ipairs load next pairs pcall print rawequal rawget rawlen "
        + "rawset require select setmetatable tonumber tostring type xpcall coroutine debug io math os package string table utf8").Split(' ');

    // Runtime globals (same as Compliance.RuntimeGlobals)
    private static readonly string[] RuntimeGlobalsList = "TinySystem List Dict Math String TcsQueue TcsHashSet".Split(' ');

    // Globals of the runtime's math and UTF-8 modules (generated code looks them up by bare name)
    public const string MathModuleGlobal = "tcs_math";
    public const string Utf8ModuleGlobal = "tcs_utf8";

    private static HashSet<string>? _globals;
    private static HashSet<string>? _reserved;

    // Names avoided when renaming pattern variables (Binder) (self, and globals that IL callees reference by bare name)
    public static bool IsReserved(string name)
    {
        if (_reserved == null)
        {
            var r = new HashSet<string>();
            string all = "self print tostring tonumber type setmetatable getmetatable rawget rawset rawequal rawlen string table math error select pairs ipairs next require pcall assert utf8 os io coroutine load dofile collectgarbage _ENV _G List Dict String Math Random Char TcsQueue TcsHashSet TinySystem tcs_math tcs_utf8 tcs_rt";
            foreach (var w in all.Split(' ')) r.Add(w);
            _reserved = r;
        }
        return _reserved.Contains(name);
    }

    private static bool IsGlobal(string name)
    {
        if (_globals == null)
        {
            var g = new HashSet<string>();
            foreach (var x in EmittedGlobalsList) g.Add(x);
            foreach (var x in RuntimeGlobalsList) g.Add(x);
            g.Add(MathModuleGlobal);
            g.Add(Utf8ModuleGlobal);
            _globals = g;
        }
        return _globals.Contains(name);
    }

    // Type names that local renaming avoids (simple names, global names, the first segment of --ref type paths)
    public static HashSet<string> ReservedTypeNames(Compilation c)
    {
        var names = new HashSet<string>();
        foreach (var t in c.Types)
        {
            names.Add(t.Name);
            if (t.IsRef)
            {
                var path = t.IlName;
                if (path != "") names.Add(path.Split('.')[0]);
            }
            else names.Add(t.IlName);
        }
        return names;
    }

    public static bool IsCandidate(string name, HashSet<string> typeNames)
    {
        return typeNames.Contains(name) || IsGlobal(name);
    }

    // Assigns local names for all members of the unit (including nested types) and for top-level statements
    public static void AssignUnit(Node unit, HashSet<string> typeNames)
    {
        if (unit.List != null)
        {
            foreach (var m in unit.List) AssignMember(m, typeNames);
        }
        if (unit.List2 != null && unit.List2.Count > 0)
        {
            // The scope of top-level statements is the whole compilation unit
            var decls = new List<Node>();
            foreach (var st in unit.List2) CollectDecls(st, decls);
            Assign(unit, decls, typeNames);
        }
    }

    private static void AssignMember(Node m, HashSet<string> typeNames)
    {
        switch (m.Kind)
        {
            case NodeKind.Namespace:
            case NodeKind.TypeDecl:
                if (m.Kind == NodeKind.TypeDecl && m.A != null)
                {
                    // Positional parameters of a record are members (not handled here)
                }
                if (m.List != null)
                {
                    foreach (var x in m.List) AssignMember(x, typeNames);
                }
                return;
            case NodeKind.Using:
            case NodeKind.EnumMember:
                return;
        }
        var decls = new List<Node>();
        CollectDecls(m, decls);
        if (decls.Count > 0) Assign(m, decls, typeNames);
    }

    private static void CollectDecls(Node n, List<Node> into)
    {
        if (n.Local != null)
        {
            switch (n.Kind)
            {
                case NodeKind.Param:
                case NodeKind.VarDeclarator:
                case NodeKind.Foreach:
                case NodeKind.PatDecl:
                case NodeKind.PatVar:
                case NodeKind.PatProp:
                    into.Add(n);
                    break;
            }
        }
        if (n.Kind == NodeKind.TypeDecl) return;
        if (n.A != null) CollectDecls(n.A, into);
        if (n.B != null) CollectDecls(n.B, into);
        if (n.C != null) CollectDecls(n.C, into);
        if (n.D != null) CollectDecls(n.D, into);
        if (n.List != null)
        {
            foreach (var x in n.List) CollectDecls(x, into);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) CollectDecls(x, into);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) CollectDecls(x, into);
        }
    }

    private static void Assign(Node scope, List<Node> decls, HashSet<string> typeNames)
    {
        HashSet<string>? taken = null;
        var chosenFrom = new List<string>();
        var chosenTo = new List<string>();
        foreach (var d in decls)
        {
            var l = d.Local!;
            l.IlName = l.Name;
            if (!IsCandidate(l.Name, typeNames)) continue;
            if (taken == null)
            {
                taken = new HashSet<string>();
                CollectIdentifiers(scope, taken);
            }
            int k = chosenFrom.IndexOf(l.Name);
            if (k >= 0)
            {
                l.IlName = chosenTo[k];
                continue;
            }
            var name = l.Name + "_";
            while (taken.Contains(name) || typeNames.Contains(name)) name = name + "_";
            chosenFrom.Add(l.Name);
            chosenTo.Add(name);
            taken.Add(name);
            l.IlName = name;
        }
        // Pattern designations are forward-declared before the switch or if, so designations with the same name in the same member
        // (`case A x: ... case B x:`) get different names (the C backend looks up locals by name; T283)
        var patternNames = new List<string>();
        foreach (var d in decls)
        {
            if (d.Kind != NodeKind.PatDecl && d.Kind != NodeKind.PatVar && d.Kind != NodeKind.PatProp) continue;
            var l = d.Local!;
            if (!patternNames.Contains(l.IlName))
            {
                patternNames.Add(l.IlName);
                continue;
            }
            if (taken == null)
            {
                taken = new HashSet<string>();
                CollectIdentifiers(scope, taken);
            }
            int n = 2;
            var name = l.IlName + "_" + n;
            while (taken.Contains(name) || typeNames.Contains(name) || patternNames.Contains(name))
            {
                n++;
                name = l.IlName + "_" + n;
            }
            taken.Add(name);
            patternNames.Add(name);
            l.IlName = name;
        }
    }

    // Spellings of identifiers in scope
    private static void CollectIdentifiers(Node n, HashSet<string> into)
    {
        switch (n.Kind)
        {
            case NodeKind.StringLit:
            case NodeKind.CharLit:
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
            case NodeKind.InterpString:
                break;
            case NodeKind.Binary:
            case NodeKind.Unary:
            case NodeKind.Assign:
            case NodeKind.PostIncDec:
            case NodeKind.PreIncDec:
            case NodeKind.PatRel:
                break;
            default:
                if (n.Text != "")
                {
                    foreach (var part in n.Text.Split('.')) into.Add(part);
                }
                break;
        }
        if (n.A != null) CollectIdentifiers(n.A, into);
        if (n.B != null) CollectIdentifiers(n.B, into);
        if (n.C != null) CollectIdentifiers(n.C, into);
        if (n.D != null) CollectIdentifiers(n.D, into);
        if (n.List != null)
        {
            foreach (var x in n.List) CollectIdentifiers(x, into);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) CollectIdentifiers(x, into);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) CollectIdentifiers(x, into);
        }
        if (n.Kind == NodeKind.InterpString && n.List != null)
        {
            foreach (var x in n.List)
            {
                if (x.Kind == NodeKind.InterpHole && x.A != null) CollectIdentifiers(x.A, into);
            }
        }
    }
}

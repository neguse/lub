using System.Collections.Generic;

namespace TcsSelf;

// local の束縛 (local 変数 / parameter / foreach の変数 / pattern の designation / lambda の parameter) の Lua の名前
// (参照実装の LuaLocalRenamer)。Lua の予約語、型の名前、生成コードが素の名前で参照する global と同じ名前は
// `x` → `x_` に写し、写した先が同じ member の中の識別子か型の名前なら `_` を足す。同じ member の同じ名前は同じ名前に写す
public static class LocalNames
{
    private static readonly string[] EmittedGlobalsList = ("_ENV _G assert error getmetatable ipairs load next pairs pcall print rawequal rawget rawlen "
        + "rawset require select setmetatable tonumber tostring type xpcall coroutine debug io math os package string table utf8").Split(' ');

    // runtime の global (TinyCsComplianceFacts.ReservedRuntimeGlobals と RuntimeGlobalAliases)
    private static readonly string[] RuntimeGlobalsList = "TinySystem List Dict Math String TcsQueue TcsHashSet".Split(' ');

    private static HashSet<string>? _globals;

    private static bool IsGlobal(string name)
    {
        if (_globals == null)
        {
            var g = new HashSet<string>();
            foreach (var x in EmittedGlobalsList) g.Add(x);
            foreach (var x in RuntimeGlobalsList) g.Add(x);
            g.Add(LuaGen.MathModuleGlobal);
            g.Add(LuaGen.Utf8ModuleGlobal);
            _globals = g;
        }
        return _globals.Contains(name);
    }

    public static bool IsCandidate(string name, HashSet<string> typeNames)
    {
        return LuaNames.IsLuaKeyword(name) || typeNames.Contains(name) || IsGlobal(name);
    }

    // unit の全ての member (入れ子の型を含む) と top-level の文の local の名前を付ける
    public static void AssignUnit(Node unit, HashSet<string> typeNames)
    {
        if (unit.List != null)
        {
            foreach (var m in unit.List) AssignMember(m, typeNames);
        }
        if (unit.List2 != null && unit.List2.Count > 0)
        {
            // top-level の文の範囲は compilation unit 全体
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
                    // record の positional parameter は member (対象外)
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
            l.LuaName = l.Name;
            if (!IsCandidate(l.Name, typeNames)) continue;
            if (taken == null)
            {
                taken = new HashSet<string>();
                CollectIdentifiers(scope, taken);
            }
            int k = chosenFrom.IndexOf(l.Name);
            if (k >= 0)
            {
                l.LuaName = chosenTo[k];
                continue;
            }
            var name = l.Name + "_";
            while (taken.Contains(name) || typeNames.Contains(name)) name = name + "_";
            chosenFrom.Add(l.Name);
            chosenTo.Add(name);
            taken.Add(name);
            l.LuaName = name;
        }
    }

    // scope の中の識別子の綴り
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

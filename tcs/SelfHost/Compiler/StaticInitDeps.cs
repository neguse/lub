using System.Collections.Generic;

namespace TcsSelf;

// 型の static の初期化子が初期化を要求する他の型 (参照実装の StaticInitDependencies)。
// 初期化子 (とそこから呼ぶ project の中の method の推移閉包) が static field に触れる他の型を依存とする。
// method ごとの直接の効果 (触る型 / 呼ぶ method) を memo し、型ごとに到達閉包を取る。
// lambda の本文は delegate を作るだけでは実行されないので辿らない
public sealed class StaticInitDeps
{
    private readonly Compilation _c;
    // 出力するファイルの Id
    private readonly HashSet<int> _files = new HashSet<int>();
    // method の効果の memo (key は Symbol.Serial)
    private readonly Dictionary<int, InitEffects> _methods = new Dictionary<int, InitEffects>();
    private int _serial;
    // 明示の constructor を持たない型の暗黙の constructor (key は TypeDeclSym.Order)
    private readonly Dictionary<int, Symbol> _implicitCtors = new Dictionary<int, Symbol>();

    public StaticInitDeps(Compilation c, List<int> emitted)
    {
        _c = c;
        foreach (var i in emitted) _files.Add(c.Files[i].Id);
    }

    private bool InProject(TypeDeclSym? t)
    {
        return t != null && !t.IsRef && _files.Contains(t.Decl.File.Id);
    }

    private int SerialOf(Symbol m)
    {
        if (m.Serial == 0)
        {
            _serial++;
            m.Serial = _serial;
        }
        return m.Serial;
    }

    // 型 (Lua の名前) → 依存する型 (発見の順)。static の初期化子を持たない型は載せない
    public Dictionary<string, List<string>> DependenciesByName()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var t in _c.Types)
        {
            if (!InProject(t) || t.IsInterface || t.IsEnum) continue;
            if (StaticInitializers(t).Count == 0) continue;
            var names = new List<string>();
            foreach (var d in Of(t)) names.Add(d.LuaName);
            result[t.LuaName] = names;
        }
        return result;
    }

    private List<TypeDeclSym> Of(TypeDeclSym type)
    {
        var types = new List<TypeDeclSym>();
        var seenTypes = new HashSet<int>();
        seenTypes.Add(type.Order);
        var seenMethods = new HashSet<int>();
        var work = new List<Symbol>();
        foreach (var init in StaticInitializers(type))
        {
            AddEffects(Collect(init), types, seenTypes, seenMethods, work);
            while (work.Count > 0)
            {
                var m = work[work.Count - 1];
                work.RemoveAt(work.Count - 1);
                AddEffects(MethodEffects(m), types, seenTypes, seenMethods, work);
            }
        }
        return types;
    }

    private void AddEffects(InitEffects e, List<TypeDeclSym> types, HashSet<int> seenTypes, HashSet<int> seenMethods, List<Symbol> work)
    {
        foreach (var t in e.Types)
        {
            if (seenTypes.Add(t.Order)) types.Add(t);
        }
        // 発見の順に辿るため逆順に積む
        for (int i = e.Callees.Count - 1; i >= 0; i--)
        {
            // 組み込みの表の Symbol は compile をまたいで共有するので Serial を振らない (本文が無く効果も無い)
            if (e.Callees[i].Builtin != "") continue;
            if (seenMethods.Add(SerialOf(e.Callees[i]))) work.Add(e.Callees[i]);
        }
    }

    // static (と const) の field / property の初期化子のうち定数でないもの
    private List<Node> StaticInitializers(TypeDeclSym t)
    {
        var r = new List<Node>();
        var decl = t.Decl;
        if (decl.List == null) return r;
        foreach (var m in decl.List)
        {
            if (!m.Has(Mods.Static) && !m.Has(Mods.Const)) continue;
            if (m.Kind == NodeKind.Field && m.List != null)
            {
                foreach (var v in m.List)
                {
                    if (v.A != null && !v.A.HasConst) r.Add(v.A);
                }
            }
            else if (m.Kind == NodeKind.Property && m.D != null && !m.D.HasConst) r.Add(m.D);
        }
        return r;
    }

    private List<Node> InstanceInitializers(TypeDeclSym t)
    {
        var r = new List<Node>();
        var decl = t.Decl;
        if (decl.List == null) return r;
        foreach (var m in decl.List)
        {
            if (m.Has(Mods.Static) || m.Has(Mods.Const)) continue;
            if (m.Kind == NodeKind.Field && m.List != null)
            {
                foreach (var v in m.List)
                {
                    if (v.A != null) r.Add(v.A);
                }
            }
            else if (m.Kind == NodeKind.Property && m.D != null) r.Add(m.D);
        }
        return r;
    }

    private InitEffects MethodEffects(Symbol m)
    {
        if (m.Builtin != "") return new InitEffects();
        if (_methods.TryGetValue(SerialOf(m), out var cached)) return cached;
        var effects = new InitEffects();
        _methods[SerialOf(m)] = effects;
        var d = m.Decl;
        if (d != null && _files.Contains(d.File.Id) && (m.Owner == null || !m.Owner.IsRef))
        {
            switch (d.Kind)
            {
                case NodeKind.Method:
                case NodeKind.OperatorDecl:
                case NodeKind.Ctor:
                    if (d.D != null && d.Kind == NodeKind.Ctor) Merge(effects, Collect(d.D));
                    if (d.B != null) Merge(effects, Collect(d.B));
                    if (d.C != null) Merge(effects, Collect(d.C));
                    break;
                case NodeKind.Property:
                    // getter と setter (参照実装は property の参照で両方を呼ぶ先にする)
                    if (d.B != null)
                    {
                        if (d.B.A != null) Merge(effects, Collect(d.B.A));
                        if (d.B.B != null) Merge(effects, Collect(d.B.B));
                    }
                    if (d.C != null)
                    {
                        if (d.C.A != null) Merge(effects, Collect(d.C.A));
                        if (d.C.B != null) Merge(effects, Collect(d.C.B));
                    }
                    break;
            }
        }
        // instance の生成は field の初期化子を走らせる
        if (m.Kind == SymKind.Ctor && !m.IsStatic && m.Owner != null && InProject(m.Owner))
        {
            foreach (var init in InstanceInitializers(m.Owner)) Merge(effects, Collect(init));
        }
        // interface の呼び出しは project の中の実装すべてに届きうる
        if (m.Kind == SymKind.Method && m.Owner != null && m.Owner.IsInterface)
        {
            foreach (var impl in Implementations(m)) effects.Callees.Add(impl);
        }
        return effects;
    }

    private List<Symbol> Implementations(Symbol ifaceMethod)
    {
        var r = new List<Symbol>();
        foreach (var t in _c.Types)
        {
            if (!InProject(t) || t.IsInterface) continue;
            bool implements = false;
            foreach (var i in IlTypes.AllInterfaces(t))
            {
                if (i.Decl == ifaceMethod.Owner) implements = true;
            }
            if (!implements) continue;
            foreach (var mm in t.Members)
            {
                if (mm.Kind == SymKind.Method && !mm.IsStatic && mm.Name == ifaceMethod.Name && mm.ParamCount == ifaceMethod.ParamCount) r.Add(mm);
            }
        }
        return r;
    }

    private static void Merge(InitEffects into, InitEffects from)
    {
        foreach (var t in from.Types) into.Types.Add(t);
        foreach (var c in from.Callees) into.Callees.Add(c);
    }

    private InitEffects Collect(Node root)
    {
        var effects = new InitEffects();
        Walk(root, effects);
        return effects;
    }

    private void Walk(Node n, InitEffects effects)
    {
        // lambda の本文は実行されない
        if (n.Kind == NodeKind.Lambda) return;
        switch (n.Kind)
        {
            case NodeKind.Name:
            case NodeKind.Member:
            {
                var s = n.Sym;
                if (s != null && s.Builtin == "")
                {
                    if (s.Kind == SymKind.Field && s.IsStatic && !s.IsConst) AddType(effects, s.Owner);
                    else if (s.Kind == SymKind.Property)
                    {
                        if (s.IsStatic && s.IsAuto) AddType(effects, s.Owner);
                        if (!s.IsAuto && s.Decl != null) effects.Callees.Add(s);
                    }
                }
                break;
            }
            case NodeKind.Call:
                if (n.Sym != null && n.Sym.Builtin == "" && (n.Sym.Kind == SymKind.Method || n.Sym.Kind == SymKind.Ctor)) effects.Callees.Add(n.Sym);
                break;
            case NodeKind.New:
            {
                var t = n.Type;
                if (t != null && t.Decl != null && InProject(t.Decl))
                {
                    if (n.Sym != null && n.Sym.Kind == SymKind.Ctor) effects.Callees.Add(n.Sym);
                    else effects.Callees.Add(ImplicitCtor(t.Decl));
                }
                break;
            }
            case NodeKind.Binary:
            case NodeKind.Unary:
            case NodeKind.Assign:
                if (n.Sym != null && n.Sym.Builtin == "" && n.Sym.Kind == SymKind.Method) effects.Callees.Add(n.Sym);
                break;
        }
        if (n.A != null) Walk(n.A, effects);
        if (n.B != null) Walk(n.B, effects);
        if (n.C != null) Walk(n.C, effects);
        if (n.D != null) Walk(n.D, effects);
        if (n.List != null)
        {
            foreach (var x in n.List) Walk(x, effects);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) Walk(x, effects);
        }
    }

    private Symbol ImplicitCtor(TypeDeclSym t)
    {
        if (_implicitCtors.TryGetValue(t.Order, out var s)) return s;
        var c = new Symbol(SymKind.Ctor, ".ctor", TypeSym.VoidType);
        c.Owner = t;
        _implicitCtors[t.Order] = c;
        return c;
    }

    private void AddType(InitEffects effects, TypeDeclSym? t)
    {
        if (InProject(t)) effects.Types.Add(t!);
    }
}

// method の本文が直接触る型と呼ぶ method
public sealed class InitEffects
{
    public List<TypeDeclSym> Types = new List<TypeDeclSym>();
    public List<Symbol> Callees = new List<Symbol>();
}

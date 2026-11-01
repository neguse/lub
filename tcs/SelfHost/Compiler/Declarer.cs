using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// 型を解決する文脈 (宣言の中の位置)
public sealed class TypeScope
{
    public TypeDeclSym? Type;
    public string Namespace;
    public List<string> Usings;
    // Usings の各要素を書いた namespace ("" は compilation unit)。拡張メソッドを内側の scope から探すのに使う
    public List<string>? UsingLevels;
    public List<string>? MethodTypeParams;
    // method の型引数の where T : struct (MethodTypeParams と同じ順)
    public List<bool>? MethodTypeParamStruct;
    public SourceFile File;
    // using X = A.B;
    public Dictionary<string, string>? Aliases;

    public TypeScope(SourceFile file, string ns, List<string> usings)
    {
        File = file;
        Namespace = ns;
        Usings = usings;
    }

    public TypeScope WithType(TypeDeclSym t)
    {
        var s = new TypeScope(File, Namespace, Usings);
        s.UsingLevels = UsingLevels;
        s.Type = t;
        s.MethodTypeParams = MethodTypeParams;
        s.MethodTypeParamStruct = MethodTypeParamStruct;
        s.Aliases = Aliases;
        return s;
    }

    public TypeScope WithMethodTypeParams(List<string>? tps)
    {
        var s = new TypeScope(File, Namespace, Usings);
        s.UsingLevels = UsingLevels;
        s.Type = Type;
        s.MethodTypeParams = tps;
        s.Aliases = Aliases;
        return s;
    }

    public TypeScope WithMethodTypeParams(List<string>? tps, List<bool>? structs)
    {
        var s = WithMethodTypeParams(tps);
        s.MethodTypeParamStruct = structs;
        return s;
    }
}

// プログラム全体の宣言
public sealed class Compilation
{
    public List<SourceFile> Files = new List<SourceFile>();
    public List<Node> Units = new List<Node>();
    public Diagnostics Diags = new Diagnostics();
    public List<TypeDeclSym> Types = new List<TypeDeclSym>();
    public Dictionary<string, TypeDeclSym> TypesByFullName = new Dictionary<string, TypeDeclSym>();
    public HashSet<string> Namespaces = new HashSet<string>();
    // 型宣言ごとの型解決の文脈
    // static class の拡張メソッド (名前ごと)
    public Dictionary<string, List<Symbol>> Extensions = new Dictionary<string, List<Symbol>>();
    // 型の Lua の global 名 (局所変数の名前がこれを覆い隠さないようにする)
    public HashSet<string> TypeLuaNames = new HashSet<string>();

    public void Error(string code, string message, Node at)
    {
        Diags.Error(code, message, at.File, at.Pos);
    }
}

public sealed class Declarer
{
    private readonly Compilation _c;
    private Dictionary<string, string>? _aliases;

    public Declarer(Compilation c)
    {
        _c = c;
    }

    public void DeclareAll()
    {
        for (int i = 0; i < _c.Units.Count; i++)
        {
            var unit = _c.Units[i];
            var usings = new List<string>();
            var levels = new List<string>();
            CollectUsings(unit, usings, levels, "");
            _aliases = CollectAliases(unit, null);
            CollectMembers(unit, "", usings, levels, null);
        }
        foreach (var t in _c.Types) DeclareHeader(t);
        foreach (var t in _c.Types) DeclareMembers(t);
        foreach (var t in _c.Types) AssignLuaNames(t);
        CheckStructCycles();
    }

    public const string StaticUsingPrefix = "static:";

    // using static で取り込んだ型 (見つからない名前は飛ばす)
    public List<TypeDeclSym> StaticUsingTypes(TypeScope scope)
    {
        var result = new List<TypeDeclSym>();
        foreach (var u in scope.Usings)
        {
            if (!u.StartsWith(StaticUsingPrefix)) continue;
            string name = u.Replace(StaticUsingPrefix, "");
            if (_c.TypesByFullName.TryGetValue(name, out var t)) result.Add(t);
        }
        return result;
    }

    public static void CollectUsings(Node container, List<string> usings, List<string> levels, string level)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Using && n.D == null)
            {
                // using static は "static:" を付けて同じ列に置く (namespace の using とは一致しない)
                usings.Add(n.Value == 1 ? StaticUsingPrefix + n.Text : n.Text);
                levels.Add(level);
            }
        }
    }

    // using X = A.B; の別名 (Using の D に別名の Name)
    public Dictionary<string, string>? CollectAliases(Node container, Dictionary<string, string>? outer)
    {
        Dictionary<string, string>? result = outer;
        if (container.List == null) return result;
        bool copied = false;
        foreach (var n in container.List)
        {
            if (n.Kind != NodeKind.Using || n.D == null) continue;
            if (!copied)
            {
                copied = true;
                var copy = new Dictionary<string, string>();
                if (outer != null)
                {
                    foreach (var kv in outer) copy[kv.Key] = kv.Value;
                }
                result = copy;
            }
            result[n.D.Text] = n.Text;
        }
        return result;
    }

    private void AddNamespace(string ns)
    {
        if (ns == "") return;
        var parts = ns.Split('.');
        string acc = "";
        foreach (var p in parts)
        {
            acc = acc == "" ? p : acc + "." + p;
            _c.Namespaces.Add(acc);
        }
    }

    private void CollectMembers(Node container, string ns, List<string> usings, List<string> levels, TypeDeclSym? outer)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Namespace)
            {
                string inner = ns == "" ? n.Text : ns + "." + n.Text;
                AddNamespace(inner);
                var innerUsings = new List<string>();
                foreach (var u in usings) innerUsings.Add(u);
                var innerLevels = new List<string>();
                foreach (var l in levels) innerLevels.Add(l);
                CollectUsings(n, innerUsings, innerLevels, inner);
                var saveAliases = _aliases;
                _aliases = CollectAliases(n, _aliases);
                CollectMembers(n, inner, innerUsings, innerLevels, null);
                _aliases = saveAliases;
            }
            else if (n.Kind == NodeKind.TypeDecl && n.Text != "")
            {
                DeclareType(n, ns, usings, levels, outer);
            }
        }
    }

    private void DeclareType(Node n, string ns, List<string> usings, List<string> levels, TypeDeclSym? outer)
    {
        string full;
        if (outer != null) full = outer.FullName + "." + n.Text;
        else full = ns == "" ? n.Text : ns + "." + n.Text;
        // System.ValueTuple (TinySystem/ValueTuple.cs) は型引数の数ごとに別の型で、Lua の global も分ける (System_ValueTuple2 など)
        if (n.File.IsLibrary && full == "System.ValueTuple" && n.List2 != null) full = full + n.List2.Count;
        var t = new TypeDeclSym(n.Text, full, ns, n.Value, n);
        n.TypeDecl = t;
        t.Outer = outer;
        t.IsStatic = n.Has(Mods.Static);
        t.IsRef = n.File.IsRef;
        t.Order = _c.Types.Count;
        if (n.List2 != null)
        {
            foreach (var tp in n.List2) t.TypeParams.Add(tp.Text);
        }
        if (_c.TypesByFullName.ContainsKey(full))
        {
            if (n.Has(Mods.Partial)) _c.Error("TCS1001", "Unsupported syntax: partial type", n);
            // 名前が同じで型引数の数が違う型は C# では別の型だが、セルフホストは名前で引くので扱わない
            else if (_c.TypesByFullName[full].TypeParams.Count != t.TypeParams.Count) _c.Error("TCS1900", "SelfHostUnsupported(types with the same name and different arity)", n);
            else _c.Error("CS0101", "The namespace '" + ns + "' already contains a definition for '" + n.Text + "'", n);
            return;
        }
        if (n.Has(Mods.Partial)) _c.Error("TCS1001", "Unsupported syntax: partial type", n);
        _c.TypesByFullName[full] = t;
        _c.TypeLuaNames.Add(t.LuaName);
        _c.Types.Add(t);
        if (outer != null) outer.Nested.Add(t);
        var scope = new TypeScope(n.File, ns, usings);
        scope.UsingLevels = levels;
        scope.Type = t;
        scope.Aliases = _aliases;
        t.Scope = scope;
        if (n.D != null && n.D.List != null)
        {
            foreach (var a in n.D.List)
            {
                if (a.Text == "Flags" || a.Text == "System.Flags" || a.Text == "FlagsAttribute") t.IsFlags = true;
            }
        }
        // 入れ子の型
        if (n.List != null)
        {
            foreach (var m in n.List)
            {
                if (m.Kind == NodeKind.TypeDecl && m.Text != "") DeclareType(m, ns, usings, levels, t);
            }
        }
    }

    public TypeScope ScopeOf(TypeDeclSym t) => t.Scope!;

    private void DeclareHeader(TypeDeclSym t)
    {
        var scope = ScopeOf(t);
        var n = t.Decl;
        t.TypeParamConstraints = ResolveConstraints(n.List2, scope);
        if (t.IsEnum)
        {
            t.EnumBase = TypeSym.IntType;
            if (n.List3 != null && n.List3.Count > 0) t.EnumBase = ResolveType(n.List3[0], scope);
            return;
        }
        if (n.List3 != null)
        {
            foreach (var b in n.List3)
            {
                // --ref の属性の宣言 (: Attribute)。属性は名前の解決にだけ使い、Lua を出さない
                if (t.IsRef && b.Kind == NodeKind.TypeName && b.Text == "Attribute" && (b.A == null || b.A.Text == "System"))
                {
                    t.IsAttribute = true;
                    continue;
                }
                var bt = ResolveType(b, scope);
                if (t.IsRef && bt.Decl != null && !bt.Decl.IsInterface && !bt.Decl.IsEnum && t.BaseClass == null)
                {
                    // --ref の host の data class の継承 (参照実装も host の宣言は診断しない)
                    t.BaseClass = bt.Decl;
                    continue;
                }
                if (bt.Kind == TypeKind.Interface) t.Interfaces.Add(bt);
                // `: object` は基底を書かないのと同じ
                else if (bt.Kind == TypeKind.Object && !t.IsInterface) continue;
                else if (!bt.IsError)
                {
                    if (t.IsInterface) _c.Error("CS0527", "Type '" + bt.Display() + "' in interface list is not an interface", b);
                    else _c.Error("TCS1001", "Unsupported syntax: class inheritance (base type '" + bt.Display() + "')", b);
                }
            }
        }
    }

    // ---- 型の解決 ----

    public TypeSym ResolveType(Node n, TypeScope scope)
    {
        switch (n.Kind)
        {
            case NodeKind.PredefType:
                return Predefined(n.Text, n);
            case NodeKind.ArrayType:
                if (n.Value == 2) _c.Error("TCS1001", "Unsupported syntax: MultiDimensionalArray", n);
                return TypeSym.ArrayOf(ResolveType(n.A!, scope));
            case NodeKind.NullableType:
                {
                    var inner = ResolveType(n.A!, scope);
                    if (inner.IsValueType && inner.Kind != TypeKind.Nullable) return TypeSym.NullableOf(inner);
                    if (inner.Kind == TypeKind.TypeParam)
                    {
                        // where T : struct の T? は Nullable<T>、それ以外の T? は注釈だけ
                        if (inner.IsStructParam) return TypeSym.NullableOf(inner);
                        return inner;
                    }
                    return inner;
                }
            case NodeKind.TupleType:
                {
                    var t = new TypeSym(TypeKind.Tuple, "tuple");
                    t.Args = new List<TypeSym>();
                    t.Names = new List<string>();
                    foreach (var e in n.List!)
                    {
                        t.Args.Add(ResolveType(e.A!, scope));
                        t.Names.Add(e.Text);
                    }
                    return t;
                }
            case NodeKind.TypeName:
                return ResolveTypeName(n, scope);
        }
        _c.Error("CS0246", "The type or namespace name could not be found", n);
        return TypeSym.ErrorType;
    }

    public TypeSym Predefined(string name, Node at)
    {
        switch (name)
        {
            case "int": return TypeSym.IntType;
            case "float": return TypeSym.FloatType;
            case "bool": return TypeSym.BoolType;
            case "string": return TypeSym.StringType;
            case "char": return TypeSym.CharType;
            case "byte": return TypeSym.ByteType;
            case "void": return TypeSym.VoidType;
            case "object": return TypeSym.ObjectType;
            case "uint": return TypeSym.UIntType;
            case "short":
            case "sbyte":
            case "ushort":
                return TypeSym.ShortType;
            case "double":
                _c.Error("TCS1001", "Unsupported syntax: DoubleType (numbers are int and float)", at);
                return TypeSym.DoubleType;
            case "long":
            case "ulong":
                _c.Error("TCS1001", "Unsupported syntax: LongType (numbers are int and float)", at);
                return TypeSym.LongType;
            case "?":
                return TypeSym.ErrorType;
        }
        _c.Error("TCS1002", "unsupported API: type '" + name + "'", at);
        return TypeSym.ErrorType;
    }

    private List<TypeSym>? TypeArgs(Node n, TypeScope scope)
    {
        if (n.List == null) return null;
        var args = new List<TypeSym>();
        foreach (var a in n.List) args.Add(ResolveType(a, scope));
        return args;
    }

    private TypeSym ResolveTypeName(Node n, TypeScope scope)
    {
        var args = TypeArgs(n, scope);
        int arity = args == null ? 0 : args.Count;
        if (n.A != null)
        {
            // 修飾された名前: 外側が namespace か型
            string? qual = QualifiedText(n.A);
            if (qual != null)
            {
                TypeDeclSym? decl;
                if (IsRooted(n)) _c.TypesByFullName.TryGetValue(qual + "." + n.Text, out decl);
                else decl = FindTypeInNamespaceOrType(qual, n.Text, scope);
                if (decl != null) return Instantiate(decl, args, n);
                if (IsLibraryNamespace(qual))
                {
                    var bt = BuiltinType(n.Text, args, n, qual);
                    if (bt != null) return bt;
                }
            }
            var outerType = ResolveType(n.A, scope);
            if (outerType.Kind == TypeKind.Dict && (n.Text == "KeyCollection" || n.Text == "ValueCollection"))
            {
                var kc = new TypeSym(n.Text == "KeyCollection" ? TypeKind.KeyCollection : TypeKind.ValueCollection, n.Text);
                kc.Args = outerType.Args;
                return kc;
            }
            if (outerType.Decl != null)
            {
                foreach (var nested in outerType.Decl.Nested)
                {
                    if (nested.Name == n.Text) return Instantiate(nested, args, n);
                }
            }
            _c.Error("CS0246", "The type or namespace name '" + n.Text + "' could not be found", n);
            return TypeSym.ErrorType;
        }
        if (n.Value == 1)
        {
            if (_c.TypesByFullName.TryGetValue(n.Text, out var rootType) && rootType.Outer == null) return Instantiate(rootType, args, n);
            var rootBuiltin = BuiltinType(n.Text, args, n, null);
            if (rootBuiltin != null) return rootBuiltin;
            _c.Error("CS0400", "The type or namespace name '" + n.Text + "' could not be found in the global namespace", n);
            return TypeSym.ErrorType;
        }
        var found = LookupTypeByName(n.Text, arity, scope);
        if (found != null)
        {
            if (found.Kind == TypeKind.TypeParam) return found;
            if (found.Decl != null) return Instantiate(found.Decl, args, n);
            return found;
        }
        // TinySystem.Random だけを写す。using System だけで見える Random は System.Random
        if (n.Text == "Random" && arity == 0 && !scope.Usings.Contains("TinySystem") && scope.Usings.Contains("System"))
        {
            _c.Error("TCS1002", "unsupported API: System.Random (use TinySystem.Random)", n);
            return TypeSym.ErrorType;
        }
        var builtin = BuiltinType(n.Text, args, n, null);
        if (builtin != null) return builtin;
        _c.Error("CS0246", "The type or namespace name '" + n.Text + "' could not be found (are you missing a using directive or an assembly reference?)", n);
        return TypeSym.ErrorType;
    }

    // BCL と TinySystem の namespace (組み込みの型を修飾できる)
    public static bool IsLibraryNamespace(string ns)
    {
        return ns == "System" || ns.StartsWith("System.") || ns == "TinySystem" || ns.StartsWith("TinySystem.");
    }

    // global:: で始まる修飾された名前か
    private static bool IsRooted(Node n)
    {
        var cur = n;
        while (cur.A != null) cur = cur.A;
        return cur.Value == 1;
    }

    private string? QualifiedText(Node n)
    {
        if (n.Kind == NodeKind.TypeName && n.List == null)
        {
            if (n.A == null) return n.Text;
            var q = QualifiedText(n.A);
            if (q == null) return null;
            return q + "." + n.Text;
        }
        return null;
    }

    private TypeDeclSym? FindTypeInNamespaceOrType(string qual, string name, TypeScope scope)
    {
        // 修飾の先頭が using の別名なら、別名の先 (namespace か型) に置き換える
        if (scope.Aliases != null)
        {
            var parts = qual.Split('.');
            if (scope.Aliases.TryGetValue(parts[0], out var target))
            {
                parts[0] = target;
                qual = string.Join(".", parts);
            }
        }
        if (_c.TypesByFullName.TryGetValue(qual + "." + name, out var t)) return t;
        // 現在の namespace からの相対
        string ns = scope.Namespace;
        while (ns != "")
        {
            if (_c.TypesByFullName.TryGetValue(ns + "." + qual + "." + name, out var t2)) return t2;
            ns = ParentNamespace(ns);
        }
        return null;
    }

    public static string ParentNamespace(string ns)
    {
        var parts = ns.Split('.');
        if (parts.Length <= 1) return "";
        var keep = new List<string>();
        for (int i = 0; i < parts.Length - 1; i++) keep.Add(parts[i]);
        return string.Join(".", keep);
    }

    // 名前から型を探す: 型引数、外側の型の入れ子、namespace、using の順
    public TypeSym? LookupTypeByName(string name, int arity, TypeScope scope)
    {
        if (scope.MethodTypeParams != null)
        {
            for (int i = 0; i < scope.MethodTypeParams.Count; i++)
            {
                if (scope.MethodTypeParams[i] == name && arity == 0)
                {
                    var tp = new TypeSym(TypeKind.TypeParam, name);
                    tp.Index = i;
                    tp.IsMethodParam = true;
                    tp.IsStructParam = scope.MethodTypeParamStruct != null && i < scope.MethodTypeParamStruct.Count && scope.MethodTypeParamStruct[i];
                    return tp;
                }
            }
        }
        var cur = scope.Type;
        while (cur != null)
        {
            for (int i = 0; i < cur.TypeParams.Count; i++)
            {
                if (cur.TypeParams[i] == name && arity == 0)
                {
                    if (cur != scope.Type) break;
                    var tp = new TypeSym(TypeKind.TypeParam, name);
                    tp.Index = i;
                    tp.IsStructParam = cur.Decl.List2 != null && i < cur.Decl.List2.Count && (cur.Decl.List2[i].Value & 2) != 0;
                    return tp;
                }
            }
            if (cur.Name == name && cur.TypeParams.Count == arity) return TypeSym.OfDecl(cur, null);
            foreach (var nested in cur.Nested)
            {
                if (nested.Name == name && nested.TypeParams.Count == arity) return TypeSym.OfDecl(nested, null);
            }
            cur = cur.Outer;
        }
        if (scope.Aliases != null && arity == 0 && scope.Aliases.TryGetValue(name, out var target))
        {
            var at = ResolveAlias(target);
            if (at != null) return at;
        }
        string ns = scope.Namespace;
        while (true)
        {
            string full = ns == "" ? name : ns + "." + name;
            if (_c.TypesByFullName.TryGetValue(full, out var t) && t.Outer == null) return TypeSym.OfDecl(t, null);
            if (ns == "") break;
            ns = ParentNamespace(ns);
        }
        foreach (var u in scope.Usings)
        {
            if (_c.TypesByFullName.TryGetValue(u + "." + name, out var t) && t.Outer == null) return TypeSym.OfDecl(t, null);
        }
        // using static の型の入れ子の型
        foreach (var st in StaticUsingTypes(scope))
        {
            foreach (var nested in st.Nested)
            {
                if (nested.Name == name && nested.TypeParams.Count == arity) return TypeSym.OfDecl(nested, null);
            }
        }
        return null;
    }

    private TypeSym Instantiate(TypeDeclSym decl, List<TypeSym>? args, Node at)
    {
        int arity = args == null ? 0 : args.Count;
        if (arity != decl.TypeParams.Count)
        {
            _c.Error("CS0305", "Using the generic type '" + decl.Name + "' requires " + decl.TypeParams.Count + " type arguments", at);
            return TypeSym.ErrorType;
        }
        return TypeSym.OfDecl(decl, args);
    }

    // using の別名の先 (user の型か、BCL / TinySystem の型)
    public TypeSym? ResolveAlias(string target)
    {
        if (_c.TypesByFullName.TryGetValue(target, out var t)) return TypeSym.OfDecl(t, null);
        var parts = target.Split('.');
        string last = parts[parts.Length - 1];
        string qual = ParentNamespace(target);
        if (IsLibraryNamespace(qual))
        {
            var st = Builtins.StaticType(last);
            if (st != null) return st;
            var dummy = new Node(NodeKind.Name, _c.Files[0], 0);
            return BuiltinType(last, null, dummy, qual);
        }
        return null;
    }

    // 組み込みの型 (BCL と TinySystem)
    public TypeSym? BuiltinType(string name, List<TypeSym>? args, Node at, string? qual)
    {
        int arity = args == null ? 0 : args.Count;
        switch (name)
        {
            case "List":
                if (arity == 1) return TypeSym.ListOf(args![0]);
                break;
            case "Dictionary":
                if (arity == 2) return TypeSym.DictOf(args![0], args[1]);
                break;
            case "HashSet":
                if (arity == 1) return TypeSym.Generic(TypeKind.HashSet, "HashSet", args![0]);
                break;
            case "Queue":
                if (arity == 1) return TypeSym.Generic(TypeKind.Queue, "Queue", args![0]);
                break;
            case "KeyValuePair":
                if (arity == 2) return TypeSym.Generic2(TypeKind.KeyValuePair, "KeyValuePair", args![0], args[1]);
                break;
            case "Action":
                return TypeSym.Delegate(args == null ? new List<TypeSym>() : args, TypeSym.VoidType);
            case "Func":
                if (arity >= 1)
                {
                    var ps = new List<TypeSym>();
                    for (int i = 0; i < arity - 1; i++) ps.Add(args![i]);
                    return TypeSym.Delegate(ps, args![arity - 1]);
                }
                break;
            case "Comparison":
                if (arity == 1)
                {
                    var ps = new List<TypeSym>();
                    ps.Add(args![0]);
                    ps.Add(args[0]);
                    return TypeSym.Delegate(ps, TypeSym.IntType);
                }
                break;
            case "Predicate":
                if (arity == 1)
                {
                    var ps = new List<TypeSym>();
                    ps.Add(args![0]);
                    return TypeSym.Delegate(ps, TypeSym.BoolType);
                }
                break;
            case "Utf8Bytes":
                if (arity == 0) return TypeSym.Utf8BytesType;
                break;
            case "Rune":
                if (arity == 0) return TypeSym.RuneType;
                break;
            case "Random":
                if (arity == 0) return TypeSym.RandomType;
                break;
            case "String":
                if (arity == 0) return TypeSym.StringType;
                break;
            case "Object":
                if (arity == 0) return TypeSym.ObjectType;
                break;
            case "Int32":
                if (arity == 0) return TypeSym.IntType;
                break;
            case "Single":
                if (arity == 0) return TypeSym.FloatType;
                break;
            case "Boolean":
                if (arity == 0) return TypeSym.BoolType;
                break;
            case "Char":
                if (arity == 0) return TypeSym.CharType;
                break;
        }
        return null;
    }

    // ---- member の宣言 ----

    private void DeclareMembers(TypeDeclSym t)
    {
        var scope = ScopeOf(t);
        var n = t.Decl;
        if (t.IsEnum)
        {
            int next = 0;
            if (n.List != null)
            {
                foreach (var em in n.List)
                {
                    var s = new Symbol(SymKind.EnumMember, em.Text, TypeSym.OfDecl(t, null));
                    s.Owner = t;
                    s.IsStatic = true;
                    s.IsConst = true;
                    s.Decl = em;
                    em.Sym = s;
                    // 値は Binder が定数として畳む。初期化子が無ければ前の値 + 1
                    s.ConstState = em.A == null ? 0 : -1;
                    if (em.A == null) s.ConstVal = null;
                    em.Value = next;
                    t.AddMember(s);
                }
            }
            return;
        }
        if (t.IsRecord && n.A != null)
        {
            t.RecordParams = new List<ParamSym>();
            var positional = n.A.List != null ? n.A.List : new List<Node>();
            foreach (var p in positional)
            {
                var pt = ResolveType(p.A!, scope);
                var ps = new ParamSym(p.Text, pt);
                ps.Default = p.B;
                t.RecordParams.Add(ps);
                var prop = new Symbol(SymKind.Property, p.Text, pt);
                prop.Owner = t;
                prop.HasGetter = true;
                // record struct (readonly でない) の positional property は書き換えられる
                prop.HasSetter = t.DeclKind == DeclKinds.RecordStruct && !n.Has(Mods.Readonly);
                prop.IsInit = true;
                prop.IsAuto = true;
                prop.Decl = p;
                t.AddMember(prop);
            }
            var ctor = new Symbol(SymKind.Ctor, ".ctor", TypeSym.VoidType);
            ctor.Owner = t;
            ctor.Params = t.RecordParams;
            ctor.Decl = n.A;
            t.Ctors.Add(ctor);
        }
        if (n.List == null) return;
        foreach (var m in n.List)
        {
            switch (m.Kind)
            {
                case NodeKind.Field:
                    {
                        var ft = ResolveType(m.A!, scope);
                        foreach (var v in m.List!)
                        {
                            var s = new Symbol(SymKind.Field, v.Text, ft);
                            s.Owner = t;
                            s.IsStatic = m.Has(Mods.Static) || m.Has(Mods.Const);
                            s.IsConst = m.Has(Mods.Const);
                            s.IsReadonly = m.Has(Mods.Readonly) || s.IsConst;
                            s.Decl = v;
                            v.Sym = s;
                            if (s.IsConst) s.ConstState = -1;
                            CheckDuplicate(t, s, v);
                            t.AddMember(s);
                        }
                        break;
                    }
                case NodeKind.Property:
                    {
                        var pt = ResolveType(m.A!, scope);
                        var s = new Symbol(SymKind.Property, m.Text, pt);
                        s.Owner = t;
                        s.IsStatic = m.Has(Mods.Static);
                        s.Decl = m;
                        m.Sym = s;
                        s.HasGetter = m.B != null;
                        s.HasSetter = m.C != null && m.C.Text != "init";
                        s.IsInit = m.C != null && m.C.Text == "init";
                        bool getAuto = m.B != null && m.B.A == null && m.B.B == null;
                        bool setAuto = m.C == null || m.C.A == null && m.C.B == null;
                        s.IsAuto = getAuto && setAuto && !t.IsInterface;
                        if (t.IsInterface) s.IsAuto = false;
                        CheckDuplicate(t, s, m);
                        t.AddMember(s);
                        break;
                    }
                case NodeKind.Method:
                    {
                        var s = new Symbol(SymKind.Method, m.Text, TypeSym.VoidType);
                        s.Owner = t;
                        s.IsStatic = m.Has(Mods.Static);
                        s.Decl = m;
                        m.Sym = s;
                        if (m.List2 != null)
                        {
                            s.TypeParams = new List<string>();
                            foreach (var tp in m.List2) s.TypeParams.Add(tp.Text);
                        }
                        List<bool>? structFlags = null;
                        if (m.List2 != null)
                        {
                            structFlags = new List<bool>();
                            foreach (var tpn in m.List2) structFlags.Add((tpn.Value & 2) != 0);
                        }
                        var mscope = scope.WithMethodTypeParams(s.TypeParams, structFlags);
                        if (s.TypeParams != null) s.TypeParamConstraints = ResolveConstraints(m.List2, mscope);
                        s.Type = ResolveType(m.A!, mscope);
                        s.Params = Params(m, mscope);
                        if (s.Params.Count > 0 && s.Params[0].IsThis)
                        {
                            s.IsExtension = true;
                            if (!_c.Extensions.TryGetValue(s.Name, out var list))
                            {
                                list = new List<Symbol>();
                                _c.Extensions[s.Name] = list;
                            }
                            list.Add(s);
                        }
                        // --ref の host の宣言は診断しない (参照実装と同じ)
                        if (m.Has(Mods.Override) && m.Text != "ToString" && !t.IsRef)
                            _c.Error("TCS1001", "Unsupported syntax: OverrideMember(" + m.Text + ")", m);
                        if ((m.Has(Mods.Abstract) || m.Has(Mods.Virtual)) && !t.IsRef)
                            _c.Error("TCS1001", "Unsupported syntax: virtual / abstract member (class inheritance)", m);
                        var sameName = t.Lookup(s.Name);
                        if (sameName != null)
                        {
                            foreach (var other in sameName)
                            {
                                if (other.Kind == SymKind.Method && SameParams(other, s))
                                {
                                    _c.Error("CS0111", "Type '" + t.Name + "' already defines a member called '" + s.Name + "' with the same parameter types", m);
                                    break;
                                }
                            }
                        }
                        t.AddMember(s);
                        break;
                    }
                case NodeKind.Ctor:
                    {
                        var s = new Symbol(SymKind.Ctor, ".ctor", TypeSym.VoidType);
                        s.Owner = t;
                        s.IsStatic = m.Has(Mods.Static);
                        s.Decl = m;
                        m.Sym = s;
                        s.Params = Params(m, scope);
                        if (s.IsStatic)
                        {
                            _c.Error("TCS1001", "Unsupported syntax: static constructor", m);
                            break;
                        }
                        foreach (var other in t.Ctors)
                        {
                            if (SameParams(other, s))
                            {
                                _c.Error("CS0111", "Type '" + t.Name + "' already defines a member called '" + t.Name + "' with the same parameter types", m);
                                break;
                            }
                        }
                        t.Ctors.Add(s);
                        break;
                    }
                case NodeKind.OperatorDecl:
                    {
                        var s = new Symbol(SymKind.Method, "op_" + OperatorName(m.Text, m.Count), TypeSym.VoidType);
                        s.Owner = t;
                        s.IsStatic = true;
                        s.Decl = m;
                        m.Sym = s;
                        s.Type = ResolveType(m.A!, scope);
                        s.Params = Params(m, scope);
                        t.AddMember(s);
                        break;
                    }
            }
        }
    }

    private List<List<TypeSym>> ResolveConstraints(List<Node>? typeParams, TypeScope scope)
    {
        var result = new List<List<TypeSym>>();
        if (typeParams == null) return result;
        foreach (var tp in typeParams)
        {
            var list = new List<TypeSym>();
            if (tp.List != null)
            {
                foreach (var c in tp.List) list.Add(ResolveType(c, scope));
            }
            result.Add(list);
        }
        return result;
    }

    public static string OperatorName(string op, int arity)
    {
        switch (op)
        {
            case "+": return arity == 1 ? "UnaryPlus" : "Addition";
            case "-": return arity == 1 ? "UnaryNegation" : "Subtraction";
            case "*": return "Multiply";
            case "/": return "Division";
            case "%": return "Modulus";
            case "==": return "Equality";
            case "!=": return "Inequality";
            case "<": return "LessThan";
            case ">": return "GreaterThan";
            case "<=": return "LessThanOrEqual";
            case ">=": return "GreaterThanOrEqual";
            case "!": return "LogicalNot";
            case "~": return "OnesComplement";
            case "&": return "BitwiseAnd";
            case "|": return "BitwiseOr";
            case "^": return "ExclusiveOr";
            case "<<": return "LeftShift";
            case ">>": return "RightShift";
            case "++": return "Increment";
            case "--": return "Decrement";
            case "true": return "True";
            case "false": return "False";
        }
        return "Unknown";
    }

    // struct の instance の field (auto property の裏の field を含む) が自分の struct を含むと、値の大きさが決まらない (CS0523)
    private void CheckStructCycles()
    {
        foreach (var t in _c.Types)
        {
            if (!t.IsStruct) continue;
            foreach (var m in t.Members)
            {
                if (!IsInstanceStorage(m)) continue;
                var ft = StructOf(m.Type);
                if (ft != null && ReachesStruct(ft, t, new HashSet<TypeDeclSym>()))
                {
                    _c.Error("CS0523", "Struct member '" + t.Name + "." + m.Name + "' of type '" + m.Type.Display() + "' causes a cycle in the struct layout", m.Decl!);
                    break;
                }
            }
        }
    }

    private static bool IsInstanceStorage(Symbol m) =>
        !m.IsStatic && !m.IsConst && (m.Kind == SymKind.Field || m.Kind == SymKind.Property && m.IsAuto);

    private static TypeDeclSym? StructOf(TypeSym t)
    {
        if (t.Kind == TypeKind.Nullable) return StructOf(t.Elem);
        if (t.Decl != null && t.Decl.IsStruct) return t.Decl;
        return null;
    }

    private static bool ReachesStruct(TypeDeclSym from, TypeDeclSym target, HashSet<TypeDeclSym> visited)
    {
        if (from == target) return true;
        if (!visited.Add(from)) return false;
        foreach (var m in from.Members)
        {
            if (!IsInstanceStorage(m)) continue;
            var ft = StructOf(m.Type);
            if (ft != null && ReachesStruct(ft, target, visited)) return true;
        }
        return false;
    }

    // 引数の型の並び (と method の型引数の数) が同じか
    private static bool SameParams(Symbol a, Symbol b)
    {
        int na = a.Params == null ? 0 : a.Params.Count;
        int nb = b.Params == null ? 0 : b.Params.Count;
        if (na != nb) return false;
        int ta = a.TypeParams == null ? 0 : a.TypeParams.Count;
        int tb = b.TypeParams == null ? 0 : b.TypeParams.Count;
        if (ta != tb) return false;
        for (int i = 0; i < na; i++)
        {
            if (!TypeSym.Same(a.Params![i].Type, b.Params![i].Type)) return false;
            if (a.Params[i].IsOut != b.Params[i].IsOut) return false;
        }
        return true;
    }

    private void CheckDuplicate(TypeDeclSym t, Symbol s, Node at)
    {
        var existing = t.Lookup(s.Name);
        if (existing != null)
            _c.Error("CS0102", "The type '" + t.Name + "' already contains a definition for '" + s.Name + "'", at);
    }

    private List<ParamSym> Params(Node m, TypeScope scope)
    {
        var result = new List<ParamSym>();
        if (m.List == null) return result;
        foreach (var p in m.List)
        {
            var pt = ResolveType(p.A!, scope);
            var ps = new ParamSym(p.Text, pt);
            ps.IsOut = p.Has(Mods.Out);
            ps.IsThis = p.Has(Mods.This);
            ps.Default = p.B;
            if (p.Has(Mods.Ref) || p.Has(Mods.In)) _c.Error("TCS1001", "Unsupported syntax: ref / in parameter", p);
            if (p.Has(Mods.Params)) _c.Error("TCS1001", "Unsupported syntax: params", p);
            result.Add(ps);
        }
        return result;
    }

    // ---- Lua の名前 ----

    private void AssignLuaNames(TypeDeclSym t)
    {
        // 同じ名前の method が複数あれば signature で区別する
        foreach (var m in t.Members)
        {
            if (m.Kind != SymKind.Method) continue;
            var same = t.Lookup(m.Name)!;
            int methods = 0;
            foreach (var o in same)
            {
                if (o.Kind == SymKind.Method) methods++;
            }
            m.LuaName = methods > 1 ? m.Name + "__" + Signature(m) : LuaNames.Member(m.Name);
        }
        // constructor (struct の new は既定値を作る関数なので、constructor は ctor)
        string ctorBase = t.IsStruct ? "ctor" : "new";
        if (t.Ctors.Count == 1) t.Ctors[0].LuaName = ctorBase;
        else
        {
            foreach (var c in t.Ctors) c.LuaName = ctorBase + "__" + Signature(c);
        }
        if (t.IsStruct)
        {
            bool immutable = t.Decl.Has(Mods.Readonly);
            if (!immutable)
            {
                immutable = true;
                foreach (var m in t.Members)
                {
                    if (m.IsStatic) continue;
                    if (m.Kind == SymKind.Field && !m.IsReadonly) immutable = false;
                    if (m.Kind == SymKind.Property && m.IsAuto && m.HasSetter) immutable = false;
                }
            }
            t.IsImmutable = immutable;
        }
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Field || m.Kind == SymKind.Property || m.Kind == SymKind.EnumMember) m.LuaName = m.Name;
        }
        if (t.IsRef) AssignHostNames(t);
    }

    // --ref の型 (host) の名前は host の規則で写す (参照実装の LuaNaming と同じ):
    // 型は namespace と入れ子の型を全小文字で . でつなぐ (Lub.Gfx → lub.gfx)。enum は入っている型か namespace の下に平らに置く。
    // member は snake_case (BeginPass → begin_pass)、enum の member は UPPER_SNAKE (Rgba8 → RGBA8)
    private static void AssignHostNames(TypeDeclSym t)
    {
        t.LuaName = HostPath(t);
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.EnumMember) m.LuaName = LuaNames.HostConst(m.Name);
            else m.LuaName = LuaNames.HostMember(m.Name);
        }
    }

    private static string HostPath(TypeDeclSym t)
    {
        var parts = new List<string>();
        TypeDeclSym? cur = t.IsEnum ? t.Outer : t;
        if (t.IsEnum && cur == null)
        {
            // namespace の直下の enum は namespace の下に置く
            if (t.Namespace == "") return "";
        }
        while (cur != null)
        {
            parts.Insert(0, LuaNames.HostPathSegment(cur.Name));
            cur = cur.Outer;
        }
        if (t.Namespace != "")
        {
            var ns = t.Namespace.Split('.');
            for (int i = ns.Length - 1; i >= 0; i--) parts.Insert(0, LuaNames.HostPathSegment(ns[i]));
        }
        return string.Join(".", parts);
    }

    public static string Signature(Symbol m)
    {
        var parts = new List<string>();
        if (m.Params != null)
        {
            foreach (var p in m.Params) parts.Add(SigName(p.Type));
        }
        if (parts.Count == 0) return "0";
        return string.Join("__", parts);
    }

    private static string SigName(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.Array: return SigName(t.Elem) + "Array";
            case TypeKind.Nullable: return SigName(t.Elem) + "N";
            case TypeKind.Delegate:
                {
                    var parts = new List<string>();
                    parts.Add(t.Ret!.Kind == TypeKind.Void ? "Action" : "Func");
                    foreach (var a in t.Args!) parts.Add(SigName(a));
                    if (t.Ret.Kind != TypeKind.Void) parts.Add(SigName(t.Ret));
                    return string.Join("_", parts);
                }
        }
        string name = t.Decl != null ? t.Decl.LuaName : t.Name;
        if (t.Args != null && t.Args.Count > 0)
        {
            var parts = new List<string>();
            parts.Add(name);
            foreach (var a in t.Args) parts.Add(SigName(a));
            return string.Join("_", parts);
        }
        return name;
    }
}

public static class LuaNames
{
    // ---- host (--ref) の名前 (参照実装の Shared/LuaNaming.cs と同じ規則) ----

    public static string HostMember(string name)
    {
        string snake = ToSnake(name);
        return IsLuaKeyword(snake) ? snake + "_" : snake;
    }

    public static string HostConst(string name) => ToSnake(name).ToUpper();

    public static string HostPathSegment(string name)
    {
        string lower = name.ToLower();
        return IsLuaKeyword(lower) ? lower + "_" : lower;
    }

    // 小文字を含まない名前 (CLEAR / DEPTH24_STENCIL8) は既に全大文字の snake_case とみなして小文字にする。
    // それ以外は先頭の _ を残し、2 文字目以降の大文字の前に _ を入れて小文字にする
    private static string ToSnake(string name)
    {
        var b = new Utf8Bytes(name);
        bool anyLower = false;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] >= 'a' && b[i] <= 'z') anyLower = true;
        }
        if (!anyLower) return name.ToLower();
        var parts = new List<string>();
        int k = 0;
        while (k < b.Length && b[k] == '_')
        {
            parts.Add("_");
            k++;
        }
        bool first = true;
        int run = k;
        for (; k < b.Length; k++)
        {
            int c = b[k];
            if (c >= 'A' && c <= 'Z')
            {
                if (k > run) parts.Add(b.Slice(run, k - run).ToString());
                if (!first) parts.Add("_");
                parts.Add(b.Slice(k, 1).ToString().ToLower());
                run = k + 1;
            }
            first = false;
        }
        if (k > run) parts.Add(b.Slice(run, k - run).ToString());
        return string.Join("", parts);
    }

    private static HashSet<string>? _reserved;

    // Lua の予約語と、生成コードが参照する global の名前
    public static bool IsReserved(string name)
    {
        if (_reserved == null)
        {
            var r = new HashSet<string>();
            string all = "and break do else elseif end false for function goto if in local nil not or repeat return then true until while self print tostring tonumber type setmetatable getmetatable rawget rawset rawequal rawlen string table math error select pairs ipairs next require pcall assert utf8 os io coroutine load dofile collectgarbage _ENV _G List Dict String Math Random Char TcsQueue TcsHashSet TinySystem tcs_math tcs_utf8 tcs_rt";
            foreach (var w in all.Split(' ')) r.Add(w);
            _reserved = r;
        }
        return _reserved.Contains(name);
    }

    public static bool IsLuaKeyword(string name)
    {
        switch (name)
        {
            case "and":
            case "break":
            case "do":
            case "else":
            case "elseif":
            case "end":
            case "false":
            case "for":
            case "function":
            case "global":
            case "goto":
            case "if":
            case "in":
            case "local":
            case "nil":
            case "not":
            case "or":
            case "repeat":
            case "return":
            case "then":
            case "true":
            case "until":
            case "while":
                return true;
        }
        return false;
    }

    public static string Local(string name)
    {
        if (IsReserved(name)) return name + "_";
        return name;
    }

    public static string Member(string name)
    {
        if (IsLuaKeyword(name)) return name + "_";
        return name;
    }
}

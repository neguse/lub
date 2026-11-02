using System.Collections.Generic;

namespace TcsSelf;

// C backend のための generic の単相化 (参照実装の IlSpecialization)。閉じた generic の型 (`Box<int>`、タプル) と
// generic な method の閉じた呼び出しごとに、型引数を置き換えた copy (`TcsClosed0_Box`) を作り、参照をその名前に書き換える。
// copy の中に新しい閉じた型が現れるので、増えなくなるまで束縛し直す (最大 128 回)。最後に generic の雛形を消す。
// 書き換えは束縛していない木 (B) の上で行う。各回は B の複製を束縛し、複製の node から元の node へ (Orig) 束縛の結果を写す (Mirror)
public sealed class Specializer
{
    private int _appended;
    private readonly List<SourceFile> _files;
    // 束縛していない木 (ファイルごと)。書き換えはこの木に行う
    private readonly List<Node> _units;
    // 閉じた型と method の key → 閉じた名前
    private readonly Dictionary<string, string> _names = new Dictionary<string, string>();

    // 閉じた interface の generic method (interface の key、method の名前、型引数の key、閉じた名前)
    private readonly List<string> _ifaceKeys = new List<string>();
    private readonly List<string> _ifaceMethods = new List<string>();
    private readonly List<string> _ifaceArgs = new List<string>();
    private readonly List<string> _ifaceNames = new List<string>();
    private Compilation? _bound;

    public string Error = "";

    public Specializer(List<SourceFile> files, List<Node> units)
    {
        _files = files;
        _units = units;
    }

    // 単相化した compilation (束縛済み)。失敗なら null (Error に理由)
    public Compilation? Run()
    {
        for (int round = 0; round < 128; round++)
        {
            Bind();
            var instances = new List<TypeSym>();
            var sources = new List<Node>();
            var methods = new List<Node>();
            var methodSubst = new List<List<TypeSym>>();
            var methodNames = new List<string>();
            foreach (int i in EmittedOrder())
            {
                CollectTypes(_units[i], instances, sources);
                if (Error != "") return null;
                CollectMethods(_units[i], methods, methodSubst, methodNames);
                if (Error != "") return null;
            }
            foreach (int i in EmittedOrder()) CollectInterfaceImplementations(_units[i], methods, methodSubst, methodNames);
            if (Error != "") return null;
            if (instances.Count == 0 && methods.Count == 0)
            {
                foreach (int i in EmittedOrder())
                {
                    _units[i] = Rewrite(_units[i], null, null, null)!;
                    RemoveTemplates(_units[i]);
                }
                Bind();
                return _bound;
            }
            // 閉じた copy (入れ子の型は外側の型に、それ以外はファイルの終わりに足す)
            var ownerNodes = new List<Node>();
            var ownerMembers = new List<List<Node>>();
            var fileAdds = new List<List<Node>>();
            foreach (var u in _units) fileAdds.Add(new List<Node>());
            for (int k = 0; k < instances.Count; k++)
            {
                var type = instances[k];
                var source = sources[k];
                var closedName = _names[Key(type)];
                var copy = Rewrite(source, TypeParamNames(source), type.Args, null)!;
                copy.Text = closedName;
                copy.List2 = null;
                if (copy.List != null)
                {
                    for (int m = 0; m < copy.List.Count; m++)
                    {
                        var member = copy.List[m];
                        if (member.Kind == NodeKind.Ctor) member.Text = closedName;
                        // generic な型の overload は閉じた copy で parameter の型が重なりうるので IL の名前にする
                        if (member.Kind == NodeKind.Method && source.List != null && m < source.List.Count)
                        {
                            var overload = OverloadName(source.List[m].Mirror != null ? source.List[m].Mirror!.Sym : null);
                            if (overload != null) member.Text = overload;
                        }
                    }
                }
                copy.Appended = ++_appended;
                var outer = OuterTypeDecl(source);
                if (outer != null) AddMember(ownerNodes, ownerMembers, outer, copy);
                else fileAdds[FileIndex(source)].Add(copy);
            }
            for (int k = 0; k < methods.Count; k++)
            {
                var source = methods[k];
                var copy = Rewrite(source, null, null, null, methodSubst[k], TypeParamNames(source))!;
                copy.Text = methodNames[k];
                copy.List2 = null;
                copy.Appended = ++_appended;
                var owner = OuterTypeDecl(source);
                if (owner != null) AddMember(ownerNodes, ownerMembers, owner, copy);
            }
            // copy を雛形の木に足してから、木全体の参照を書き換える (copy の node は雛形の束縛の結果を持つので、もう一度通しても変わらない)
            for (int k = 0; k < ownerNodes.Count; k++)
            {
                foreach (var m in ownerMembers[k]) ownerNodes[k].Add(m);
            }
            for (int i = 0; i < _units.Count; i++)
            {
                foreach (var add in fileAdds[i]) _units[i].Add(add);
                if (_files[i].IsRef) continue;
                _units[i] = Rewrite(_units[i], null, null, null)!;
            }
        }
        Error = "C generic specialization exceeds 128 expansion rounds";
        return null;
    }

    private static void AddMember(List<Node> owners, List<List<Node>> members, Node owner, Node member)
    {
        for (int i = 0; i < owners.Count; i++)
        {
            if (owners[i] == owner)
            {
                members[i].Add(member);
                return;
            }
        }
        owners.Add(owner);
        var list = new List<Node>();
        list.Add(member);
        members.Add(list);
    }

    // library を先に、次に user のファイル (Lua の出力と同じ順)
    private List<int> EmittedOrder()
    {
        var r = new List<int>();
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].IsLibrary) r.Add(i);
        }
        for (int i = 0; i < _files.Count; i++)
        {
            if (!_files[i].IsLibrary && !_files[i].IsRef) r.Add(i);
        }
        return r;
    }

    private int FileIndex(Node n)
    {
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i] == n.File) return i;
        }
        return 0;
    }

    // ---- 束縛 ----

    private void Bind()
    {
        foreach (var u in _units) ClearMirror(u);
        var c = new Compilation();
        for (int i = 0; i < _units.Count; i++)
        {
            _files[i].Id = i;
            c.Files.Add(_files[i]);
            c.Units.Add(_units[i].CloneTree());
        }
        var d = new Declarer(c);
        d.DeclareAll();
        new Binder(c, d).BindAll();
        foreach (var u in c.Units) SetMirror(u);
        _bound = c;
        _deconstructed = new List<Node>();
        foreach (var u in _units) CollectDeconstruction(u, _deconstructed);
    }

    private static void ClearMirror(Node n)
    {
        n.Mirror = null;
        if (n.A != null) ClearMirror(n.A);
        if (n.B != null) ClearMirror(n.B);
        if (n.C != null) ClearMirror(n.C);
        if (n.D != null) ClearMirror(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) ClearMirror(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) ClearMirror(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) ClearMirror(x);
        }
    }

    private static void SetMirror(Node n)
    {
        if (n.Orig != null && n.Orig.Mirror == null) n.Orig.Mirror = n;
        if (n.A != null) SetMirror(n.A);
        if (n.B != null) SetMirror(n.B);
        if (n.C != null) SetMirror(n.C);
        if (n.D != null) SetMirror(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) SetMirror(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) SetMirror(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) SetMirror(x);
        }
    }

    // ---- 閉じた型と method を集める ----

    // ファイルの中の位置の順 (同じ位置は外側が先)
    // 木を Roslyn の DescendantNodes と同じ順 (元の位置の pre-order) に並べる。
    // node の slot は位置の順でないので兄弟を位置で並べ替え、単相化が足した copy は足した順に後ろへ置く
    private static void Ordered(Node n, int depth, Node? parent, List<Node> nodes, List<int> depths, List<Node?> parents)
    {
        nodes.Add(n);
        depths.Add(depth);
        parents.Add(parent);
        var children = new List<Node>();
        if (n.A != null) children.Add(n.A);
        if (n.B != null) children.Add(n.B);
        if (n.C != null) children.Add(n.C);
        if (n.D != null) children.Add(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) children.Add(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) children.Add(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) children.Add(x);
        }
        for (int i = 1; i < children.Count; i++)
        {
            var x = children[i];
            int j = i - 1;
            while (j >= 0 && Later(children[j], x))
            {
                children[j + 1] = children[j];
                j--;
            }
            children[j + 1] = x;
        }
        foreach (var c in children) Ordered(c, depth + 1, n, nodes, depths, parents);
    }

    private static bool Later(Node a, Node b)
    {
        if (a.Appended != b.Appended) return b.Appended == 0 || a.Appended != 0 && a.Appended > b.Appended;
        return a.Pos > b.Pos;
    }

    private void CollectTypes(Node unit, List<TypeSym> instances, List<Node> sources)
    {
        var nodes = new List<Node>();
        var depths = new List<int>();
        var parents = new List<Node?>();
        Ordered(unit, 0, null, nodes, depths, parents);
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var type = ClosedTypeOf(n, _deconstructed);
            if (type == null || ContainsParameter(type) || _names.ContainsKey(Key(type))) continue;
            var source = TemplateOf(type);
            if (source == null) continue;
            var decl = DeclOf(type)!;
            if (type.Kind != TypeKind.Tuple && !IsTopLevelOrNested(source))
            {
                Error = "C specialization requires top-level or nested generic types";
                return;
            }
            if (source.Has(Mods.Partial))
            {
                Error = "C specialization does not support partial generic types";
                return;
            }
            if (_names.Count >= 256)
            {
                Error = "C generic specialization exceeds 256 closed classes";
                return;
            }
            var name = "TcsClosed" + _names.Count + "_" + decl.Name;
            if (_bound!.TypesByFullName.ContainsKey(name))
            {
                Error = "C specialization name conflicts with " + name;
                return;
            }
            _names[Key(type)] = name;
            instances.Add(type);
            sources.Add(source);
        }
    }

    // 閉じた copy を作る候補の型。タプルの literal は変換先の型 (T? なら中の型) で決まる
    private static TypeSym? ClosedTypeOf(Node n, List<Node> deconstructed)
    {
        var m = n.Mirror;
        if (m == null) return null;
        if ((n.Kind == NodeKind.TypeName || n.Kind == NodeKind.Name || n.Kind == NodeKind.Member) && n.List != null)
        {
            if (n.Kind != NodeKind.TypeName && m.Info != RefKind.Type) return null;
            var t = m.Type;
            if (t != null && t.Decl != null && t.Decl.TypeParams.Count > 0 && t.Args != null && t.Args.Count > 0) return t;
            return null;
        }
        if (n.Kind == NodeKind.TupleType && m.Type != null && m.Type.Kind == TypeKind.Tuple) return m.Type;
        if (n.Kind == NodeKind.Tuple && !deconstructed.Contains(n) && m.Type != null)
        {
            var t = IlTypes.UnwrapNullable(m.Type);
            if (t.Kind == TypeKind.Tuple) return t;
        }
        return null;
    }

    // 分解の右辺の literal とその中のタプルの literal は要素ごとに代入するのでタプルの値を作らない
    private static void CollectDeconstruction(Node n, List<Node> r)
    {
        if (n.Kind == NodeKind.DeconstructAssign && n.A != null && n.A.Kind == NodeKind.Tuple) AddNestedTuples(n.A, r);
        if (n.A != null) CollectDeconstruction(n.A, r);
        if (n.B != null) CollectDeconstruction(n.B, r);
        if (n.C != null) CollectDeconstruction(n.C, r);
        if (n.D != null) CollectDeconstruction(n.D, r);
        if (n.List != null)
        {
            foreach (var x in n.List) CollectDeconstruction(x, r);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) CollectDeconstruction(x, r);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) CollectDeconstruction(x, r);
        }
    }

    private static void AddNestedTuples(Node t, List<Node> r)
    {
        r.Add(t);
        if (t.List == null) return;
        foreach (var e in t.List)
        {
            if (e.Kind == NodeKind.Tuple) AddNestedTuples(e, r);
        }
    }

    private TypeDeclSym? DeclOf(TypeSym t)
    {
        if (t.Kind == TypeKind.Tuple)
        {
            TypeDeclSym? vt;
            if (_bound!.TypesByFullName.TryGetValue("System.ValueTuple" + t.Args!.Count, out vt)) return vt;
            return null;
        }
        return t.Decl;
    }

    // 閉じた型の雛形の宣言 (束縛していない木の node)
    private Node? TemplateOf(TypeSym t)
    {
        var d = DeclOf(t);
        if (d == null || d.TypeParams.Count == 0) return null;
        return d.Decl.Orig;
    }

    private bool IsTopLevelOrNested(Node source)
    {
        foreach (var u in _units)
        {
            if (u.List != null && u.List.Contains(source)) return true;
        }
        return OuterTypeDecl(source) != null;
    }

    // node を member に持つ型の宣言 (束縛していない木)
    private Node? OuterTypeDecl(Node member)
    {
        foreach (var u in _units)
        {
            var r = FindOwner(u, member);
            if (r != null) return r;
        }
        return null;
    }

    private static Node? FindOwner(Node container, Node member)
    {
        if (container.List == null) return null;
        foreach (var x in container.List)
        {
            if (x == member) return container.Kind == NodeKind.TypeDecl ? container : null;
            if (x.Kind == NodeKind.TypeDecl || x.Kind == NodeKind.Namespace)
            {
                var r = FindOwner(x, member);
                if (r != null) return r;
            }
        }
        return null;
    }

    private void CollectMethods(Node unit, List<Node> methods, List<List<TypeSym>> substs, List<string> names)
    {
        var nodes = new List<Node>();
        var depths = new List<int>();
        var parents = new List<Node?>();
        Ordered(unit, 0, null, nodes, depths, parents);
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var m = n.Mirror;
            if (m == null) continue;
            bool call = n.Kind == NodeKind.Call;
            bool group = (n.Kind == NodeKind.Name || n.Kind == NodeKind.Member) && !IsCallee(n, parents[i]);
            if (!call && !group) continue;
            var called = m.Sym;
            if (called == null || called.Kind != SymKind.Method || called.TypeParams == null || m.TypeArgs == null) continue;
            var method = InterfaceMethod(called);
            var owner = method.Owner;
            if (owner == null || ContainsAnyParameter(m.TypeArgs) || owner.TypeParams.Count > 0) continue;
            var key = MethodKey(owner.SelfType(), method, m.TypeArgs);
            if (_names.ContainsKey(key)) continue;
            var source = method.Decl != null ? method.Decl.Orig : null;
            if (source == null || source.Kind != NodeKind.Method) continue;
            if (_names.Count >= 256)
            {
                Error = "C generic specialization exceeds 256 closed classes";
                return;
            }
            var name = "TcsClosed" + _names.Count + "_" + method.Name;
            _names[key] = name;
            methods.Add(source);
            substs.Add(m.TypeArgs);
            names.Add(name);
            if (owner.IsInterface)
            {
                _ifaceKeys.Add(Key(owner.SelfType()));
                _ifaceMethods.Add(LuaEmit.MethodName(method));
                _ifaceArgs.Add(ArgsKey(m.TypeArgs));
                _ifaceNames.Add(name);
                _ifaceTypeArgs.Add(m.TypeArgs);
            }
        }
    }

    private readonly List<List<TypeSym>> _ifaceTypeArgs = new List<List<TypeSym>>();

    // 閉じた interface の generic method を、実装する型に同じ名前で作る
    private void CollectInterfaceImplementations(Node unit, List<Node> methods, List<List<TypeSym>> substs, List<string> names)
    {
        var decls = new List<Node>();
        CollectTypeDecls(unit, decls);
        foreach (var decl in decls)
        {
            var bound = decl.Mirror;
            if (bound == null || bound.TypeDecl == null) continue;
            var type = bound.TypeDecl;
            if (type.TypeParams.Count > 0 || type.IsInterface) continue;
            for (int k = 0; k < _ifaceKeys.Count; k++)
            {
                TypeSym? iface = null;
                foreach (var i in LuaEmit.AllInterfaces(type))
                {
                    if (Key(i) == _ifaceKeys[k]) iface = i;
                }
                if (iface == null) continue;
                Symbol? impl = null;
                foreach (var m in type.Members)
                {
                    if (m.Kind == SymKind.Method && m.TypeParams != null && LuaEmit.MethodName(m) == _ifaceMethods[k]) impl = m;
                }
                if (impl == null || impl.Decl == null || impl.Decl.Orig == null || OuterTypeDecl(impl.Decl.Orig) != decl) continue;
                var implKey = Key(type.SelfType()) + "::" + LuaEmit.MethodName(impl) + "<" + _ifaceArgs[k] + ">";
                if (_names.ContainsKey(implKey)) continue;
                _names[implKey] = _ifaceNames[k];
                methods.Add(impl.Decl.Orig);
                substs.Add(_ifaceTypeArgs[k]);
                names.Add(_ifaceNames[k]);
            }
        }
    }

    private static void CollectTypeDecls(Node container, List<Node> into)
    {
        if (container.List == null) return;
        foreach (var x in container.List)
        {
            if (x.Kind == NodeKind.TypeDecl)
            {
                into.Add(x);
                CollectTypeDecls(x, into);
            }
            else if (x.Kind == NodeKind.Namespace) CollectTypeDecls(x, into);
        }
    }

    private static bool IsCallee(Node n, Node? parent)
    {
        return parent != null && parent.Kind == NodeKind.Call && parent.A == n;
    }

    // class の method が実装する interface の generic method (無ければ自分)
    private static Symbol InterfaceMethod(Symbol m)
    {
        if (m.IsStatic || m.Owner == null || m.Owner.IsInterface) return m;
        foreach (var iface in LuaEmit.AllInterfaces(m.Owner))
        {
            foreach (var cand in iface.Decl!.Members)
            {
                if (cand.Kind == SymKind.Method && cand.TypeParams != null && cand.Name == m.Name && LuaEmit.MethodName(cand) == LuaEmit.MethodName(m)) return cand;
            }
        }
        return m;
    }

    // generic な型の overload の IL の名前 (同じ名前の method が 2 つ以上)。それ以外は null
    private static string? OverloadName(Symbol? m)
    {
        if (m == null || m.Kind != SymKind.Method || m.Owner == null || m.Owner.TypeParams.Count == 0 || m.Decl == null) return null;
        int same = 0;
        foreach (var x in m.Owner.Members)
        {
            if (x.Kind == SymKind.Method && x.Name == m.Name) same++;
        }
        if (same < 2) return null;
        return LuaEmit.MethodName(m);
    }

    private static List<string> TypeParamNames(Node decl)
    {
        var r = new List<string>();
        if (decl.List2 == null) return r;
        foreach (var p in decl.List2) r.Add(p.Text);
        return r;
    }

    // ---- key ----

    // タプルの要素の名前は型を分けない
    private static string Key(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.Tuple: return "(" + ArgsKey(t.Args!).Replace(",", ", ") + ")";
            case TypeKind.Array: return Key(t.Elem) + "[]";
            case TypeKind.Nullable: return Key(t.Elem) + "?";
            case TypeKind.List: return "List<" + ArgsKey(t.Args!) + ">";
            case TypeKind.Dict: return "Dictionary<" + ArgsKey(t.Args!) + ">";
            case TypeKind.HashSet: return "HashSet<" + ArgsKey(t.Args!) + ">";
            case TypeKind.Queue: return "Queue<" + ArgsKey(t.Args!) + ">";
            case TypeKind.KeyValuePair: return "KeyValuePair<" + ArgsKey(t.Args!) + ">";
            case TypeKind.KeyCollection: return "Dictionary<" + ArgsKey(t.Args!) + ">.KeyCollection";
            case TypeKind.ValueCollection: return "Dictionary<" + ArgsKey(t.Args!) + ">.ValueCollection";
            case TypeKind.Delegate:
                {
                    var all = new List<TypeSym>();
                    foreach (var a in t.Args!) all.Add(a);
                    if (t.Ret!.Kind != TypeKind.Void) all.Add(t.Ret);
                    return (t.Ret.Kind == TypeKind.Void ? "Action" : "Func") + "<" + ArgsKey(all) + ">";
                }
            case TypeKind.TypeParam: return t.Name;
        }
        if (t.Decl != null)
        {
            if (t.Args != null && t.Args.Count > 0) return "global::" + t.Decl.FullName + "<" + ArgsKey(t.Args) + ">";
            return "global::" + t.Decl.FullName;
        }
        return IlTypes.Display(t.WithoutAnnotation());
    }

    private static string ArgsKey(List<TypeSym> args)
    {
        var parts = new List<string>();
        foreach (var a in args) parts.Add(Key(a));
        return string.Join(",", parts);
    }

    private static string MethodKey(TypeSym owner, Symbol m, List<TypeSym> args)
    {
        return Key(owner) + "::" + LuaEmit.MethodName(m) + "<" + ArgsKey(args) + ">";
    }

    private static bool ContainsParameter(TypeSym t)
    {
        if (t.Kind == TypeKind.TypeParam) return true;
        if (t.Args != null)
        {
            foreach (var a in t.Args)
            {
                if (ContainsParameter(a)) return true;
            }
        }
        if (t.Ret != null && ContainsParameter(t.Ret)) return true;
        return false;
    }

    private static bool ContainsAnyParameter(List<TypeSym> ts)
    {
        foreach (var t in ts)
        {
            if (ContainsParameter(t)) return true;
        }
        return false;
    }

    // ---- 書き換え ----

    // n の copy (template が null でなければ型の型引数を置き換え、methodArgs が null でなければ method の型引数を置き換える)。
    // 閉じた型の参照は閉じた名前に、閉じた generic method の呼び出しは閉じた method の名前にする
    private Node? Rewrite(Node? n, List<string>? typeParams, List<TypeSym>? typeArgs, Node? parent,
        List<TypeSym>? methodArgs = null, List<string>? methodParams = null)
    {
        if (n == null) return null;
        var m = n.Mirror;
        // 型引数の参照
        if (n.Kind == NodeKind.TypeName && n.A == null && n.List == null && m != null && m.Type != null && m.Type.Kind == TypeKind.TypeParam)
        {
            if (typeArgs != null && typeParams != null && !m.Type.IsMethodParam)
            {
                int at = typeParams.IndexOf(n.Text);
                if (at >= 0 && at < typeArgs.Count) return TypeNode(typeArgs[at], n);
            }
            if (methodArgs != null && methodParams != null && m.Type.IsMethodParam)
            {
                int at = methodParams.IndexOf(n.Text);
                if (at >= 0 && at < methodArgs.Count) return TypeNode(methodArgs[at], n);
            }
        }
        // 閉じた型の参照
        if ((n.Kind == NodeKind.TypeName || n.Kind == NodeKind.TupleType) && m != null && m.Type != null && !ContainsParameter(m.Type))
        {
            string? closed;
            if ((n.List != null || n.Kind == NodeKind.TupleType) && _names.TryGetValue(Key(m.Type), out closed))
            {
                var r = NameNode(closed!, n);
                // 型で修飾した入れ子の型 (`Outer.Box<int>`) は copy も外側の型に置くので修飾を残す
                if (n.Kind == NodeKind.TypeName && n.A != null && m.Type.Decl != null && m.Type.Decl.Outer != null)
                    r.A = Rewrite(n.A, typeParams, typeArgs, n, methodArgs, methodParams);
                return r;
            }
        }
        if ((n.Kind == NodeKind.Name || n.Kind == NodeKind.Member) && n.List != null && m != null && m.Info == RefKind.Type && m.Type != null && !ContainsParameter(m.Type))
        {
            string? closed;
            if (_names.TryGetValue(Key(m.Type), out closed))
            {
                var r = n.Kind == NodeKind.Member ? CopyNode(n, typeParams, typeArgs, methodArgs, methodParams) : new Node(NodeKind.Name, n.File, n.Pos);
                r.Text = closed!;
                r.List = null;
                r.End = n.End;
                // namespace で修飾した閉じた型 (`N.Box<int>`) は global の名前だけにする。
                // 型で修飾した入れ子の型 (`Outer.Box<int>`) は copy も外側の型に置くので修飾を残す
                if (n.Kind == NodeKind.Member && (m.Type.Decl == null || m.Type.Decl.Outer == null))
                {
                    r.Kind = NodeKind.Name;
                    r.A = null;
                }
                return r;
            }
        }
        // タプルの literal は閉じた struct の生成
        if (n.Kind == NodeKind.Tuple && m != null && m.Type != null && n.List != null)
        {
            var t = IlTypes.UnwrapNullable(m.Type);
            string? closed;
            if (t.Kind == TypeKind.Tuple && !ContainsParameter(t) && _names.TryGetValue(Key(t), out closed) && !_deconstructed.Contains(n))
            {
                var nw = new Node(NodeKind.New, n.File, n.Pos);
                nw.End = n.End;
                nw.A = NameNode(closed!, n);
                nw.A.Kind = NodeKind.TypeName;
                nw.Value = 1;
                foreach (var e in n.List)
                {
                    var arg = new Node(NodeKind.Arg, e.File, e.Pos);
                    arg.End = e.End;
                    arg.A = Rewrite(e, typeParams, typeArgs, n, methodArgs, methodParams);
                    nw.Add(arg);
                }
                return nw;
            }
        }
        var r2 = CopyNode(n, typeParams, typeArgs, methodArgs, methodParams);
        {
            // 名前の付いたタプルの要素 (`t.x`) は閉じた struct の Item の field
            if (n.Kind == NodeKind.Member && m != null && m.Sym != null && m.Sym.Kind == SymKind.Field && m.Sym.Owner == null && m.A != null && m.A.Type != null
                && IlTypes.UnwrapNullable(m.A.Type).Kind == TypeKind.Tuple && m.Sym.Name != n.Text && _names.ContainsKey(Key(IlTypes.UnwrapNullable(m.A.Type))))
                r2.Text = m.Sym.Name;
            // 閉じた generic method の呼び出しと method group
            if ((n.Kind == NodeKind.Name || n.Kind == NodeKind.Member) && m != null)
            {
                var callNode = parent != null && parent.Kind == NodeKind.Call && parent.A == n ? parent.Mirror : null;
                var sym = callNode != null ? callNode.Sym : m.Sym;
                var targs = callNode != null ? callNode.TypeArgs : m.TypeArgs;
                if (sym != null && sym.Kind == SymKind.Method)
                {
                    string? closed = null;
                    if (sym.TypeParams != null && targs != null && !ContainsAnyParameter(targs))
                    {
                        var method = InterfaceMethod(sym);
                        var owner = method.Owner;
                        if (owner != null) _names.TryGetValue(MethodKey(owner.SelfType(), method, targs), out closed);
                        if (closed == null && sym.Owner != null)
                        {
                            // 実装する型の閉じた method (interface の method の名前と同じ)
                            var implKey = Key(sym.Owner.SelfType()) + "::" + LuaEmit.MethodName(sym) + "<" + ArgsKey(targs) + ">";
                            _names.TryGetValue(implKey, out closed);
                        }
                    }
                    if (closed != null)
                    {
                        r2.Text = closed;
                        r2.List = null;
                    }
                    else
                    {
                        var overload = OverloadName(sym);
                        if (overload != null) r2.Text = overload;
                    }
                }
            }
            // 型引数どうしの ==/!= は参照の比較 (where T : class)。型引数を record に置き換えても値の等価にならないよう、両辺を object にする
            if (n.Kind == NodeKind.Binary && (n.Text == "==" || n.Text == "!=") && m != null && m.A != null && m.B != null
                && m.A.Type != null && m.B.Type != null && m.A.Type.Kind == TypeKind.TypeParam && m.B.Type.Kind == TypeKind.TypeParam
                && (typeArgs != null || methodArgs != null))
            {
                r2.A = ObjectCast(r2.A!, n);
                r2.B = ObjectCast(r2.B!, n);
            }
        }
        return r2;
    }

    private Node ObjectCast(Node operand, Node at)
    {
        var c = new Node(NodeKind.Cast, at.File, at.Pos);
        c.End = at.End;
        c.A = TypeNode(TypeSym.ObjectType, at);
        c.B = operand;
        return c;
    }

    private List<Node> _deconstructed = new List<Node>();

    private Node CopyNode(Node n, List<string>? typeParams, List<TypeSym>? typeArgs, List<TypeSym>? methodArgs, List<string>? methodParams)
    {
        var r = new Node(n.Kind, n.File, n.Pos);
        r.End = n.End;
        r.Text = n.Text;
        r.Value = n.Value;
        r.Flags = n.Flags;
        r.Mirror = n.Mirror;
        r.Oblivious = n.Oblivious;
        r.Appended = n.Appended;
        r.A = Rewrite(n.A, typeParams, typeArgs, n, methodArgs, methodParams);
        r.B = Rewrite(n.B, typeParams, typeArgs, n, methodArgs, methodParams);
        r.C = Rewrite(n.C, typeParams, typeArgs, n, methodArgs, methodParams);
        r.D = Rewrite(n.D, typeParams, typeArgs, n, methodArgs, methodParams);
        r.List = RewriteList(n.List, n, typeParams, typeArgs, methodArgs, methodParams);
        r.List2 = RewriteList(n.List2, n, typeParams, typeArgs, methodArgs, methodParams);
        r.List3 = RewriteList(n.List3, n, typeParams, typeArgs, methodArgs, methodParams);
        return r;
    }

    private List<Node>? RewriteList(List<Node>? list, Node parent, List<string>? typeParams, List<TypeSym>? typeArgs, List<TypeSym>? methodArgs, List<string>? methodParams)
    {
        if (list == null) return null;
        var r = new List<Node>();
        foreach (var x in list) r.Add(Rewrite(x, typeParams, typeArgs, parent, methodArgs, methodParams)!);
        return r;
    }

    private static Node NameNode(string name, Node at)
    {
        var r = new Node(NodeKind.TypeName, at.File, at.Pos);
        r.End = at.End;
        r.Text = name;
        if (at.Kind == NodeKind.Name || at.Kind == NodeKind.Member) r.Kind = NodeKind.Name;
        return r;
    }

    // 型の構文を作る (型引数の置き換え)。作った node は全て oblivious
    private Node TypeNode(TypeSym t, Node at)
    {
        var n = TypeNodeCore(t, at);
        MarkOblivious(n);
        return n;
    }

    private static void MarkOblivious(Node n)
    {
        n.Oblivious = true;
        if (n.A != null) MarkOblivious(n.A);
        if (n.List != null)
        {
            foreach (var x in n.List) MarkOblivious(x);
        }
    }

    private Node TypeNodeCore(TypeSym t, Node at)
    {
        switch (t.Kind)
        {
            case TypeKind.Nullable:
                {
                    var n = new Node(NodeKind.NullableType, at.File, at.Pos);
                    n.End = at.End;
                    n.A = TypeNode(t.Elem, at);
                    return n;
                }
            case TypeKind.Array:
                {
                    var n = new Node(NodeKind.ArrayType, at.File, at.Pos);
                    n.End = at.End;
                    n.Value = 1;
                    n.A = TypeNode(t.Elem, at);
                    return n;
                }
            case TypeKind.Tuple:
                {
                    var n = new Node(NodeKind.TupleType, at.File, at.Pos);
                    n.End = at.End;
                    for (int i = 0; i < t.Args!.Count; i++)
                    {
                        var e = new Node(NodeKind.Param, at.File, at.Pos);
                        e.End = at.End;
                        e.A = TypeNode(t.Args[i], at);
                        if (t.Names != null && i < t.Names.Count) e.Text = t.Names[i];
                        n.Add(e);
                    }
                    return n;
                }
            case TypeKind.Int:
            case TypeKind.Float:
            case TypeKind.Bool:
            case TypeKind.String:
            case TypeKind.Char:
            case TypeKind.Byte:
            case TypeKind.UInt:
            case TypeKind.Short:
            case TypeKind.Object:
            case TypeKind.Double:
            case TypeKind.Long:
                {
                    var n = new Node(NodeKind.PredefType, at.File, at.Pos);
                    n.End = at.End;
                    n.Text = t.Name;
                    return n;
                }
            case TypeKind.Delegate:
                {
                    var n = Simple(t.Ret!.Kind == TypeKind.Void ? "Action" : "Func", at);
                    var args = new List<TypeSym>();
                    foreach (var a in t.Args!) args.Add(a);
                    if (t.Ret.Kind != TypeKind.Void) args.Add(t.Ret);
                    if (args.Count > 0)
                    {
                        n.List = new List<Node>();
                        foreach (var a in args) n.List.Add(TypeNode(a, at));
                    }
                    return n;
                }
            case TypeKind.List: return Generic("List", t, at);
            case TypeKind.Dict: return Generic("Dictionary", t, at);
            case TypeKind.HashSet: return Generic("HashSet", t, at);
            case TypeKind.Queue: return Generic("Queue", t, at);
            case TypeKind.KeyValuePair: return Generic("KeyValuePair", t, at);
            case TypeKind.Utf8Bytes: return Qualified("TinySystem.Text.Utf8Bytes", at);
            case TypeKind.Rune: return Qualified("System.Text.Rune", at);
            case TypeKind.Random: return Qualified("TinySystem.Random", at);
        }
        if (t.Decl != null)
        {
            var n = Qualified(t.Decl.FullName, at);
            if (t.Args != null && t.Args.Count > 0)
            {
                n.List = new List<Node>();
                foreach (var a in t.Args) n.List.Add(TypeNode(a, at));
            }
            return n;
        }
        return Simple(t.Name, at);
    }

    private static Node Simple(string name, Node at)
    {
        var n = new Node(NodeKind.TypeName, at.File, at.Pos);
        n.End = at.End;
        n.Text = name;
        return n;
    }

    private Node Generic(string name, TypeSym t, Node at)
    {
        var n = Simple(name, at);
        n.List = new List<Node>();
        foreach (var a in t.Args!) n.List.Add(TypeNode(a, at));
        return n;
    }

    // global:: から書いた修飾された名前
    private static Node Qualified(string fullName, Node at)
    {
        var parts = fullName.Split('.');
        Node? cur = null;
        foreach (var p in parts)
        {
            var n = Simple(p, at);
            if (cur == null) n.Value = 1;
            else n.A = cur;
            cur = n;
        }
        return cur!;
    }

    // 最後の回: generic の雛形 (型と method) を消す
    private static void RemoveTemplates(Node container)
    {
        if (container.List == null) return;
        var kept = new List<Node>();
        foreach (var x in container.List)
        {
            bool template = (x.Kind == NodeKind.TypeDecl || x.Kind == NodeKind.Method) && x.List2 != null && x.List2.Count > 0;
            if (template) continue;
            if (x.Kind == NodeKind.TypeDecl || x.Kind == NodeKind.Namespace) RemoveTemplates(x);
            kept.Add(x);
        }
        container.List = kept;
    }
}

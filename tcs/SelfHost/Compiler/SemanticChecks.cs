using System.Collections.Generic;

namespace TcsSelf;

// 型の決まった木の上の準拠検査 (参照実装の TinyCsComplianceFacts のうち、意味を見る規則)。
// --ref の宣言と tcs が配る library は対象外。名前は参照実装の診断の名前に揃える
public sealed class SemanticChecks
{
    private readonly Compilation _c;
    // 0 との比較の operand になっている string.CompareOrdinal の呼び出し
    private readonly List<Node> _comparedWithZero = new List<Node>();
    // 歩いている型と method の宣言 (型引数の制約を引く)
    private Node? _typeDecl;
    private Node? _method;

    public SemanticChecks(Compilation c)
    {
        _c = c;
    }

    private void Report(string name, Node at)
    {
        _c.Error("TCS1001", "Unsupported syntax: " + name, at);
    }

    public void CheckAll()
    {
        for (int i = 0; i < _c.Units.Count; i++)
        {
            var f = _c.Files[i];
            if (f.IsRef || f.IsLibrary) continue;
            Walk(_c.Units[i], false);
        }
        foreach (var t in _c.Types)
        {
            if (t.IsRef || t.Decl.File.IsLibrary) continue;
            CheckType(t);
        }
    }

    // ---- 型の宣言 ----

    private void CheckType(TypeDeclSym t)
    {
        foreach (var i in t.Interfaces)
        {
            // host (--ref) の interface を user の型で実装すると、host の名前 (snake_case) の呼び出しが実装に届かない
            if (i.Decl != null && i.Decl.IsRef) Report("RefInterfaceImplementation(" + i.Display() + ")", t.Decl);
        }
        if (!t.IsInterface && !t.IsEnum) CheckInterfaceMembers(t);
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Method && m.Decl != null && m.Decl.Kind == NodeKind.Method && IsCollidingOverload(t, m)) Report("MethodOverload", m.Decl);
        }
    }

    // interface の member を実装しているか (CS0535)
    private void CheckInterfaceMembers(TypeDeclSym t)
    {
        foreach (var iface in LuaEmit.AllInterfaces(t))
        {
            var d = iface.Decl!;
            if (d.IsRef) continue;
            foreach (var member in d.Members)
            {
                if (member.IsStatic) continue;
                bool found = false;
                foreach (var cand in t.Members)
                {
                    if (cand.Name != member.Name || cand.IsStatic) continue;
                    if (member.Kind == SymKind.Property && cand.Kind == SymKind.Property) found = true;
                    if (member.Kind == SymKind.Method && cand.Kind == SymKind.Method && SameParams(cand, member, iface)) found = true;
                }
                if (found) continue;
                var shown = member.Kind == SymKind.Method ? d.Name + "." + member.Name + "(" + ParamList(member, iface) + ")" : d.Name + "." + member.Name;
                _c.Error("CS0535", "'" + t.Name + "' does not implement interface member '" + shown + "'", t.Decl);
            }
        }
    }

    private static string ParamList(Symbol m, TypeSym iface)
    {
        var parts = new List<string>();
        if (m.Params != null)
        {
            foreach (var p in m.Params) parts.Add(p.Type.Subst(iface.Args, null).Display());
        }
        return string.Join(", ", parts);
    }

    private static bool SameParams(Symbol impl, Symbol iface, TypeSym ifaceType)
    {
        if (impl.ParamCount != iface.ParamCount) return false;
        for (int i = 0; i < impl.ParamCount; i++)
        {
            if (!TypeSym.Same(impl.Params![i].Type, iface.Params![i].Type.Subst(ifaceType.Args, null))) return false;
        }
        return true;
    }

    // overload は名前に parameter の型を付けて別の key にする。その名前が同じ型の他の member と同じ綴りになる宣言は止める
    private static bool IsCollidingOverload(TypeDeclSym t, Symbol m)
    {
        var name = LuaMethodName(t, m);
        foreach (var other in t.Members)
        {
            if (other == m) continue;
            var otherName = other.Kind == SymKind.Method && other.Decl != null && other.Decl.Kind == NodeKind.Method ? LuaMethodName(t, other) : other.Name;
            if (otherName == name) return true;
        }
        return false;
    }

    // LuaEmit.MethodName と同じ規則 (interface の実装の改名は除く)
    private static string LuaMethodName(TypeDeclSym t, Symbol m)
    {
        int same = 0;
        foreach (var x in t.Members)
        {
            if (x.Kind == SymKind.Method && x.Name == m.Name && x.Decl != null && x.Decl.Kind == NodeKind.Method) same++;
        }
        if (same < 2) return m.Name;
        var parts = new List<string>();
        parts.Add(m.Name);
        if (m.Params != null)
        {
            foreach (var p in m.Params)
            {
                parts.Add("__");
                if (p.IsOut) parts.Add("out_");
                parts.Add(LuaNaming.TypeToken(p.Type));
            }
        }
        return string.Join("", parts);
    }

    // ---- 式と文 ----

    private void Walk(Node? n, bool checkedContext)
    {
        if (n == null) return;
        switch (n.Kind)
        {
            case NodeKind.TypeDecl:
            {
                var outerType = _typeDecl;
                _typeDecl = n;
                if (n.List != null)
                {
                    foreach (var m in n.List) Walk(m, false);
                }
                _typeDecl = outerType;
                return;
            }
            case NodeKind.Method:
            {
                var outerMethod = _method;
                _method = n;
                Walk(n.B, false);
                Walk(n.C, false);
                if (n.List != null)
                {
                    foreach (var x in n.List) Walk(x, false);
                }
                _method = outerMethod;
                return;
            }
            case NodeKind.Checked:
                Walk(n.A, n.Value == 1);
                return;
            case NodeKind.Call:
                CheckCall(n);
                break;
            case NodeKind.Binary:
                MarkComparedWithZero(n);
                if (checkedContext && IsCheckedArithmetic(n)) Report("CheckedOperation", n);
                break;
            case NodeKind.Unary:
                if (checkedContext && n.Text == "-" && n.Type != null && (n.Type.Kind != TypeKind.Int) && IlTypes.IsIntegral(n.Type) && n.Sym == null) Report("CheckedOperation", n);
                break;
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (checkedContext && IlTypes.IsIntegral(n.A!.Type)) Report("CheckedOperation", n);
                break;
            case NodeKind.Assign:
                if (checkedContext && n.Text != "=" && n.Text != "??=" && IlTypes.IsIntegral(n.A!.Type) && n.Text != "&=" && n.Text != "|=" && n.Text != "^=") Report("CheckedOperation", n);
                break;
            case NodeKind.Cast:
                if (checkedContext && IsCheckedConversion(n)) Report("CheckedOperation", n);
                CheckBoxingCast(n);
                break;
            case NodeKind.Conv:
                CheckBoxing(n);
                break;
            case NodeKind.Foreach:
                CheckForeachBoxing(n);
                break;
            case NodeKind.Is:
                CheckTypeTestPattern(n.B, n.A!.Type);
                break;
            case NodeKind.As:
                CheckTypeTest(n.A!.Type, n.Type, n);
                break;
            case NodeKind.SwitchExpr:
                if (n.List != null)
                {
                    foreach (var arm in n.List) CheckTypeTestPattern(arm.A, n.A!.Type);
                }
                break;
            case NodeKind.Switch:
                if (n.List != null)
                {
                    foreach (var section in n.List)
                    {
                        if (section.List == null) continue;
                        foreach (var label in section.List)
                        {
                            if (label.Kind == NodeKind.CaseLabel) CheckTypeTestPattern(label.A, n.A!.Type);
                        }
                    }
                }
                break;
            case NodeKind.New:
                CheckDictionaryKey(n);
                break;
        }
        Walk(n.A, checkedContext);
        Walk(n.B, checkedContext);
        Walk(n.C, checkedContext);
        Walk(n.D, checkedContext);
        if (n.List != null)
        {
            foreach (var x in n.List) Walk(x, checkedContext);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) Walk(x, checkedContext);
        }
    }

    // ---- 呼び出し ----

    private void CheckCall(Node call)
    {
        var m = call.Sym;
        if (m == null) return;
        // 自作の method の out は `out var` / `out _` の新しい変数だけ受ける (既存の変数は別名になり、multi-return では呼び出しの中の書き込みが見えない)
        if (m.Kind == SymKind.Method && m.Builtin == "" && m.Owner != null && !m.Owner.IsRef && call.List != null)
        {
            foreach (var a in call.List)
            {
                if (!a.Has(Mods.Out) || a.A == null) continue;
                var x = a.A;
                bool ok = x.Kind == NodeKind.PatDecl || x.Kind == NodeKind.PatDiscard || (x.Kind == NodeKind.Name && x.Text == "_" && x.Local == null && x.Sym == null);
                if (!ok) Report("OutArgumentNotDeclaration", a);
            }
        }
        // string.CompareOrdinal の値は符号だけが契約なので、0 との比較の operand 以外では使えない
        if (m.Builtin == "String.CompareOrdinal" && !_comparedWithZero.Contains(call)) Report("CompareOrdinalValue", call);
        // host (--ref) の型の overload は名前に型を付けないので呼び分けられない
        if (m.Kind == SymKind.Method && m.Builtin == "" && m.Owner != null && m.Owner.IsRef && HostOverloadCount(m) > 1) Report("MethodOverload", call.A != null ? call.A : call);
    }

    private static int HostOverloadCount(Symbol m)
    {
        int count = 0;
        TypeDeclSym? t = m.Owner;
        while (t != null)
        {
            foreach (var x in t.Members)
            {
                if (x.Kind == SymKind.Method && x.Name == m.Name && !(x.Decl != null && x.Decl.Has(Mods.Private))) count++;
            }
            t = t.BaseClass;
        }
        return count;
    }

    private void MarkComparedWithZero(Node bin)
    {
        var op = bin.Text;
        if (op != "==" && op != "!=" && op != "<" && op != "<=" && op != ">" && op != ">=") return;
        var l = Unwrap(bin.A!);
        var r = Unwrap(bin.B!);
        if (l.Kind == NodeKind.Call && IsZero(bin.B!)) _comparedWithZero.Add(l);
        if (r.Kind == NodeKind.Call && IsZero(bin.A!)) _comparedWithZero.Add(r);
    }

    private static Node Unwrap(Node e)
    {
        while ((e.Kind == NodeKind.Paren || e.Kind == NodeKind.Conv) && e.A != null) e = e.A;
        return e;
    }

    private static bool IsZero(Node e)
    {
        return e.HasConst && e.Const != null && e.Const.Kind == ConstKind.Int && e.Const.I == 0;
    }

    // ---- checked ----

    // checked で overflow を検出するのは i32 の + - * / % と単項 - だけ (それ以外の checked な演算は止める)
    private static bool IsCheckedArithmetic(Node bin)
    {
        var op = bin.Text;
        if (op != "+" && op != "-" && op != "*" && op != "/" && op != "%") return false;
        if (bin.Sym != null && bin.Sym.Builtin == "" && bin.Sym.Kind == SymKind.Method) return false;
        var t = bin.Type;
        if (t == null || !IlTypes.IsIntegral(t)) return false;
        return t.Kind != TypeKind.Int;
    }

    // 明示の数の変換は float / double → int だけ (除算と同じく範囲外を fault にする)
    private static bool IsCheckedConversion(Node cast)
    {
        var target = cast.Type;
        var src = cast.B != null ? Unwrap(cast.B).Type : null;
        if (target == null || src == null) return false;
        if (!(src.IsNumeric || src.Kind == TypeKind.Enum) || !(target.IsNumeric || target.Kind == TypeKind.Enum)) return false;
        // 暗黙に変換できる cast (広げる変換) は overflow しない
        if (cast.Info == ConvKind.Identity || cast.Info == ConvKind.IntToFloat) return false;
        return !((src.Kind == TypeKind.Float || src.Kind == TypeKind.Double) && target.Kind == TypeKind.Int);
    }

    // ---- struct の boxing ----

    // struct を object / interface に入れる。Lua の struct は型を持たない table で、`is` と interface 経由の呼び出しが効かない
    private void CheckBoxing(Node conv)
    {
        var src = conv.A!.Type;
        if (src == null || conv.Type == null) return;
        if (IsBoxedStruct(src) && IsReferenceTarget(conv.Type)) Report("StructBoxing", conv.A);
    }

    private void CheckBoxingCast(Node cast)
    {
        var src = cast.B != null ? Unwrap(cast.B).Type : null;
        if (src == null || cast.Type == null) return;
        if (cast.B!.Kind == NodeKind.Conv) return;
        if (IsBoxedStruct(src) && IsReferenceTarget(cast.Type)) Report("StructBoxing", cast);
    }

    private void CheckForeachBoxing(Node f)
    {
        if (f.A == null || f.Local == null || f.B == null || f.B.Type == null) return;
        var elem = IlTypes.ElementOf(f.B.Type);
        if (elem != null && IsBoxedStruct(elem) && IsReferenceTarget(f.Local.Type)) Report("StructBoxing", f);
    }

    private static bool IsBoxedStruct(TypeSym t)
    {
        var u = IlTypes.UnwrapNullable(t);
        // ToString を override した struct の文字列化は直接の呼び出しに写すので boxing にならない
        if (u.Kind == TypeKind.Struct && u.Decl != null && IlBuild.HasToStringOverride(u)) return false;
        return (u.Kind == TypeKind.Struct && u.Decl != null) || u.Kind == TypeKind.Utf8Bytes;
    }

    private static bool IsReferenceTarget(TypeSym t)
    {
        return t.Kind == TypeKind.Object || t.Kind == TypeKind.Interface;
    }

    // ---- interface の型の検査 ----

    private void CheckTypeTestPattern(Node? p, TypeSym? input)
    {
        if (p == null) return;
        switch (p.Kind)
        {
            case NodeKind.PatType:
            case NodeKind.PatDecl:
                CheckTypeTest(input, p.Type, p);
                return;
            case NodeKind.PatProp:
                if (p.A != null) CheckTypeTest(input, p.Type, p);
                if (p.List != null)
                {
                    foreach (var sub in p.List) CheckTypeTestPattern(sub.A, sub.Type);
                }
                return;
            case NodeKind.PatAnd:
            case NodeKind.PatOr:
                CheckTypeTestPattern(p.A, input);
                CheckTypeTestPattern(p.B, input);
                return;
            case NodeKind.PatNot:
                CheckTypeTestPattern(p.A, input);
                return;
        }
    }

    // class の interface の検査は実装の一覧で判定する。一覧に載らない interface (host、generic) と、一覧を持たない入力 (参照型でない) は止める
    private void CheckTypeTest(TypeSym? input, TypeSym? type, Node at)
    {
        if (type == null || type.Kind != TypeKind.Interface) return;
        bool refInput = input != null && (input.Kind == TypeKind.Class || input.Kind == TypeKind.Interface || input.Kind == TypeKind.Object || input.Kind == TypeKind.String
            || input.Kind == TypeKind.TypeParam && IsClassConstrained(input));
        bool generic = type.Decl != null && type.Decl.TypeParams.Count > 0;
        if (!refInput || generic || type.Decl == null || type.Decl.IsRef) Report("InterfaceTypeTest", at);
    }

    // 型引数が参照型に制約されているか (`where T : class`)
    private bool IsClassConstrained(TypeSym tp)
    {
        var holder = tp.IsMethodParam ? _method : _typeDecl;
        if (holder == null || holder.List2 == null || tp.Index >= holder.List2.Count) return false;
        return (holder.List2[tp.Index].Value & 1) != 0;
    }

    // ---- 値で比べる key ----

    // 値で比べる key の Dictionary と HashSet (Lua の table は table の同一性で引く)
    private void CheckDictionaryKey(Node creation)
    {
        var t = creation.Type;
        if (t == null) return;
        if (t.Kind == TypeKind.HashSet)
        {
            var e = t.Elem;
            if (HasValueEquality(e) || e.Kind == TypeKind.Float || e.Kind == TypeKind.Double) Report("UnsupportedHashSetElement(" + e.Display() + ")", creation);
            return;
        }
        if (t.Kind == TypeKind.Dict && HasValueEquality(t.Arg(0))) Report("ValueEqualityDictionaryKey(" + t.Arg(0).Display() + ")", creation);
    }

    private static bool HasValueEquality(TypeSym t)
    {
        var u = IlTypes.UnwrapNullable(t);
        switch (u.Kind)
        {
            case TypeKind.Struct:
            case TypeKind.Tuple:
            case TypeKind.KeyValuePair:
            case TypeKind.Rune:
            case TypeKind.Utf8Bytes:
                return true;
            case TypeKind.Class:
                return u.Decl != null && u.Decl.DeclKind == DeclKinds.RecordClass;
        }
        return false;
    }
}

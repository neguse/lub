using System.Collections.Generic;

namespace TcsSelf;

// 型の決まった木の上の準拠検査 (参照実装の TinyCsComplianceFacts のうち、意味を見る規則)。
// --ref の宣言と tcs が配る library は対象外。名前は参照実装の診断の名前に揃える
public sealed class SemanticChecks
{
    private readonly Compilation _c;
    // 0 との比較の operand になっている string.CompareOrdinal の呼び出し
    private readonly List<Node> _comparedWithZero = new List<Node>();
    // 文字列にする位置の式 (string の + の operand と Console.WriteLine の引数)。値の boxing にならない
    private readonly List<Node> _stringified = new List<Node>();
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
        // record の値の等価は field の比較で、Utf8Bytes では .NET (byte[] の参照) と Lua (table の同一性) が揃わない
        if (t.IsRecord && HasUtf8BytesField(t.SelfType())) Report("Utf8BytesInRecord", t.Decl);
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
        var name = LuaEmit.MethodName(m);
        foreach (var other in t.Members)
        {
            if (other == m) continue;
            var otherName = other.Kind == SymKind.Method && other.Decl != null && other.Decl.Kind == NodeKind.Method ? LuaEmit.MethodName(other) : other.Name;
            if (otherName == name) return true;
        }
        return false;
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
                if (n.Text == "+" && n.Type != null && n.Type.Kind == TypeKind.String)
                {
                    _stringified.Add(n.A!);
                    _stringified.Add(n.B!);
                    CheckStringifiedOperand(n.A!);
                    CheckStringifiedOperand(n.B!);
                }
                CheckEquality(n);
                break;
            case NodeKind.InterpHole:
                if (n.A != null) _stringified.Add(n.A);
                break;
            case NodeKind.Unary:
                if (checkedContext && n.Text == "-" && n.Type != null && (n.Type.Kind != TypeKind.Int) && IlTypes.IsIntegral(n.Type) && n.Sym == null) Report("CheckedOperation", n);
                break;
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (checkedContext && IlTypes.IsIntegral(n.A!.Type)) Report("CheckedOperation", n);
                // byte の ++ / -- と複合代入は 0..255 に丸まらない
                if (IsByteType(n.A!.Type)) Report("ByteNarrowing", n);
                break;
            case NodeKind.Assign:
                if (checkedContext && n.Text != "=" && n.Text != "??=" && IlTypes.IsIntegral(n.A!.Type) && n.Text != "&=" && n.Text != "|=" && n.Text != "^=") Report("CheckedOperation", n);
                if (n.Text != "=" && n.Text != "??=" && IsByteType(n.A!.Type)) Report("ByteNarrowing", n);
                if (n.Text == "+=" && n.Type != null && n.Type.Kind == TypeKind.String && n.B != null) _stringified.Add(n.B);
                break;
            case NodeKind.Cast:
                if (checkedContext && IsCheckedConversion(n)) Report("CheckedOperation", n);
                CheckBoxingCast(n);
                CheckGenericTypeTest(n.B != null ? Unwrap(n.B).Type : null, n.Type, n, "UnverifiableCast");
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
                CheckGenericTypeTest(n.A.Type, n.Type, n, "UnverifiableTypeTest");
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
                CheckStructConstraint(n.Type, n);
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
        // Rune? の `?.ToString()` はサブセットの外
        if (m.Builtin == "Rune.ToString" && call.A != null && call.A.Kind == NodeKind.Member && call.A.Value == 1) Report("UnsupportedToString(System.Text.Rune)", call);
        // Console.WriteLine の引数は文字列にする
        if ((m.Builtin == "Console.WriteLine" || m.Builtin == "Console.Write") && call.List != null)
        {
            foreach (var a in call.List)
            {
                if (a.A == null) continue;
                _stringified.Add(a.A);
                CheckStringifiedOperand(a.A);
            }
        }
        // string.Join の要素は文字列にできる型だけ
        if ((m.Builtin == "String.Join" || m.Builtin == "String.JoinAny") && call.List != null && call.List.Count == 2)
        {
            var elem = IlTypes.ElementOf(Unwrap(call.List[1].A!).Type);
            if (elem != null && !IsStringifiable(elem)) Report("UnsupportedToString(" + elem.Display() + ")", call.List[1].A!);
        }
        // List の要素の等価は .NET が field の比較、Lua が table の同一性で、Utf8Bytes を含む値では揃わない
        if ((m.Builtin == "List.Contains" || m.Builtin == "List.IndexOf" || m.Builtin == "List.Remove") && call.A != null && call.A.Kind == NodeKind.Member && call.A.A != null)
        {
            var lt = call.A.A.Type;
            if (lt != null && lt.Kind == TypeKind.List && ContainsUtf8Bytes(lt.Elem))
                _c.Error("TCS1002", "unsupported API: System.Collections.Generic.List<T>." + m.Name, call.A);
        }
        // interface の制約を持つ型引数に struct を入れる (Lua の generic なコードは interface の method を `x:M()` で呼ぶが、struct の member は自由関数)
        if (m.Kind == SymKind.Method && m.Builtin == "" && m.TypeParams != null && call.TypeArgs != null && m.TypeParamConstraints != null)
        {
            for (int i = 0; i < call.TypeArgs.Count && i < m.TypeParamConstraints.Count; i++)
            {
                if (IsStructForInterface(call.TypeArgs[i], m.TypeParamConstraints[i])) Report("StructInterfaceConstraint(" + call.TypeArgs[i].Display() + ")", call.A != null ? call.A : call);
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
        if (!IsReferenceTarget(conv.Type)) return;
        if (IsBoxedStruct(src))
        {
            if (!(!IlTypes.IsNullableValueType(src) && IlBuild.HasToStringOverride(src) && IsStringified(conv))) Report("StructBoxing", conv.A);
            return;
        }
        // int / float / bool 以外の値型 (char、byte、enum など) を object に入れると、Lua では型を失う
        var u = IlTypes.UnwrapNullable(src);
        if (!u.IsValueType || u.Kind == TypeKind.Int || u.Kind == TypeKind.Float || u.Kind == TypeKind.Bool) return;
        if (IsStringified(conv)) return;
        Report("ValueBoxing(" + u.Display() + ")", conv.A);
    }

    // 文字列にする式 (string の + の operand と Console.WriteLine の引数): ToString を持たない struct は boxing、文字列にできない型は止める
    private void CheckStringifiedOperand(Node e)
    {
        var x = Unwrap(e);
        var t = x.Type;
        if (t == null) return;
        var u = IlTypes.UnwrapNullable(t);
        if (IsBoxedStruct(t) && (IlTypes.IsNullableValueType(t) || !IlBuild.HasToStringOverride(u)))
        {
            if (!(e.Kind == NodeKind.Conv && IsReferenceTarget(e.Type!))) Report("StructBoxing", x);
            return;
        }
        // Rune は値そのものの文字列化だけ (Rune? は外)
        if (u.Kind == TypeKind.Rune && IlTypes.IsNullableValueType(t)) Report("UnsupportedToString(" + t.Display() + ")", x);
        // 文字列にできない型 (ToString を持たない class / interface、collection、delegate)
        if ((u.Kind == TypeKind.Class || u.Kind == TypeKind.Interface) && u.Decl != null && !IlBuild.HasToStringOverride(u)) Report("UnsupportedToString(" + t.Display() + ")", x);
        if (u.Kind == TypeKind.List || u.Kind == TypeKind.Dict || u.Kind == TypeKind.Array || u.Kind == TypeKind.HashSet || u.Kind == TypeKind.Queue || u.Kind == TypeKind.Delegate)
            Report("UnsupportedToString(" + t.Display() + ")", x);
    }

    private bool IsStringified(Node e)
    {
        return _stringified.Contains(e) || (e.A != null && _stringified.Contains(e.A));
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
        return (u.Kind == TypeKind.Struct && u.Decl != null) || u.Kind == TypeKind.Utf8Bytes || u.Kind == TypeKind.Tuple;
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
                CheckGenericTypeTest(input, p.Type, p, "UnverifiableTypeTest");
                return;
            case NodeKind.PatProp:
                if (p.A != null)
                {
                    CheckTypeTest(input, p.Type, p);
                    CheckGenericTypeTest(input, p.Type, p, "UnverifiableTypeTest");
                }
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

    // ---- 等価と generic ----

    // delegate の == / != (null との比較を除く) と、Utf8Bytes と null の比較 (Utf8Bytes は struct)
    private void CheckEquality(Node bin)
    {
        if (bin.Text != "==" && bin.Text != "!=") return;
        var l = Unwrap(bin.A!);
        var r = Unwrap(bin.B!);
        bool lNull = l.Kind == NodeKind.NullLit || l.Kind == NodeKind.DefaultLit && l.A == null;
        bool rNull = r.Kind == NodeKind.NullLit || r.Kind == NodeKind.DefaultLit && r.A == null;
        if ((IsDelegate(bin.A!) || IsDelegate(bin.B!)) && !lNull && !rNull) Report("DelegateEquality", bin);
        if ((lNull && r.Type != null && r.Type.Kind == TypeKind.Utf8Bytes) || (rNull && l.Type != null && l.Type.Kind == TypeKind.Utf8Bytes))
            _c.Error("CS0019", "Operator '" + bin.Text + "' cannot be applied to operands of type 'Utf8Bytes' and '<null>'", bin);
    }

    private static bool IsDelegate(Node e)
    {
        return e.Type != null && e.Type.Kind == TypeKind.Delegate || (e.Kind == NodeKind.Conv && e.A != null && e.A.Type != null && e.A.Type.Kind == TypeKind.Delegate);
    }

    // generic な class への型の検査 / cast (Lua は型引数を消すので確かめられない)
    // 値を実行時に調べる (参照型か型引数の入力で、暗黙の変換が無い) ときだけ
    private void CheckGenericTypeTest(TypeSym? input, TypeSym? type, Node at, string name)
    {
        if (type == null || type.Kind != TypeKind.Class || type.Decl == null) return;
        if (type.Args == null || type.Args.Count == 0) return;
        if (input == null) return;
        bool runtime = input.Kind == TypeKind.TypeParam || input.Kind == TypeKind.Object || input.Kind == TypeKind.Interface || input.Kind == TypeKind.Class;
        if (!runtime || TypeSym.Same(input, type)) return;
        if (input.Decl != null && type.Decl != null && input.Decl != type.Decl)
        {
            TypeDeclSym? cur = input.Decl;
            while (cur != null)
            {
                if (cur == type.Decl) return;
                cur = cur.BaseClass;
            }
        }
        Report(name + "(" + type.Display() + ")", at);
    }

    private void CheckStructConstraint(TypeSym? created, Node at)
    {
        if (created == null || created.Decl == null || created.Args == null) return;
        var constraints = created.Decl.TypeParamConstraints;
        for (int i = 0; i < created.Args.Count && i < constraints.Count; i++)
        {
            if (IsStructForInterface(created.Args[i], constraints[i])) Report("StructInterfaceConstraint(" + created.Args[i].Display() + ")", at);
        }
    }

    private static bool IsStructForInterface(TypeSym arg, List<TypeSym> constraints)
    {
        if (!(arg.Kind == TypeKind.Struct && arg.Decl != null) && arg.Kind != TypeKind.Tuple) return false;
        foreach (var c in constraints)
        {
            if (c.Kind == TypeKind.Interface) return true;
        }
        return false;
    }

    private static bool IsByteType(TypeSym? t)
    {
        return t != null && IlTypes.UnwrapNullable(t).Kind == TypeKind.Byte;
    }

    // 文字列にできる型 (string.Join の要素)
    private static bool IsStringifiable(TypeSym t)
    {
        var u = IlTypes.UnwrapNullable(t);
        switch (u.Kind)
        {
            // 要素は runtime が文字列にするので ToString の override を呼べない
            case TypeKind.Class:
            case TypeKind.Struct:
            case TypeKind.Interface:
            case TypeKind.Rune:
            case TypeKind.List:
            case TypeKind.Dict:
            case TypeKind.Array:
            case TypeKind.HashSet:
            case TypeKind.Queue:
            case TypeKind.Delegate:
            case TypeKind.Tuple:
                return false;
        }
        return true;
    }

    // 値として Utf8Bytes を中に持つ型
    private static bool ContainsUtf8Bytes(TypeSym t)
    {
        var u = IlTypes.UnwrapNullable(t);
        if (u.Kind == TypeKind.Utf8Bytes) return true;
        if (u.Kind == TypeKind.Tuple)
        {
            foreach (var a in u.Args!)
            {
                if (ContainsUtf8Bytes(a)) return true;
            }
            return false;
        }
        if (u.Kind == TypeKind.Struct && u.Decl != null) return HasUtf8BytesField(u);
        return false;
    }

    private static bool HasUtf8BytesField(TypeSym t)
    {
        foreach (var m in LuaEmit.ValueMembers(t))
        {
            if (ContainsUtf8Bytes(m.Type)) return true;
        }
        return false;
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

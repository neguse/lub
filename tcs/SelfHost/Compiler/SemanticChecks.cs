using System.Collections.Generic;

namespace TcsSelf;

// 型の決まった木の上の準拠検査 (意味を見る規則。IDE の analyzer もこの compiler の診断を出す)。
// --ref の宣言と tcs が配る library は対象外
public sealed class SemanticChecks
{
    private readonly Compilation _c;
    // 0 との比較の operand になっている string.CompareOrdinal の呼び出し
    private readonly List<Node> _comparedWithZero = new List<Node>();
    // 文字列にする位置の式 (string の + の operand と Console.WriteLine の引数)。値の boxing にならない
    private readonly List<Node> _stringified = new List<Node>();
    // 歩いている型と method の宣言 (型引数の制約を引く)
    private Node? _typeDecl;
    // int / enum への cast の operand の中 (uint の定数を bit の並びとして受ける)
    private bool _uintBits;

    private static bool IsUIntType(TypeSym? t) => t != null && IlTypes.UnwrapNullable(t).Kind == TypeKind.UInt;
    private Node? _method;

    public SemanticChecks(Compilation c)
    {
        _c = c;
    }

    private void Report(string name, Node at)
    {
        _c.Error("TCS1001", "unsupported syntax: " + name, at);
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

    // 基底の並びの中で decl を指す型の構文 (無ければ型の宣言)
    private static Node BaseTypeNode(TypeDeclSym t, TypeDeclSym decl)
    {
        if (t.Decl.List3 != null)
        {
            foreach (var b in t.Decl.List3)
            {
                if (b.Text == decl.Name || b.Text.EndsWith("." + decl.Name)) return b;
            }
        }
        return t.Decl;
    }

    private void CheckType(TypeDeclSym t)
    {
        foreach (var i in t.Interfaces)
        {
            // host (--ref) の interface を user の型で実装すると、host の名前 (snake_case) の呼び出しが実装に届かない
            if (i.Decl != null && i.Decl.IsRef) Report("RefInterfaceImplementation(" + i.FullDisplay() + ")", BaseTypeNode(t, i.Decl));
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
        foreach (var iface in IlTypes.AllInterfaces(t))
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
        var name = IlNames.MethodName(m);
        foreach (var other in t.Members)
        {
            if (other == m) continue;
            var otherName = other.Kind == SymKind.Method && other.Decl != null && other.Decl.Kind == NodeKind.Method ? IlNames.MethodName(other) : other.Name;
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
                CheckCollectionNull(n);
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
                // uint は i32 と同じ表現なので、結果が 0〜2^31-1 を出うる演算は .NET と違う値になる
                if ((n.Text == "+" || n.Text == "-" || n.Text == "*" || n.Text == "<<") && IsUIntType(n.Type)) Report("UIntArithmetic", n);
                break;
            case NodeKind.IntLit:
                // 2^31 以上の uint の定数は i32 にならない (int への cast の operand と enum の member の値は 2 の補数の bit として受ける)
                if (IsUIntType(n.Type) && n.HasConst && n.Const != null && n.Const.I < 0 && n.Info != 1 && !_uintBits
                    && !(_typeDecl != null && _typeDecl.Value == DeclKinds.Enum))
                    Report("UIntRange", n);
                break;
            case NodeKind.InterpHole:
                if (n.A != null) _stringified.Add(n.A);
                break;
            case NodeKind.Unary:
                if (n.Text == "~" && IsUIntType(n.Type)) Report("UIntArithmetic", n);
                if (checkedContext && n.Text == "-" && n.Type != null && (n.Type.Kind != TypeKind.Int) && IlTypes.IsIntegral(n.Type) && n.Sym == null) Report("CheckedOperation", n);
                break;
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (checkedContext && IlTypes.IsIntegral(n.A!.Type)) Report("CheckedOperation", n);
                // byte の ++ / -- と複合代入は 0..255 に丸まらない
                if (IsByteType(n.A!.Type)) Report("ByteNarrowing", n);
                if (IsUIntType(n.A!.Type)) Report("UIntArithmetic", n);
                break;
            case NodeKind.Assign:
                CheckCollectionNull(n);
                if (checkedContext && n.Text != "=" && n.Text != "??=" && IlTypes.IsIntegral(n.A!.Type) && n.Text != "&=" && n.Text != "|=" && n.Text != "^=") Report("CheckedOperation", n);
                if (n.Text != "=" && n.Text != "??=" && IsByteType(n.A!.Type)) Report("ByteNarrowing", n);
                if ((n.Text == "+=" || n.Text == "-=" || n.Text == "*=" || n.Text == "<<=") && IsUIntType(n.A!.Type)) Report("UIntArithmetic", n);
                if (n.Text == "+=" && n.Type != null && n.Type.Kind == TypeKind.String && n.B != null) _stringified.Add(n.B);
                break;
            case NodeKind.Cast:
                // int と enum への cast の operand の uint の定数は bit の並びとして受ける (unchecked((int)0xFFFFFFFF))
                if (n.Type != null && (n.Type.Kind == TypeKind.Int || n.Type.Kind == TypeKind.Enum) && n.B != null && IsUIntType(Unwrap(n.B).Type))
                {
                    var outerBits = _uintBits;
                    _uintBits = true;
                    Walk(n.A, checkedContext);
                    Walk(n.B, checkedContext);
                    _uintBits = outerBits;
                    if (checkedContext && IsCheckedConversion(n)) Report("CheckedOperation", n);
                    CheckBoxingCast(n);
                    return;
                }
                if (checkedContext && IsCheckedConversion(n)) Report("CheckedOperation", n);
                CheckBoxingCast(n);
                CheckGenericTypeTest(n.B != null ? Unwrap(n.B).Type : null, n.Type, n, "UnverifiableCast");
                CheckRefTypeTest(n.B != null ? Unwrap(n.B).Type : null, n.Type, n, true);
                break;
            case NodeKind.Conv:
                CheckBoxing(n);
                break;
            case NodeKind.Foreach:
                CheckForeachBoxing(n);
                break;
            case NodeKind.Is:
                CheckTypeTestPattern(n.B, n.A!.Type);
                CheckBoxedValuePattern(n.B, n.A!.Type, null);
                break;
            case NodeKind.As:
                CheckTypeTest(n.A!.Type, n.Type, n);
                CheckGenericTypeTest(n.A.Type, n.Type, n, "UnverifiableTypeTest");
                CheckRefTypeTest(n.A.Type, n.Type, n, false);
                break;
            case NodeKind.SwitchExpr:
                if (n.List != null)
                {
                    foreach (var arm in n.List)
                    {
                        CheckTypeTestPattern(arm.A, n.A!.Type);
                        CheckBoxedValuePattern(arm.A, n.A!.Type, null);
                    }
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
                            if (label.Kind != NodeKind.CaseLabel) continue;
                            CheckTypeTestPattern(label.A, n.A!.Type);
                            CheckBoxedValuePattern(label.A, n.A!.Type, null);
                        }
                    }
                }
                break;
            case NodeKind.New:
                CheckDictionaryKey(n);
                CheckStructConstraint(n.Type, n);
                CheckCollectionNull(n);
                break;
            case NodeKind.NewArray:
                CheckCollectionNull(n);
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
        // 自作の method の out は、`out var` / `out _` の新しい変数と、宣言済みで lambda に capture されない局所変数だけ受ける。
        // multi-return は呼び出しが終わってから受け手に書くので、呼び出しの中から見える受け手 (field、配列の要素、parameter、capture された変数) は C# と違って見える
        if (m.Kind == SymKind.Method && m.Builtin == "" && m.Owner != null && !m.Owner.IsRef && call.List != null)
        {
            foreach (var a in call.List)
            {
                if (!a.Has(Mods.Out) || a.A == null) continue;
                var x = a.A;
                bool ok = x.Kind == NodeKind.PatDecl || x.Kind == NodeKind.PatDiscard || (x.Kind == NodeKind.Name && x.Text == "_" && x.Local == null && x.Sym == null)
                    || (x.Kind == NodeKind.Name && x.Local != null && !x.Local.IsParam && !x.Local.Captured);
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
            var values = Unwrap(call.List[1].A!).Type;
            // Dictionary の Keys / Values は key / value の型
            var elem = values != null && values.Kind == TypeKind.KeyCollection ? values.Arg(0)
                : values != null && values.Kind == TypeKind.ValueCollection ? values.Arg(1) : IlTypes.ElementOf(values);
            if (elem != null && !IsStringifiable(elem)) Report("UnsupportedToString(" + elem.FullDisplay() + ")", call.List[1].A!);
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
                if (IsStructForInterface(call.TypeArgs[i], m.TypeParamConstraints[i])) Report("StructInterfaceConstraint(" + call.TypeArgs[i].FullDisplay() + ")", call.A != null ? call.A : call);
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

    // ---- collection の null (TCS1003) ----

    // Lua の table は nil を値として持てない。Dictionary の値の null は key を消し、配列の null 要素と長さで作った配列の
    // nil の既定値は Length (# の長さ) を変える。参照実装の AnalyzeUnsupportedCollectionNulls と同じ地点で警告する
    private void CheckCollectionNull(Node n)
    {
        const string dictMessage = "Dictionary<K,V> cannot store null values because nil removes keys";
        switch (n.Kind)
        {
            case NodeKind.New:
                if (n.Type != null && n.Type.Kind == TypeKind.Dict && n.B != null && n.B.List != null)
                {
                    foreach (var item in n.B.List)
                    {
                        Node? value = null;
                        if (item.Kind == NodeKind.ArrayInit && item.List != null && item.List.Count == 2) value = item.List[1];
                        else if (item.Kind == NodeKind.IndexInit) value = item.B;
                        if (value != null && IsNilLiteralOrDefault(value))
                        {
                            CollectionNull(dictMessage, n);
                            return;
                        }
                    }
                }
                return;
            case NodeKind.Call:
                if (n.Sym != null && n.Sym.Name == "Add" && n.Sym.BuiltinOwner == "Dictionary" && n.Count >= 2 && n.Item(1).A != null && IsNilLiteralOrDefault(n.Item(1).A!))
                    CollectionNull(dictMessage, n);
                return;
            case NodeKind.Assign:
                if (n.Text != "=" || n.A == null || n.A.Kind != NodeKind.Index || n.B == null || !IsNilLiteralOrDefault(n.B)) return;
                var recv = n.A.A != null ? n.A.A.Type : null;
                if (recv == null) return;
                if (recv.Kind == TypeKind.Dict) CollectionNull(dictMessage, n);
                else if (recv.Kind == TypeKind.Array) CollectionNull(recv.Display() + " cannot store null elements in Lua sequence tables", n);
                return;
            case NodeKind.NewArray:
                if (n.Type == null || n.Type.Kind != TypeKind.Array) return;
                if (n.C != null)
                {
                    if (HasNilElement(n.C)) CollectionNull(n.Type.Display() + " cannot store null elements in Lua sequence tables", n);
                    return;
                }
                if (n.B == null || n.B.HasConst && n.B.Const != null && n.B.Const.Kind == ConstKind.Int && n.B.Const.I == 0) return;
                if (!CanDefaultToNil(n.Type.Elem)) return;
                CollectionNull(n.Type.Display() + " created by size cannot keep Length because nil elements do not exist in Lua sequence tables", n);
                return;
        }
    }

    private void CollectionNull(string message, Node at)
    {
        _c.Diags.Warning("TCS1003", "unsupported collection null: " + message, at.File, at.Pos);
    }

    private static bool HasNilElement(Node init)
    {
        if (init.List == null) return false;
        foreach (var e in init.List)
        {
            if (IsNilLiteralOrDefault(e)) return true;
        }
        return false;
    }

    // null か、nil になる型の default (変換の前の型で見る。default(int) は int? / object に変換しても null でない)
    private static bool IsNilLiteralOrDefault(Node e)
    {
        while ((e.Kind == NodeKind.Paren || e.Kind == NodeKind.Conv) && e.A != null) e = e.A;
        while (e.Kind == NodeKind.Cast && e.B != null)
        {
            e = e.B;
            while ((e.Kind == NodeKind.Paren || e.Kind == NodeKind.Conv) && e.A != null) e = e.A;
        }
        if (e.Kind == NodeKind.NullLit) return true;
        if (e.Kind != NodeKind.DefaultLit) return false;
        return e.Type != null && CanDefaultToNil(e.Type);
    }

    // 既定値が nil になる型 (参照型、Nullable、型引数)
    private static bool CanDefaultToNil(TypeSym? t)
    {
        if (t == null || t.IsError) return false;
        return !t.IsValueType || t.IsNullable || t.Kind == TypeKind.TypeParam;
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
    // C# の暗黙の数値の変換 (値の範囲が広がる)
    private static bool IsWideningNumeric(TypeKind from, TypeKind to)
    {
        bool fromIntegral = from == TypeKind.Int || from == TypeKind.UInt || from == TypeKind.Short || from == TypeKind.Byte || from == TypeKind.Char || from == TypeKind.Long;
        if (fromIntegral && (to == TypeKind.Float || to == TypeKind.Double)) return true;
        if (from == TypeKind.Float && to == TypeKind.Double) return true;
        switch (from)
        {
            case TypeKind.Byte: return to == TypeKind.Int || to == TypeKind.UInt || to == TypeKind.Short || to == TypeKind.Long;
            case TypeKind.Char: return to == TypeKind.Int || to == TypeKind.UInt || to == TypeKind.Long;
            case TypeKind.Short: return to == TypeKind.Int || to == TypeKind.Long;
            case TypeKind.Int: return to == TypeKind.Long;
            case TypeKind.UInt: return to == TypeKind.Long;
        }
        return false;
    }

    private static bool IsCheckedConversion(Node cast)
    {
        var target = cast.Type;
        var src = cast.B != null ? Unwrap(cast.B).Type : null;
        if (target == null || src == null) return false;
        if (!(src.IsNumeric || src.Kind == TypeKind.Enum) || !(target.IsNumeric || target.Kind == TypeKind.Enum)) return false;
        // 暗黙に変換できる cast (広げる変換) は overflow しない
        if (TypeSym.Same(src, target) || IsWideningNumeric(src.Kind, target.Kind)) return false;
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
        Report("ValueBoxing(" + u.FullDisplay() + ")", conv.A);
    }

    // 文字列にする式 (string の + の operand と Console.WriteLine の引数): ToString を持たない struct は boxing、文字列にできない型は止める
    private void CheckStringifiedOperand(Node e)
    {
        var x = Unwrap(e);
        var t = x.Type;
        if (t == null) return;
        var u = IlTypes.UnwrapNullable(t);
        // KeyValuePair の文字列化 (C# は [key, value] と書く) は写していない
        if (u.Kind == TypeKind.KeyValuePair)
        {
            Report("UnsupportedToString(" + t.FullDisplay() + ")", x);
            return;
        }
        if (IsBoxedStruct(t) && (IlTypes.IsNullableValueType(t) || !IlBuild.HasToStringOverride(u)))
        {
            if (!(e.Kind == NodeKind.Conv && IsReferenceTarget(e.Type!))) Report("StructBoxing", x);
            return;
        }
        // Rune は値そのものの文字列化だけ (Rune? は外)
        if (u.Kind == TypeKind.Rune && IlTypes.IsNullableValueType(t)) Report("UnsupportedToString(" + t.FullDisplay() + ")", x);
        // 文字列にできない型 (ToString を持たない class / interface、collection、delegate)
        if ((u.Kind == TypeKind.Class || u.Kind == TypeKind.Interface) && u.Decl != null && !IlBuild.HasToStringOverride(u)) Report("UnsupportedToString(" + t.FullDisplay() + ")", x);
        if (u.Kind == TypeKind.List || u.Kind == TypeKind.Dict || u.Kind == TypeKind.Array || u.Kind == TypeKind.HashSet || u.Kind == TypeKind.Queue || u.Kind == TypeKind.Delegate)
            Report("UnsupportedToString(" + t.FullDisplay() + ")", x);
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

    // ---- object に入った数と bool の pattern ----

    // object に入った数 (int / float) の型の test と、object に入った数と bool の値を pattern で読む形 (`o is int`、`o is int x`、`o is 5`、
    // `b is bool z`、`b is true`、`o is bool and true`)。IL は object の int と float を見分けず (どちらも number)、C backend は持たず、
    // VM は値を取り出せない。値は cast (`(int)o`) で取り出す。`o is bool` と string の pattern は対象外。
    // boxed は、`and` の左が object の値を bool と確かめた後の右側で、その型
    private void CheckBoxedValuePattern(Node? p, TypeSym? input, TypeSym? boxed)
    {
        if (p == null || input == null) return;
        bool fromObject = input.Kind == TypeKind.Object;
        switch (p.Kind)
        {
            case NodeKind.PatType:
                if (fromObject && IsNumberType(p.Type)) Report("BoxedValuePattern(" + p.Type!.FullDisplay() + ")", p);
                return;
            case NodeKind.PatDecl:
                if (fromObject && (IsNumberType(p.Type) || IsBoolType(p.Type))) Report("BoxedValuePattern(" + p.Type!.FullDisplay() + ")", p);
                else if (boxed != null) Report("BoxedValuePattern(" + boxed.FullDisplay() + ")", p);
                return;
            case NodeKind.PatProp:
                if (fromObject && (IsNumberType(p.Type) || (IsBoolType(p.Type) && (p.Text != "" || (p.List != null && p.List.Count > 0)))))
                    Report("BoxedValuePattern(" + p.Type!.FullDisplay() + ")", p);
                return;
            case NodeKind.PatConst:
                if (fromObject && p.A != null && (IsNumberType(p.A.Type) || IsBoolType(p.A.Type))) Report("BoxedValuePattern(" + p.A.Type!.FullDisplay() + ")", p);
                else if (boxed != null) Report("BoxedValuePattern(" + boxed.FullDisplay() + ")", p);
                return;
            case NodeKind.PatVar:
            case NodeKind.PatRel:
                if (boxed != null) Report("BoxedValuePattern(" + boxed.FullDisplay() + ")", p);
                return;
            case NodeKind.PatAnd:
            {
                CheckBoxedValuePattern(p.A, input, boxed);
                bool typeTest = p.A != null && (p.A.Kind == NodeKind.PatType || p.A.Kind == NodeKind.PatDecl);
                // 数の型の test は左で報告済み。bool は右の pattern が値を読むときに報告する
                var narrowed = boxed;
                if (narrowed == null && fromObject && typeTest && IsBoolType(p.A!.Type)) narrowed = p.A.Type;
                CheckBoxedValuePattern(p.B, typeTest ? p.A!.Type : input, narrowed);
                return;
            }
            case NodeKind.PatOr:
                CheckBoxedValuePattern(p.A, input, boxed);
                CheckBoxedValuePattern(p.B, input, boxed);
                return;
            case NodeKind.PatNot:
                CheckBoxedValuePattern(p.A, input, boxed);
                return;
        }
    }

    private static bool IsNumberType(TypeSym? t) => t != null && t.IsNumeric;

    private static bool IsBoolType(TypeSym? t) => t != null && t.Kind == TypeKind.Bool;

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
                CheckRefTypeTest(input, p.Type, p, false);
                return;
            case NodeKind.PatProp:
                if (p.A != null)
                {
                    CheckTypeTest(input, p.Type, p);
                    CheckGenericTypeTest(input, p.Type, p, "UnverifiableTypeTest");
                    CheckRefTypeTest(input, p.Type, p, false);
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
        // --ref の generic でない interface は別の名前 (Lua は止める。tcs VM は型の表で判定する、object-model §10.6)
        if (refInput && !generic && type.Decl != null && type.Decl.IsRef) Report("RefInterfaceTypeTest(" + type.FullDisplay() + ")", at);
        else if (!refInput || generic || type.Decl == null) Report("InterfaceTypeTest", at);
    }

    // --ref の generic でない class (と cast の interface) への型 test と cast。Lua は host の値の型を見分けられないので止める。
    // 値を実行時に調べる (参照型か型引数の入力で、暗黙の変換が無い) ときだけ
    private void CheckRefTypeTest(TypeSym? input, TypeSym? type, Node at, bool cast)
    {
        if (input == null || type == null || type.Decl == null || !type.Decl.IsRef || type.Decl.TypeParams.Count > 0) return;
        if (type.Kind != TypeKind.Class && !(cast && type.Kind == TypeKind.Interface)) return;
        bool runtime = input.Kind == TypeKind.Object || input.Kind == TypeKind.Class || input.Kind == TypeKind.Interface
            || input.Kind == TypeKind.String || input.Kind == TypeKind.TypeParam;
        if (!runtime || ImplicitReference(input, type)) return;
        Report((cast ? "RefCast(" : "RefTypeTest(") + type.FullDisplay() + ")", at);
    }

    // 参照の暗黙の変換 (同じ型、基底 class、実装する interface)
    private static bool ImplicitReference(TypeSym from, TypeSym to)
    {
        if (TypeSym.Same(from, to)) return true;
        TypeDeclSym? cur = from.Decl;
        while (cur != null)
        {
            if (cur == to.Decl) return true;
            foreach (var i in cur.Interfaces)
            {
                if (i.Decl == to.Decl) return true;
            }
            cur = cur.BaseClass;
        }
        return false;
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
        // --ref の generic な型は実行時の型の表現を持たない
        if (name == "UnverifiableTypeTest" && type.Decl != null && type.Decl.IsRef) Report("TypeTestWithoutRuntimeType", at);
        else Report(name + "(" + type.FullDisplay() + ")", at);
    }

    private void CheckStructConstraint(TypeSym? created, Node at)
    {
        if (created == null || created.Decl == null || created.Args == null) return;
        var constraints = created.Decl.TypeParamConstraints;
        for (int i = 0; i < created.Args.Count && i < constraints.Count; i++)
        {
            if (IsStructForInterface(created.Args[i], constraints[i])) Report("StructInterfaceConstraint(" + created.Args[i].FullDisplay() + ")", at);
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
        foreach (var m in IlTypes.ValueMembers(t))
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
            if (HasValueEquality(e) || e.Kind == TypeKind.Float || e.Kind == TypeKind.Double) Report("UnsupportedHashSetElement(" + e.FullDisplay() + ")", creation);
            return;
        }
        if (t.Kind != TypeKind.Dict) return;
        var k = t.Arg(0);
        if (HasValueEquality(k)) Report("ValueEqualityDictionaryKey(" + k.FullDisplay() + ")", creation);
        // float の key は HashSet<float> の要素と同じく対応外 (C backend は持たず、VM は NaN の key と列挙を持たない)
        else if (k.Kind == TypeKind.Float || k.Kind == TypeKind.Double) Report("UnsupportedDictionaryKey(" + k.FullDisplay() + ")", creation);
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

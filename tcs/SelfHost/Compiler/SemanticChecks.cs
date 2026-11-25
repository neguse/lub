using System.Collections.Generic;

namespace TcsSelf;

// Compliance checks on the typed tree (rules that look at semantics; the IDE analyzer also reports this compiler's diagnostics).
// --ref declarations and the libraries shipped with tcs are excluded
public sealed class SemanticChecks
{
    private readonly Compilation _c;
    // string.CompareOrdinal calls that are operands of a comparison with 0
    private readonly List<Node> _comparedWithZero = new List<Node>();
    // Expressions in stringification position (operands of string + and Console.WriteLine arguments). They do not box the value
    private readonly List<Node> _stringified = new List<Node>();
    // The type and method declarations being walked (used to look up type argument constraints)
    private Node? _typeDecl;
    // Inside the operand of a cast to int / enum (a uint constant is taken as a bit pattern)
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

    // ---- Type declarations ----

    // The type syntax in the base list that refers to decl (the type declaration if there is none)
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
            // If a user type implements a host (--ref) interface, calls by the host name (snake_case) do not reach the implementation
            if (i.Decl != null && i.Decl.IsRef) Report("RefInterfaceImplementation(" + i.FullDisplay() + ")", BaseTypeNode(t, i.Decl));
        }
        if (!t.IsInterface && !t.IsEnum) CheckInterfaceMembers(t);
        // Records with a Utf8Bytes field are rejected (.NET record value equality compares the byte[] inside Utf8Bytes by reference)
        if (t.IsRecord && HasUtf8BytesField(t.SelfType())) Report("Utf8BytesInRecord", t.Decl);
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Method && m.Decl != null && m.Decl.Kind == NodeKind.Method && IsCollidingOverload(t, m)) Report("MethodOverload", m.Decl);
        }
    }

    // Whether the interface members are implemented (CS0535)
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

    // Overloads get a separate key by appending the parameter types to the name. A declaration whose resulting name has the same spelling as another member of the same type is rejected
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

    // ---- Expressions and statements ----

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
                // uint has the same representation as i32, so an operation whose result can leave 0 to 2^31-1 gives a different value from .NET
                if ((n.Text == "+" || n.Text == "-" || n.Text == "*" || n.Text == "<<") && IsUIntType(n.Type)) Report("UIntArithmetic", n);
                break;
            case NodeKind.IntLit:
                // A uint constant of 2^31 or more does not fit in i32 (the operand of a cast to int and enum member values are taken as two's-complement bits)
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
                // ++ / -- and compound assignment on byte do not wrap to 0..255
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
                // A uint constant as the operand of a cast to int or enum is taken as a bit pattern (unchecked((int)0xFFFFFFFF))
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

    // ---- Calls ----

    private void CheckCall(Node call)
    {
        var m = call.Sym;
        if (m == null) return;
        // out of a user-defined method accepts only new variables from `out var` / `out _` and already-declared locals that are not captured by a lambda.
        // Multi-return writes to the targets after the call finishes, so targets visible from inside the call (fields, array elements, parameters, captured variables) would look different from C#
        // Passing the same local to two outs is rejected, because inside the call the two parameters become aliases
        if (m.Kind == SymKind.Method && m.Builtin == "" && m.Owner != null && !m.Owner.IsRef && call.List != null)
        {
            var outLocals = new List<LocalSym>();
            foreach (var a in call.List)
            {
                if (!a.Has(Mods.Out) || a.A == null) continue;
                var x = a.A;
                bool ok = x.Kind == NodeKind.PatDecl || x.Kind == NodeKind.PatDiscard || (x.Kind == NodeKind.Name && x.Text == "_" && x.Local == null && x.Sym == null)
                    || (x.Kind == NodeKind.Name && x.Local != null && !x.Local.IsParam && !x.Local.Captured);
                if (!ok) Report("OutArgumentNotDeclaration", a);
                else if (x.Kind == NodeKind.Name && x.Local != null)
                {
                    if (outLocals.Contains(x.Local)) Report("OutArgumentDuplicate", a);
                    else outLocals.Add(x.Local);
                }
            }
        }
        // `?.ToString()` on Rune? is outside the subset
        if (m.Builtin == "Rune.ToString" && call.A != null && call.A.Kind == NodeKind.Member && call.A.Value == 1) Report("UnsupportedToString(System.Text.Rune)", call);
        // Console.WriteLine arguments are stringified
        if ((m.Builtin == "Console.WriteLine" || m.Builtin == "Console.Write") && call.List != null)
        {
            foreach (var a in call.List)
            {
                if (a.A == null) continue;
                _stringified.Add(a.A);
                CheckStringifiedOperand(a.A);
            }
        }
        // string.Join elements must be of a stringifiable type
        if ((m.Builtin == "String.Join" || m.Builtin == "String.JoinAny") && call.List != null && call.List.Count == 2)
        {
            var values = Unwrap(call.List[1].A!).Type;
            // Dictionary's Keys / Values have the key / value type
            var elem = values != null && values.Kind == TypeKind.KeyCollection ? values.Arg(0)
                : values != null && values.Kind == TypeKind.ValueCollection ? values.Arg(1) : IlTypes.ElementOf(values);
            if (elem != null && !IsStringifiable(elem)) Report("UnsupportedToString(" + elem.FullDisplay() + ")", call.List[1].A!);
        }
        // Contains / IndexOf / Remove on a List whose elements contain Utf8Bytes are rejected (.NET equality compares the byte[] inside Utf8Bytes by reference)
        if ((m.Builtin == "List.Contains" || m.Builtin == "List.IndexOf" || m.Builtin == "List.Remove") && call.A != null && call.A.Kind == NodeKind.Member && call.A.A != null)
        {
            var lt = call.A.A.Type;
            if (lt != null && lt.Kind == TypeKind.List && ContainsUtf8Bytes(lt.Elem))
                _c.Error("TCS1002", "unsupported API: System.Collections.Generic.List<T>." + m.Name, call.A);
        }
        // Calls that pass a struct to a type argument with an interface constraint are rejected
        if (m.Kind == SymKind.Method && m.Builtin == "" && m.TypeParams != null && call.TypeArgs != null && m.TypeParamConstraints != null)
        {
            for (int i = 0; i < call.TypeArgs.Count && i < m.TypeParamConstraints.Count; i++)
            {
                if (IsStructForInterface(call.TypeArgs[i], m.TypeParamConstraints[i])) Report("StructInterfaceConstraint(" + call.TypeArgs[i].FullDisplay() + ")", call.A != null ? call.A : call);
            }
        }
        // Only the sign of string.CompareOrdinal's value is guaranteed, so it can only be used as an operand of a comparison with 0
        if (m.Builtin == "String.CompareOrdinal" && !_comparedWithZero.Contains(call)) Report("CompareOrdinalValue", call);
        // Overloads of host (--ref) types do not get types appended to their names, so they cannot be told apart
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

    // ---- null in collections (TCS1003) ----

    // Warns about null Dictionary values, null array elements, and arrays created with a length whose element default is null (TCS1003)
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

    // null, or the default of a type that becomes nil (judged by the type before conversion; default(int) is not null even when converted to int? / object)
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

    // Types whose default value is nil (reference types, Nullable, type arguments)
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

    // checked detects overflow only for i32 + - * / % and unary - (other checked operations are rejected)
    private static bool IsCheckedArithmetic(Node bin)
    {
        var op = bin.Text;
        if (op != "+" && op != "-" && op != "*" && op != "/" && op != "%") return false;
        if (bin.Sym != null && bin.Sym.Builtin == "" && bin.Sym.Kind == SymKind.Method) return false;
        var t = bin.Type;
        if (t == null || !IlTypes.IsIntegral(t)) return false;
        return t.Kind != TypeKind.Int;
    }

    // The only explicit numeric conversion is float / double → int (out-of-range values fault, as with division)
    // C#'s implicit numeric conversions (the value range widens)
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
        // A cast that is an implicit (widening) conversion does not overflow
        if (TypeSym.Same(src, target) || IsWideningNumeric(src.Kind, target.Kind)) return false;
        return !((src.Kind == TypeKind.Float || src.Kind == TypeKind.Double) && target.Kind == TypeKind.Int);
    }

    // ---- Struct boxing ----

    // Conversions that put a struct into object / interface are rejected (except in stringification position for a struct that overrides ToString)
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
        // Conversions that put value types other than int / float / bool (char, byte, enum, etc.) into object are rejected (except in stringification position)
        var u = IlTypes.UnwrapNullable(src);
        if (!u.IsValueType || u.Kind == TypeKind.Int || u.Kind == TypeKind.Float || u.Kind == TypeKind.Bool) return;
        if (IsStringified(conv)) return;
        Report("ValueBoxing(" + u.FullDisplay() + ")", conv.A);
    }

    // Stringified expressions (operands of string + and Console.WriteLine arguments): a struct without ToString is boxing, and non-stringifiable types are rejected
    private void CheckStringifiedOperand(Node e)
    {
        var x = Unwrap(e);
        var t = x.Type;
        if (t == null) return;
        var u = IlTypes.UnwrapNullable(t);
        // Stringification of KeyValuePair (C# writes [key, value]) is not implemented
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
        // Rune is only stringified as the value itself (Rune? is excluded)
        if (u.Kind == TypeKind.Rune && IlTypes.IsNullableValueType(t)) Report("UnsupportedToString(" + t.FullDisplay() + ")", x);
        // Non-stringifiable types (class / interface without ToString, collections, delegates)
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

    private static bool IsBoxedStruct(TypeSym t)
    {
        var u = IlTypes.UnwrapNullable(t);
        return (u.Kind == TypeKind.Struct && u.Decl != null) || u.Kind == TypeKind.Utf8Bytes || u.Kind == TypeKind.Tuple;
    }

    private static bool IsReferenceTarget(TypeSym t)
    {
        return t.Kind == TypeKind.Object || t.Kind == TypeKind.Interface;
    }

    // ---- Patterns on numbers and bools stored in object ----

    // Type tests on numbers (int / float) stored in object, and forms that read number and bool values stored in object with a pattern (`o is int`, `o is int x`, `o is 5`,
    // `b is bool z`, `b is true`, `o is bool and true`). IL does not distinguish int and float in object (both are number), the C backend does not support them,
    // and the VM cannot extract the value. Extract the value with a cast (`(int)o`). `o is bool` and string patterns are not covered.
    // boxed is that type, on the right side of an `and` after the left side has confirmed that the object value is a bool
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
                    // Type tests on numbers were already reported on the left. For bool, it is reported when the right pattern reads the value
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

    // ---- Interface type checks ----

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

    // An interface check on a class is decided by its implementation list. Interfaces not in the list (host, generic) and inputs without a list (not reference types) are rejected
    private void CheckTypeTest(TypeSym? input, TypeSym? type, Node at)
    {
        if (type == null || type.Kind != TypeKind.Interface) return;
        bool refInput = input != null && (input.Kind == TypeKind.Class || input.Kind == TypeKind.Interface || input.Kind == TypeKind.Object || input.Kind == TypeKind.String
            || input.Kind == TypeKind.TypeParam && IsClassConstrained(input));
        bool generic = type.Decl != null && type.Decl.TypeParams.Count > 0;
        // Non-generic --ref interfaces have a separate name (tcs check and tcs c reject them, and the tcs VM decides by the type table; object-model §10.6)
        if (refInput && !generic && type.Decl != null && type.Decl.IsRef) Report("RefInterfaceTypeTest(" + type.FullDisplay() + ")", at);
        else if (!refInput || generic || type.Decl == null) Report("InterfaceTypeTest", at);
    }

    // Type tests and casts to non-generic --ref classes (and interfaces for casts). tcs check and tcs c reject them, and the tcs VM decides by the type table.
    // Only when the value is inspected at run time (a reference-type or type-argument input with no implicit conversion)
    private void CheckRefTypeTest(TypeSym? input, TypeSym? type, Node at, bool cast)
    {
        if (input == null || type == null || type.Decl == null || !type.Decl.IsRef || type.Decl.TypeParams.Count > 0) return;
        if (type.Kind != TypeKind.Class && !(cast && type.Kind == TypeKind.Interface)) return;
        bool runtime = input.Kind == TypeKind.Object || input.Kind == TypeKind.Class || input.Kind == TypeKind.Interface
            || input.Kind == TypeKind.String || input.Kind == TypeKind.TypeParam;
        if (!runtime || ImplicitReference(input, type)) return;
        Report((cast ? "RefCast(" : "RefTypeTest(") + type.FullDisplay() + ")", at);
    }

    // Implicit reference conversions (same type, base class, implemented interface)
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

    // Whether the type argument is constrained to reference types (`where T : class`)
    private bool IsClassConstrained(TypeSym tp)
    {
        var holder = tp.IsMethodParam ? _method : _typeDecl;
        if (holder == null || holder.List2 == null || tp.Index >= holder.List2.Count) return false;
        return (holder.List2[tp.Index].Value & 1) != 0;
    }

    // ---- Equality and generics ----

    // == / != on delegates (except comparison with null), and comparison of Utf8Bytes with null (Utf8Bytes is a struct)
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

    // Type checks / casts to generic classes are rejected
    // Only when the value is inspected at run time (a reference-type or type-argument input with no implicit conversion)
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
        // Generic --ref types have no runtime type representation
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

    // Stringifiable types (string.Join elements)
    private static bool IsStringifiable(TypeSym t)
    {
        var u = IlTypes.UnwrapNullable(t);
        switch (u.Kind)
        {
            // The runtime stringifies the elements, so ToString overrides cannot be called
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

    // Types that contain Utf8Bytes as a value
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

    // ---- Keys compared by value ----

    // Dictionary and HashSet with keys compared by value (struct, tuple, record, etc.) or with float keys are rejected
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
        // float keys are unsupported, like HashSet<float> elements (the C backend does not support them, and the VM does not support NaN keys and enumeration)
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

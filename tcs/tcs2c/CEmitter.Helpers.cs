using System.Globalization;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private CType TypeOfIsType(IlIsType typeTest)
    {
        if (!_classes.ContainsKey(typeTest.TypeRef))
            throw new Tcs2cException($"IlIsType target is not a class: {typeTest.TypeRef}");
        var operand = TypeOf(typeTest.E);
        if (operand.Kind is not (CTypeKind.Ref or CTypeKind.Null or CTypeKind.Object))
            throw new Tcs2cException($"IlIsType operand is not a class reference: {operand}");
        return CType.Bool;
    }

    // 明示 downcast: null は素通し、実行時型が範囲外なら fault (C# の
    // InvalidCastException 相当)
    private CType TypeOfCast(IlCast cast)
    {
        if (!_classes.ContainsKey(cast.TypeRef))
            throw new Tcs2cException($"IlCast target is not a class: {cast.TypeRef}");
        var source = TypeOf(cast.E);
        if (source.Kind is not (CTypeKind.Ref or CTypeKind.Null or CTypeKind.Object))
            throw new Tcs2cException($"IlCast operand is not a class reference: {source}");
        return CType.Ref(cast.TypeRef);
    }

    private string RenderCast(IlCast cast)
    {
        var type = TypeOfCast(cast);
        return $"(({type.CName})tcs_cast({RenderExpr(cast.E)}, " +
            $"{Names.TypeId(cast.TypeRef)}, {Names.TypeIdMax(cast.TypeRef)}))";
    }

    // 数値 cast (int / float 目標)。float → int は IL が __tcs_trunc で
    // 明示するので、ここに来るのは int ↔ float の拡大と恒等 (char / enum)、
    // T? / object からの unbox
    private CType TypeOfNumericConvert(IlNumericConvert convert)
    {
        var target = convert.TargetType == "int" ? CType.I32 : CType.F32;
        var source = TypeOf(convert.Value);
        if (source == CType.Object) return target;
        if (source.Kind == CTypeKind.Nullable) source = source.Element!;
        if (source.Kind is not (CTypeKind.I32 or CTypeKind.F32))
            throw new Tcs2cException($"numeric cast of non-numeric operand: {source}");
        return target;
    }

    private string RenderNumericConvert(IlNumericConvert convert)
    {
        var target = TypeOfNumericConvert(convert);
        var source = TypeOf(convert.Value);
        if (source == CType.Object) return RenderUnbox(convert.Value, target);
        if (source.Kind == CTypeKind.Nullable)
        {
            // (int)x で x が T?: 値なしは fault (.Value と同じ)
            var temp = Temp("opt");
            return $"({{ {source.CName} {temp} = {RenderExpr(convert.Value)}; " +
                $"if (!{temp}.has) tcs_fault(\"nullable-value\"); ({target.CName}){temp}.v; }})";
        }
        if (source == target) return RenderExpr(convert.Value);
        if (target == CType.I32) return $"tcs_trunc_f32({RenderExpr(convert.Value)})";
        return $"((float)({RenderExpr(convert.Value)}))";
    }

    // 参照型 / bool / T? / object への cast。user class の downcast は IL が
    // IlCast で明示する。object からは実行時 tag で検査 (C# の
    // InvalidCastException 相当は fault)
    private CType TypeOfRefCast(IlRefCast cast)
    {
        var target = _facts.MapType(cast.TargetType);
        var source = TypeOf(cast.Value);
        if (source == target || target.CanAssignFrom(source)) return target;
        if (source == CType.Object
            && (target.IsNullable || target.Kind == CTypeKind.Nullable || target == CType.Bool))
            return target;
        // interface が絡む cast は静的な継承関係が無くても成立しうる
        // (実行時に tcs_interface_cast / tcs_cast が検査する)
        if (source.Kind == CTypeKind.Ref && target.Kind == CTypeKind.Ref
            && (IsAncestorOrSame(source.Name!, target.Name!)
                || IsInterface(source.Name!) || IsInterface(target.Name!)))
            return target;
        throw new Tcs2cException($"unsupported cast: {source} to {target}");
    }

    private string RenderRefCast(IlRefCast cast)
    {
        var target = TypeOfRefCast(cast);
        var source = TypeOf(cast.Value);
        if (target == CType.Object) return RenderBox(cast.Value);
        if (source != CType.Object && (source == target || target.CanAssignFrom(source)))
            return RenderCoerced(cast.Value, target);
        var value = RenderExpr(cast.Value);
        if (target.Kind == CTypeKind.Nullable)
        {
            var temp = Temp("obj");
            var field = BoxField(target.Element!);
            return $"({{ void *{temp} = {value}; {temp} == NULL ? ({target.CName}){{0}} " +
                $": ({target.CName}){{ true, tcs_unbox({temp}, {RuntimeTypeId(target.Element!)})->value.{field} }}; }})";
        }
        if (target == CType.Bool) return RenderUnbox(cast.Value, target);
        if (target.Kind == CTypeKind.Ref && IsInterface(target.Name!))
            return $"(({target.CName})tcs_interface_cast({value}, {Names.InterfaceCheck(target.Name!)}))";
        if (target.Kind == CTypeKind.Ref)
            return $"(({target.CName})tcs_cast({value}, {Names.TypeId(target.Name!)}, " +
                $"{Names.TypeIdMax(target.Name!)}))";
        var id = RuntimeTypeId(target);
        return $"(({target.CName})tcs_cast({value}, {id}, {id}))";
    }

    private string RenderIsType(IlIsType typeTest)
    {
        _ = TypeOfIsType(typeTest);
        var value = RenderExpr(typeTest.E);
        if (!Effectful(typeTest.E))
            return $"({value} != NULL && tcs_type_in_range(" +
                $"TCS_GC_HEADER({value})->type_id, " +
                $"{Names.TypeId(typeTest.TypeRef)}, {Names.TypeIdMax(typeTest.TypeRef)}))";
        var temp = Temp("is_object");
        return $"({{ void *{temp} = {value}; {temp} != NULL && " +
            $"tcs_type_in_range(((TcsObjectHeader *){temp})->type_id, " +
            $"{Names.TypeId(typeTest.TypeRef)}, {Names.TypeIdMax(typeTest.TypeRef)}); }})";
    }

    private static CType TypeOfLiteral(IlLit literal)
    {
        var text = literal.LuaText;
        if (text is "true" or "false") return CType.Bool;
        if (text == "nil") return CType.Null;
        if (text.StartsWith('"')) return CType.String;
        return literal.Type == "float" || IsFloatText(text) ? CType.F32 : CType.I32;
    }

    private string RenderLiteral(IlLit literal)
    {
        var text = literal.LuaText;
        if (text is "true" or "false") return text;
        if (text == "nil") return "NULL";
        if (text.StartsWith('"')) return RenderStringLiteral(text);
        if (literal.Type == "float" || IsFloatText(text))
        {
            if (!float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
                throw new Tcs2cException($"invalid f32 literal: {text}");
            return Constants.F32(value);
        }
        int integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var bits))
                throw new Tcs2cException($"invalid i32 literal: {text}");
            integer = unchecked((int)bits);
        }
        else if (!int.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out integer))
            throw new Tcs2cException($"invalid i32 literal: {text}");
        return Constants.I32(integer);
    }

    private string RenderStringLiteral(string luaText) =>
        InternStringLiteral(DecodeLuaString(luaText));

    private static byte[] DecodeLuaString(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
            throw new Tcs2cException($"invalid Lua string literal: {text}");
        var decoded = new StringBuilder();
        for (var i = 1; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (ch != '\\')
            {
                decoded.Append(ch);
                continue;
            }
            if (++i >= text.Length - 1)
                throw new Tcs2cException($"invalid Lua string escape: {text}");
            switch (text[i])
            {
                case '\\': decoded.Append('\\'); break;
                case '"': decoded.Append('"'); break;
                case 'n': decoded.Append('\n'); break;
                case 'r': decoded.Append('\r'); break;
                case 't': decoded.Append('\t'); break;
                case 'x' when i + 2 < text.Length - 1
                    && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var value):
                    decoded.Append((char)value);
                    i += 2;
                    break;
                default:
                    throw new Tcs2cException($"unsupported Lua string escape: \\{text[i]}");
            }
        }
        try
        {
            return new UTF8Encoding(false, true).GetBytes(decoded.ToString());
        }
        catch (EncoderFallbackException)
        {
            throw new Tcs2cException("string literal contains an isolated UTF-16 surrogate");
        }
    }

    private static bool IsFloatText(string text) =>
        text.Contains('.') || text.Contains('e', StringComparison.OrdinalIgnoreCase);

    private static CType NumericJoin(CType left, CType right, string where)
    {
        if (left.Kind is not (CTypeKind.I32 or CTypeKind.F32)
            || right.Kind is not (CTypeKind.I32 or CTypeKind.F32))
            throw new Tcs2cException($"{where} operands are not numeric: {left}, {right}");
        return left.Kind == CTypeKind.F32 || right.Kind == CTypeKind.F32
            ? CType.F32 : CType.I32;
    }

    private static CType CommonType(CType left, CType right, string where)
    {
        if (left == right) return left;
        if (left == CType.Null && right.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool)
            return CType.Nullable(right);
        if (right == CType.Null && left.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool)
            return CType.Nullable(left);
        if (left.Kind == CTypeKind.Nullable && left.CanAssignFrom(right)) return left;
        if (right.Kind == CTypeKind.Nullable && right.CanAssignFrom(left)) return right;
        if (left.Kind is CTypeKind.I32 or CTypeKind.F32
            && right.Kind is CTypeKind.I32 or CTypeKind.F32)
            return NumericJoin(left, right, where);
        if (left.Kind == CTypeKind.Null && right.IsNullable) return right;
        if (right.Kind == CTypeKind.Null && left.IsNullable) return left;
        if (left.Kind == CTypeKind.List && right.Kind == CTypeKind.List)
        {
            if (left.Element is null) return right;
            if (right.Element is null) return left;
        }
        throw new Tcs2cException($"incompatible {where}: {left}, {right}");
    }

    private void RequireComparable(CType left, CType right, string where)
    {
        // object: 参照同一性 (box 同士も pointer 比較 = C# と同じ)
        if (left == CType.Object && (right.IsNullable || right == CType.Null)
            || right == CType.Object && (left.IsNullable || left == CType.Null)) return;
        if (left.Kind == CTypeKind.Nullable || right.Kind == CTypeKind.Nullable)
        {
            if (left == CType.Null || right == CType.Null) return;
            RequireComparable(left.Kind == CTypeKind.Nullable ? left.Element! : left,
                right.Kind == CTypeKind.Nullable ? right.Element! : right, where);
            return;
        }
        if (left.Kind == CTypeKind.Ref && right.Kind == CTypeKind.Ref
            && (IsAncestorOrSame(left.Name!, right.Name!) || IsAncestorOrSame(right.Name!, left.Name!))) return;
        if (left.Kind is CTypeKind.I32 or CTypeKind.F32
            && right.Kind is CTypeKind.I32 or CTypeKind.F32) return;
        if (left == right && left.Kind is CTypeKind.Bool or CTypeKind.String
            or CTypeKind.Ref or CTypeKind.Array or CTypeKind.List
            or CTypeKind.Random) return;
        if (left.Kind == CTypeKind.Null && right.IsNullable
            || right.Kind == CTypeKind.Null && left.IsNullable) return;
        if (left.Kind == CTypeKind.Nullable && right.Kind is CTypeKind.Null or CTypeKind.Nullable
            || right.Kind == CTypeKind.Nullable && left.Kind is CTypeKind.Null or CTypeKind.Nullable)
            return;
        if (left.Kind == CTypeKind.Nullable && left.Element!.CanAssignFrom(right)
            || right.Kind == CTypeKind.Nullable && right.Element!.CanAssignFrom(left))
            return;
        throw new Tcs2cException($"incompatible {where} operands: {left}, {right}");
    }

    private static void RequireType(CType expected, CType actual, string where)
    {
        if (expected != actual)
            throw new Tcs2cException($"{where}: expected {expected}, got {actual}");
    }

    // closure / static method group は単独で型付けできない (target で決まる)
    private bool IsClosureValue(IlExpr expr) =>
        expr is IlClosure
        || expr is IlField { Recv: IlVar recv } group && TryResolve(recv.Name) is null
            && _classes.TryGetValue(recv.Name, out var cls)
            && cls.Methods.Any(m => m.Name == group.Name && m.IsStatic);

    private void CheckAssignable(CType target, IlExpr value, string where)
    {
        if (target.Kind == CTypeKind.Closure && value is IlClosure) return;
        if (target.Kind == CTypeKind.Closure
            && value is IlField { Recv: IlVar recvVar } group
            && _classes.ContainsKey(recvVar.Name)
            && _classes[recvVar.Name].Methods
                .Any(m => m.Name == group.Name && m.IsStatic))
            return;
        RequireAssignable(target, TypeOf(value), where);
    }

    private void RequireAssignable(CType target, CType source, string where)
    {
        if (target.CanAssignFrom(source)) return;
        // 継承 upcast: Derived → Base (il-spec §9)。downcast (pattern local
        // への代入 / 明示 cast) も C の cast で通す — cast は型消去で
        // 透過 (support-matrix)、is-pattern は直後の IlIsType が守る
        if (target.Kind == CTypeKind.Ref && source.Kind == CTypeKind.Ref
            && (IsAncestorOrSame(target.Name!, source.Name!)
                || IsAncestorOrSame(source.Name!, target.Name!)))
            return;
        throw new Tcs2cException($"{where}: cannot assign {source} to {target}");
    }

    // upcast が必要なら C の明示 cast を挟んで render する。
    // closure は型付き文脈でのみ生成できる (IlClosure は引数型を持たない)
    private string RenderCoerced(IlExpr expr, CType target)
    {
        if (target == CType.Object) return RenderBox(expr);
        if (target.Kind == CTypeKind.Closure)
        {
            if (expr is IlClosure closure)
                return RenderClosure(closure, target);
            if (expr is IlField { Recv: IlVar recvVar } group
                && _classes.ContainsKey(recvVar.Name)
                && _classes[recvVar.Name].Methods
                    .Any(m => m.Name == group.Name && m.IsStatic))
                return RenderStaticGroupThunk(recvVar.Name, group.Name, target);
        }
        var source = TypeOf(expr);
        // T? への暗黙変換 (IL の IlNullableWrap が無い地点の安全網)
        if (target.Kind == CTypeKind.Nullable && source != target)
        {
            if (source.Kind == CTypeKind.Null) return $"(({target.CName}){{0}})";
            return $"(({target.CName}){{ true, {RenderCoerced(expr, target.Element!)} }})";
        }
        var rendered = RenderExpr(expr);
        if (target.Kind == CTypeKind.Ref && source.Kind == CTypeKind.Ref
            && target.Name != source.Name)
            return $"(({target.CName}){rendered})";
        return rendered;
    }

    private static bool Effectful(IlExpr expr) => expr switch
    {
        IlLit literal => literal.LuaText.StartsWith('"'),
        IlVar => false,
        IlParen paren => Effectful(paren.E),
        IlNumericConvert convert => Effectful(convert.Value),
        IlRefCast cast => Effectful(cast.Value),
        IlUn unary => Effectful(unary.E),
        IlBin binary => Effectful(binary.L) || Effectful(binary.R),
        IlTernary ternary => Effectful(ternary.Cond)
            || Effectful(ternary.T) || Effectful(ternary.F),
        IlIsType typeTest => Effectful(typeTest.E),
        IlCast cast => Effectful(cast.E),
        IlNullableWrap w => Effectful(w.E),
        IlNullableHasValue h => Effectful(h.E),
        IlNullableValue => true,
        IlNullableGetOrDefault g => Effectful(g.E) || Effectful(g.Default),
        IlLiftedBin lb => Effectful(lb.L) || Effectful(lb.R),
        IlLiftedUn lu => Effectful(lu.E),
        IlField or IlIndex or IlLen or IlCall or IlDynCall or IlInvoke
            or IlNewObj or IlTable or IlNewArray or IlIife or IlClosure or IlWith => true,
        _ => true,
    };

    private static void RequireArity(string callee, int actual, int expected)
    {
        if (actual != expected)
            throw new Tcs2cException($"{callee}: expected {expected} arguments, got {actual}");
    }

    private static Tcs2cException Unsupported(IlNode node) =>
        new($"unsupported IL node: {node.GetType().Name}");

    private Variable Resolve(string name) =>
        TryResolve(name)
        ?? throw new Tcs2cException($"unbound IL variable: {name}");

    private Variable? TryResolve(string name)
    {
        foreach (var scope in _scopes)
            if (scope.TryGetValue(name, out var variable)) return variable;
        return null;
    }

    // Lua の local 再宣言 (shadow) と同じく後勝ちで束縛を差し替える。
    // C 側は都度別名 (v_..._serial) を発行するため衝突しない
    private void AddVariable(string name, Variable variable) =>
        _scopes.Peek()[name] = variable;

    private void PushScope() => _scopes.Push(new Dictionary<string, Variable>());
    private void PopScope() => _scopes.Pop();
    private string Temp(string role) => $"__tcs_{role}_{_serial++}";
}

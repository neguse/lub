using System.Globalization;
using System.Text;
using TinyCs;

namespace TinyCs.Luoc;

internal sealed partial class CEmitter
{
    private CType TypeOfIsType(IlIsType typeTest)
    {
        if (!_classes.ContainsKey(typeTest.TypeRef))
            throw new LuocException($"IlIsType target is not a class: {typeTest.TypeRef}");
        var operand = TypeOf(typeTest.E);
        if (operand.Kind is not (CTypeKind.Ref or CTypeKind.Null))
            throw new LuocException($"IlIsType operand is not a class reference: {operand}");
        return CType.Bool;
    }

    private string RenderIsType(IlIsType typeTest)
    {
        _ = TypeOfIsType(typeTest);
        var value = RenderExpr(typeTest.E);
        if (!Effectful(typeTest.E))
            return $"({value} != NULL && tcs_type_in_range(" +
                $"((TcsObjectHeader *){value})->type_id, " +
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
        return IsFloatText(text) ? CType.F32 : CType.I32;
    }

    private static string RenderLiteral(IlLit literal)
    {
        var text = literal.LuaText;
        if (text is "true" or "false") return text;
        if (text == "nil") return "NULL";
        if (text.StartsWith('"')) return RenderStringLiteral(text);
        if (IsFloatText(text))
        {
            if (!float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
                throw new LuocException($"invalid f32 literal: {text}");
            return Constants.F32(value);
        }
        int integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var bits))
                throw new LuocException($"invalid i32 literal: {text}");
            integer = unchecked((int)bits);
        }
        else if (!int.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out integer))
            throw new LuocException($"invalid i32 literal: {text}");
        return Constants.I32(integer);
    }

    private static string RenderStringLiteral(string luaText)
    {
        var bytes = DecodeLuaString(luaText);
        var escaped = string.Concat(bytes.Select(b => $"\\x{b:x2}"));
        return $"tcs_string_new((const unsigned char *)\"{escaped}\", " +
            $"(size_t){bytes.Length})";
    }

    private static byte[] DecodeLuaString(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
            throw new LuocException($"invalid Lua string literal: {text}");
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
                throw new LuocException($"invalid Lua string escape: {text}");
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
                    throw new LuocException($"unsupported Lua string escape: \\{text[i]}");
            }
        }
        try
        {
            return new UTF8Encoding(false, true).GetBytes(decoded.ToString());
        }
        catch (EncoderFallbackException)
        {
            throw new LuocException("string literal contains an isolated UTF-16 surrogate");
        }
    }

    private static bool IsFloatText(string text) =>
        text.Contains('.') || text.Contains('e', StringComparison.OrdinalIgnoreCase);

    private static CType NumericJoin(CType left, CType right, string where)
    {
        if (left.Kind is not (CTypeKind.I32 or CTypeKind.F32)
            || right.Kind is not (CTypeKind.I32 or CTypeKind.F32))
            throw new LuocException($"{where} operands are not numeric: {left}, {right}");
        return left.Kind == CTypeKind.F32 || right.Kind == CTypeKind.F32
            ? CType.F32 : CType.I32;
    }

    private static CType CommonType(CType left, CType right, string where)
    {
        if (left == right) return left;
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
        throw new LuocException($"incompatible {where}: {left}, {right}");
    }

    private static void RequireComparable(CType left, CType right, string where)
    {
        if (left.Kind is CTypeKind.I32 or CTypeKind.F32
            && right.Kind is CTypeKind.I32 or CTypeKind.F32) return;
        if (left == right && left.Kind is CTypeKind.Bool or CTypeKind.String
            or CTypeKind.Ref or CTypeKind.Array or CTypeKind.List) return;
        if (left.Kind == CTypeKind.Null && right.IsNullable
            || right.Kind == CTypeKind.Null && left.IsNullable) return;
        throw new LuocException($"incompatible {where} operands: {left}, {right}");
    }

    private static void RequireType(CType expected, CType actual, string where)
    {
        if (expected != actual)
            throw new LuocException($"{where}: expected {expected}, got {actual}");
    }

    private void RequireAssignable(CType target, CType source, string where)
    {
        if (target.CanAssignFrom(source)) return;
        // 継承 upcast: Derived → Base (il-spec §9)
        if (target.Kind == CTypeKind.Ref && source.Kind == CTypeKind.Ref
            && IsAncestorOrSame(target.Name!, source.Name!))
            return;
        throw new LuocException($"{where}: cannot assign {source} to {target}");
    }

    // upcast が必要なら C の明示 cast を挟んで render する。
    // closure は型付き文脈でのみ生成できる (IlClosure は引数型を持たない)
    private string RenderCoerced(IlExpr expr, CType target)
    {
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
        IlUn unary => Effectful(unary.E),
        IlBin binary => Effectful(binary.L) || Effectful(binary.R),
        IlTernary ternary => Effectful(ternary.Cond)
            || Effectful(ternary.T) || Effectful(ternary.F),
        IlIsType typeTest => Effectful(typeTest.E),
        IlField or IlIndex or IlLen or IlCall or IlDynCall or IlInvoke
            or IlNewObj or IlTable or IlNewArray or IlIife or IlClosure or IlWith => true,
        _ => true,
    };

    private static void RequireArity(string callee, int actual, int expected)
    {
        if (actual != expected)
            throw new LuocException($"{callee}: expected {expected} arguments, got {actual}");
    }

    private static LuocException Unsupported(IlNode node) =>
        new($"unsupported IL node: {node.GetType().Name}");

    private Variable Resolve(string name) =>
        TryResolve(name)
        ?? throw new LuocException($"unbound IL variable: {name}");

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

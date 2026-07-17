using System.Globalization;
using TinyCs;

namespace TinyCs.Luoc;

internal sealed partial class CEmitter
{
    private string RenderExpr(IlExpr expr) => expr switch
    {
        IlLit literal => RenderLiteral(literal),
        IlVar variable => Resolve(variable.Name).CName,
        IlField field => RenderField(field),
        IlIndex index => RenderIndex(index),
        IlLen length => RenderLength(length),
        IlBin binary => RenderBinary(binary),
        IlUn unary => RenderUnary(unary),
        IlParen paren => $"({RenderExpr(paren.E)})",
        IlCall call => RenderCall(call),
        IlNewObj creation => RenderNew(creation),
        _ => throw Unsupported(expr),
    };

    private CType TypeOf(IlExpr expr) => expr switch
    {
        IlLit literal => TypeOfLiteral(literal),
        IlVar variable => Resolve(variable.Name).Type,
        IlField field => TypeOfField(field),
        IlIndex index => RequireArray(index).Element!,
        IlLen length => TypeOf(length.E).Kind == CTypeKind.Array
            ? CType.I32 : throw new LuocException("length receiver is not an array"),
        IlBin binary => TypeOfBinary(binary),
        IlUn { Op: IlUnOp.Not } => CType.Bool,
        IlUn unary => TypeOf(unary.E),
        IlParen paren => TypeOf(paren.E),
        IlCall call => TypeOfCall(call),
        IlNewObj creation => CType.Ref(creation.TypeName),
        _ => throw Unsupported(expr),
    };

    private CType TypeOfPlace(IlExpr expr) => expr switch
    {
        IlVar or IlField or IlIndex => TypeOf(expr),
        _ => throw new LuocException($"expression is not a place: {expr.GetType().Name}"),
    };

    private string RenderField(IlField field)
    {
        if (TryStaticField(field, out var staticName, out _)) return staticName;
        var receiver = TypeOf(field.Recv);
        if (receiver.Kind != CTypeKind.Ref)
            throw new LuocException("field receiver is not a class reference");
        _ = _facts.Field(receiver.Name!, field.Name);
        return $"(({receiver.CName})tcs_nonnull({RenderExpr(field.Recv)}))->" +
            Names.Field(field.Name);
    }

    private CType TypeOfField(IlField field)
    {
        if (TryStaticField(field, out _, out var staticType)) return staticType;
        var receiver = TypeOf(field.Recv);
        if (receiver.Kind != CTypeKind.Ref)
            throw new LuocException("field receiver is not a class reference");
        return _facts.Field(receiver.Name!, field.Name).Type;
    }

    private bool TryStaticField(IlField field, out string cName, out CType type)
    {
        if (field.Recv is IlVar receiver && _classes.TryGetValue(receiver.Name, out var cls))
        {
            var metadata = cls.Fields.FirstOrDefault(f => f.Name == field.Name && f.IsStatic);
            if (metadata is not null)
            {
                cName = Names.StaticField(cls.Name, field.Name);
                type = _facts.Field(cls.Name, field.Name).Type;
                return true;
            }
        }
        cName = "";
        type = CType.Void;
        return false;
    }

    private string RenderIndex(IlIndex index)
    {
        var array = RequireArray(index);
        if (Effectful(index.Recv) && Effectful(index.Idx))
            throw new LuocException("array receiver and index cannot both have effects yet");
        return $"(*({array.ElementCName} *)tcs_array_at(" +
            $"{RenderExpr(index.Recv)}, {RenderExpr(index.Idx)}))";
    }

    private CType RequireArray(IlIndex index)
    {
        if (!index.PlusOne)
            throw new LuocException("only 0-based array indexing is supported");
        RequireType(CType.I32, TypeOf(index.Idx), "array index");
        var array = TypeOf(index.Recv);
        return array.Kind == CTypeKind.Array
            ? array : throw new LuocException("index receiver is not an array");
    }

    private string RenderLength(IlLen length)
    {
        var type = TypeOf(length.E);
        if (type.Kind != CTypeKind.Array)
            throw new LuocException("only array Length is supported");
        return $"tcs_array_length({RenderExpr(length.E)})";
    }

    private string RenderBinary(IlBin binary)
    {
        var leftType = TypeOf(binary.L);
        var rightType = TypeOf(binary.R);
        _ = TypeOfBinary(binary);
        var left = RenderExpr(binary.L);
        var right = RenderExpr(binary.R);
        if (binary.Op is not (IlBinOp.And or IlBinOp.Or)
            && Effectful(binary.L) && Effectful(binary.R))
        {
            var leftTemp = Temp("lhs");
            var rightTemp = Temp("rhs");
            return $"({{ {leftType.CName} {leftTemp} = {left}; " +
                $"{rightType.CName} {rightTemp} = {right}; " +
                $"{RenderBinaryOperation(binary.Op, leftTemp, rightTemp)}; }})";
        }
        return RenderBinaryOperation(binary.Op, left, right);
    }

    private static string RenderBinaryOperation(IlBinOp op, string left,
        string right) => op switch
        {
            IlBinOp.AddNum => $"({left} + {right})",
            IlBinOp.Sub => $"({left} - {right})",
            IlBinOp.Mul => $"({left} * {right})",
            IlBinOp.DivNum => $"({left} / {right})",
            IlBinOp.Eq => $"({left} == {right})",
            IlBinOp.Ne => $"({left} != {right})",
            IlBinOp.Lt => $"({left} < {right})",
            IlBinOp.Le => $"({left} <= {right})",
            IlBinOp.Gt => $"({left} > {right})",
            IlBinOp.Ge => $"({left} >= {right})",
            IlBinOp.And => $"({left} && {right})",
            IlBinOp.Or => $"({left} || {right})",
            IlBinOp.BitAnd => $"({left} & {right})",
            IlBinOp.BitOr => $"({left} | {right})",
            IlBinOp.BitXor => $"({left} ^ {right})",
            IlBinOp.Shl => $"tcs_shl({left}, {right})",
            IlBinOp.Shr => $"tcs_shr({left}, {right})",
            _ => throw new LuocException($"unsupported binary operator: {op}"),
        };

    private CType TypeOfBinary(IlBin binary)
    {
        var left = TypeOf(binary.L);
        var right = TypeOf(binary.R);
        switch (binary.Op)
        {
            case IlBinOp.AddNum or IlBinOp.Sub or IlBinOp.Mul:
                return NumericJoin(left, right, binary.Op.ToString());
            case IlBinOp.DivNum:
                RequireType(CType.F32, NumericJoin(left, right, "division"), "division");
                return CType.F32;
            case IlBinOp.Eq or IlBinOp.Ne:
                RequireComparable(left, right, "equality");
                return CType.Bool;
            case IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge:
                _ = NumericJoin(left, right, "comparison");
                return CType.Bool;
            case IlBinOp.And or IlBinOp.Or:
                RequireType(CType.Bool, left, "logical operand");
                RequireType(CType.Bool, right, "logical operand");
                return CType.Bool;
            case IlBinOp.BitAnd or IlBinOp.BitOr or IlBinOp.BitXor
                or IlBinOp.Shl or IlBinOp.Shr:
                RequireType(CType.I32, left, "bitwise operand");
                RequireType(CType.I32, right, "bitwise operand");
                return CType.I32;
            default:
                throw new LuocException($"unsupported binary operator: {binary.Op}");
        }
    }

    private string RenderUnary(IlUn unary)
    {
        var value = RenderExpr(unary.E);
        var type = TypeOf(unary.E);
        return unary.Op switch
        {
            IlUnOp.Neg when type.Kind is CTypeKind.I32 or CTypeKind.F32 => $"(-{value})",
            IlUnOp.Not when type == CType.Bool => $"(!{value})",
            IlUnOp.BitNot when type == CType.I32 => $"(~{value})",
            _ => throw new LuocException($"invalid unary operator {unary.Op} for {type}"),
        };
    }

    private string RenderCall(IlCall call)
    {
        var type = TypeOfCall(call);
        if (call.Args.Count(Effectful) > 1)
            throw new LuocException($"call arguments both have effects: {call.Callee}");
        var args = call.Args.Select(RenderExpr).ToArray();
        return call.Callee switch
        {
            "__tcs_idiv" => $"tcs_idiv({args[0]}, {args[1]})",
            "__tcs_irem" => $"tcs_irem({args[0]}, {args[1]})",
            "print" when type == CType.Void => $"tcs_digest_float({args[0]})",
            _ => RenderUserCall(call, args),
        };
    }

    private CType TypeOfCall(IlCall call)
    {
        if (call.Callee is "__tcs_idiv" or "__tcs_irem")
        {
            if (call.Args.Length != 2) throw BadArity(call, 2);
            RequireType(CType.I32, TypeOf(call.Args[0]), call.Callee);
            RequireType(CType.I32, TypeOf(call.Args[1]), call.Callee);
            return CType.I32;
        }
        if (call.Callee == "print")
        {
            if (call.Args.Length != 1) throw BadArity(call, 1);
            RequireType(CType.F32, TypeOf(call.Args[0]), "print/digest");
            return CType.Void;
        }
        var (cls, method) = ParseUserCallee(call.Callee);
        var fact = _facts.Method(cls, method);
        if (!fact.IsStatic) throw new LuocException($"call target is not static: {call.Callee}");
        if (call.Args.Length != fact.Parameters.Count) throw BadArity(call, fact.Parameters.Count);
        for (var i = 0; i < call.Args.Length; i++)
            RequireAssignable(fact.Parameters[i].Type, TypeOf(call.Args[i]),
                $"argument {i} of {call.Callee}");
        return fact.ReturnType;
    }

    private string RenderUserCall(IlCall call, IReadOnlyList<string> args)
    {
        var (cls, method) = ParseUserCallee(call.Callee);
        return $"{Names.Method(cls, method)}({string.Join(", ", args)})";
    }

    private (string Class, string Method) ParseUserCallee(string callee)
    {
        var dot = callee.IndexOf('.');
        if (dot <= 0 || dot != callee.LastIndexOf('.') || dot == callee.Length - 1)
            throw new LuocException($"unsupported intrinsic or callee: {callee}");
        var cls = callee[..dot];
        var method = callee[(dot + 1)..];
        if (!_classes.ContainsKey(cls))
            throw new LuocException($"unknown call target class: {callee}");
        return (cls, method);
    }

    private string RenderNew(IlNewObj creation)
    {
        if (creation.Args.Length != 0)
            throw new LuocException("constructors with arguments are not supported yet");
        if (!_classes.ContainsKey(creation.TypeName))
            throw new LuocException($"unknown class: {creation.TypeName}");
        return $"{Names.New(creation.TypeName)}()";
    }

    private static CType TypeOfLiteral(IlLit literal)
    {
        var text = literal.LuaText;
        if (text is "true" or "false") return CType.Bool;
        if (text == "nil")
            throw new LuocException("untyped nil literal is not supported in this slice");
        if (text.StartsWith('"'))
            throw new LuocException("string literals are not supported in this slice");
        return IsFloatText(text) ? CType.F32 : CType.I32;
    }

    private static string RenderLiteral(IlLit literal)
    {
        var text = literal.LuaText;
        if (text is "true" or "false") return text;
        if (text == "nil") return "NULL";
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

    private static void RequireComparable(CType left, CType right, string where)
    {
        if (left.Kind is CTypeKind.I32 or CTypeKind.F32
            && right.Kind is CTypeKind.I32 or CTypeKind.F32) return;
        if (left == right && left.Kind is CTypeKind.Bool or CTypeKind.Ref) return;
        throw new LuocException($"incompatible {where} operands: {left}, {right}");
    }

    private static void RequireType(CType expected, CType actual, string where)
    {
        if (expected != actual)
            throw new LuocException($"{where}: expected {expected}, got {actual}");
    }

    private static void RequireAssignable(CType target, CType source, string where)
    {
        if (!target.CanAssignFrom(source))
            throw new LuocException($"{where}: cannot assign {source} to {target}");
    }

    private static bool Effectful(IlExpr expr) => expr switch
    {
        IlLit or IlVar => false,
        IlParen paren => Effectful(paren.E),
        IlUn unary => Effectful(unary.E),
        IlBin binary => Effectful(binary.L) || Effectful(binary.R),
        IlField or IlIndex or IlLen or IlCall or IlDynCall or IlInvoke
            or IlNewObj or IlIife or IlClosure or IlWith => true,
        IlTable table => table.Entries.Any(e =>
            (e.Key is not null && Effectful(e.Key)) || Effectful(e.Value)),
        _ => true,
    };

    private static LuocException BadArity(IlCall call, int expected) =>
        new($"{call.Callee}: expected {expected} arguments, got {call.Args.Length}");

    private static LuocException Unsupported(IlNode node) =>
        new($"unsupported IL node: {node.GetType().Name}");

    private Variable Resolve(string name)
    {
        foreach (var scope in _scopes)
            if (scope.TryGetValue(name, out var variable)) return variable;
        throw new LuocException($"unbound IL variable: {name}");
    }

    private void AddVariable(string name, Variable variable)
    {
        if (!_scopes.Peek().TryAdd(name, variable))
            throw new LuocException($"duplicate local in one IL scope: {name}");
    }

    private void PushScope() => _scopes.Push(new Dictionary<string, Variable>());
    private void PopScope() => _scopes.Pop();
    private string Temp(string role) => $"__tcs_{role}_{_serial++}";

    private static string DefaultValue(CType type) => type.Kind switch
    {
        CTypeKind.I32 => Constants.I32(0),
        CTypeKind.F32 => Constants.F32(0.0f),
        CTypeKind.Bool => "false",
        CTypeKind.Ref or CTypeKind.Array => "NULL",
        _ => throw new LuocException($"type has no default value: {type}"),
    };

    private static string RenderConstant(CType type, object? value) => type.Kind switch
    {
        CTypeKind.I32 when value is int integer => Constants.I32(integer),
        CTypeKind.F32 when value is float single => Constants.F32(single),
        CTypeKind.Bool when value is bool boolean => boolean ? "true" : "false",
        CTypeKind.Ref when value is null => "NULL",
        _ => throw new LuocException($"unsupported constant initializer {value ?? "null"} " +
            $"for {type}"),
    };

}

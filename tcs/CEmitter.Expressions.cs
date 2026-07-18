using System.Text;
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
        IlTernary ternary => RenderTernary(ternary),
        IlCall call => RenderCall(call),
        IlDynCall call => RenderDynCall(call),
        IlInvoke invoke => RenderInvoke(invoke),
        IlNewObj creation => RenderNew(creation),
        IlTable table => RenderTable(table),
        IlNewArray array => RenderNewArray(array),
        IlIsType typeTest => RenderIsType(typeTest),
        _ => throw Unsupported(expr),
    };

    private CType TypeOf(IlExpr expr) => expr switch
    {
        IlLit literal => TypeOfLiteral(literal),
        IlVar variable => Resolve(variable.Name).Type,
        IlField field => TypeOfField(field),
        IlIndex index => RequireSequence(index).Element!,
        IlLen length => TypeOfLength(length),
        IlBin binary => TypeOfBinary(binary),
        IlUn { Op: IlUnOp.Not } => CType.Bool,
        IlUn unary => TypeOf(unary.E),
        IlParen paren => TypeOf(paren.E),
        IlTernary ternary => TypeOfTernary(ternary),
        IlCall call => TypeOfCall(call),
        IlDynCall call => TypeOfDynCall(call),
        IlInvoke invoke => TypeOfInvoke(invoke),
        IlNewObj creation => CType.Ref(creation.TypeName),
        IlTable table => TypeOfTable(table),
        IlNewArray array => TypeOfNewArray(array),
        IlIsType typeTest => TypeOfIsType(typeTest),
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
        var sequenceType = RequireSequence(index);
        var sequence = Temp("index_sequence");
        var position = Temp("index");
        var at = sequenceType.Kind == CTypeKind.Array
            ? "tcs_array_at" : "tcs_list_at";
        return $"({{ {sequenceType.CName} {sequence} = {RenderExpr(index.Recv)}; " +
            $"int32_t {position} = {RenderExpr(index.Idx)}; " +
            $"*({sequenceType.ElementCName} *){at}({sequence}, {position}); }})";
    }

    private CType RequireSequence(IlIndex index)
    {
        if (!index.PlusOne)
            throw new LuocException("only 0-based array/List indexing is supported");
        RequireType(CType.I32, TypeOf(index.Idx), "sequence index");
        var sequence = TypeOf(index.Recv);
        if (sequence.Kind is not (CTypeKind.Array or CTypeKind.List))
            throw new LuocException("index receiver is not an array/List");
        if (sequence.Element is null)
            throw new LuocException("cannot index a List with unknown element type");
        return sequence;
    }

    private CType TypeOfLength(IlLen length)
    {
        var type = TypeOf(length.E);
        return type.Kind is CTypeKind.Array or CTypeKind.List or CTypeKind.String
            ? CType.I32
            : throw new LuocException("length receiver is not an array/List/string");
    }

    private string RenderLength(IlLen length)
    {
        var type = TypeOf(length.E);
        var helper = type.Kind switch
        {
            CTypeKind.Array => "tcs_array_length",
            CTypeKind.List => "tcs_list_length",
            CTypeKind.String => "tcs_string_length",
            _ => throw new LuocException("length receiver is not an array/List/string"),
        };
        return $"{helper}({RenderExpr(length.E)})";
    }

    private string RenderBinary(IlBin binary)
    {
        var leftType = TypeOf(binary.L);
        var rightType = TypeOf(binary.R);
        var resultType = TypeOfBinary(binary);
        if (binary.Op == IlBinOp.Concat)
        {
            var leftString = leftType == CType.String
                ? RenderExpr(binary.L) : RenderToString(binary.L);
            var rightString = rightType == CType.String
                ? RenderExpr(binary.R) : RenderToString(binary.R);
            var concatLeft = Temp("concat_lhs");
            var concatRight = Temp("concat_rhs");
            return $"({{ TcsString *{concatLeft} = {leftString}; " +
                $"TcsString *{concatRight} = {rightString}; " +
                $"tcs_string_concat({concatLeft}, {concatRight}); }})";
        }
        var left = RenderExpr(binary.L);
        var right = RenderExpr(binary.R);

        if (binary.Op is IlBinOp.And
            || binary.Op == IlBinOp.Or && leftType == CType.Bool)
            return RenderBinaryOperation(binary.Op, left, right, leftType, rightType);
        if (binary.Op == IlBinOp.Or && resultType.IsNullable)
        {
            if (leftType.Kind == CTypeKind.Null) return right;
            if (!Effectful(binary.L)) return $"({left} != NULL ? {left} : {right})";
            var temp = Temp("coalesce");
            return $"({{ {leftType.CName} {temp} = {left}; " +
                $"{temp} != NULL ? {temp} : {right}; }})";
        }
        if (leftType.Kind == CTypeKind.Null || rightType.Kind == CTypeKind.Null
            || !Effectful(binary.L) && !Effectful(binary.R))
            return RenderBinaryOperation(binary.Op, left, right, leftType, rightType);

        var leftTemp = Temp("lhs");
        var rightTemp = Temp("rhs");
        return $"({{ {leftType.CName} {leftTemp} = {left}; " +
            $"{rightType.CName} {rightTemp} = {right}; " +
            $"{RenderBinaryOperation(binary.Op, leftTemp, rightTemp, leftType, rightType)}; }})";
    }

    private static string RenderBinaryOperation(IlBinOp op, string left, string right,
        CType leftType, CType rightType) => op switch
    {
        IlBinOp.AddNum => $"({left} + {right})",
        IlBinOp.Concat => $"tcs_string_concat({left}, {right})",
        IlBinOp.Sub => $"({left} - {right})",
        IlBinOp.Mul => $"({left} * {right})",
        IlBinOp.DivNum => $"({left} / {right})",
        IlBinOp.Eq when leftType == CType.String && rightType == CType.String =>
            $"tcs_string_equal({left}, {right})",
        IlBinOp.Ne when leftType == CType.String && rightType == CType.String =>
            $"(!tcs_string_equal({left}, {right}))",
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
            case IlBinOp.Concat:
                if (left.Kind is not (CTypeKind.I32 or CTypeKind.F32
                    or CTypeKind.Bool or CTypeKind.String)
                    || right.Kind is not (CTypeKind.I32 or CTypeKind.F32
                        or CTypeKind.Bool or CTypeKind.String))
                    throw new LuocException($"concat operands are not stringifiable: " +
                        $"{left}, {right}");
                return CType.String;
            case IlBinOp.DivNum:
                RequireType(CType.F32, NumericJoin(left, right, "division"), "division");
                return CType.F32;
            case IlBinOp.Eq or IlBinOp.Ne:
                RequireComparable(left, right, "equality");
                return CType.Bool;
            case IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge:
                _ = NumericJoin(left, right, "comparison");
                return CType.Bool;
            case IlBinOp.And:
                RequireType(CType.Bool, left, "logical operand");
                RequireType(CType.Bool, right, "logical operand");
                return CType.Bool;
            case IlBinOp.Or when left == CType.Bool:
                RequireType(CType.Bool, right, "logical operand");
                return CType.Bool;
            case IlBinOp.Or:
                return CommonType(left, right, "coalesce");
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

    private CType TypeOfTernary(IlTernary ternary)
    {
        RequireType(CType.Bool, TypeOf(ternary.Cond), "ternary condition");
        return CommonType(TypeOf(ternary.T), TypeOf(ternary.F), "ternary arms");
    }

    private string RenderTernary(IlTernary ternary)
    {
        _ = TypeOfTernary(ternary);
        return $"({RenderExpr(ternary.Cond)} ? {RenderExpr(ternary.T)} : " +
            $"{RenderExpr(ternary.F)})";
    }

    private string RenderCall(IlCall call)
    {
        var type = TypeOfCall(call);
        return call.Callee switch
        {
            "__tcs_idiv" => RenderOrderedCall("tcs_idiv", type,
                [(CType.I32, RenderExpr(call.Args[0])),
                 (CType.I32, RenderExpr(call.Args[1]))]),
            "__tcs_irem" => RenderOrderedCall("tcs_irem", type,
                [(CType.I32, RenderExpr(call.Args[0])),
                 (CType.I32, RenderExpr(call.Args[1]))]),
            "print" => RenderPrint(call),
            "tostring" => RenderToString(call.Args[0]),
            "table.insert" => RenderListAdd(call),
            _ => RenderUserCall(call),
        };
    }

    private CType TypeOfCall(IlCall call)
    {
        if (call.Callee is "__tcs_idiv" or "__tcs_irem")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            RequireType(CType.I32, TypeOf(call.Args[0]), call.Callee);
            RequireType(CType.I32, TypeOf(call.Args[1]), call.Callee);
            return CType.I32;
        }
        if (call.Callee == "print")
        {
            if (call.Args.Length == 0) return CType.Void;
            RequireArity(call.Callee, call.Args.Length, 1);
            var argument = TypeOf(call.Args[0]);
            if (_digestF32)
                RequireType(CType.F32, argument, "print/digest");
            else if (argument.Kind is not (CTypeKind.I32 or CTypeKind.F32
                or CTypeKind.Bool or CTypeKind.String))
                throw new LuocException($"print does not support {argument}");
            return CType.Void;
        }
        if (call.Callee == "tostring")
        {
            RequireArity(call.Callee, call.Args.Length, 1);
            var argument = TypeOf(call.Args[0]);
            if (argument.Kind is not (CTypeKind.I32 or CTypeKind.F32
                or CTypeKind.Bool or CTypeKind.String))
                throw new LuocException($"tostring does not support {argument}");
            return CType.String;
        }
        if (call.Callee == "table.insert")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            _ = RequireListForAdd(call.Args[0], TypeOf(call.Args[1]));
            return CType.Void;
        }
        var (cls, method) = ParseUserCallee(call.Callee);
        return ValidateMethodCall(_facts.Method(cls, method), null, call.Args);
    }

    private string RenderPrint(IlCall call)
    {
        if (call.Args.Length == 0) return "tcs_print_newline()";
        var argument = TypeOf(call.Args[0]);
        var helper = _digestF32 ? "tcs_digest_float" : argument.Kind switch
        {
            CTypeKind.I32 => "tcs_print_i32",
            CTypeKind.F32 => "tcs_print_f32",
            CTypeKind.Bool => "tcs_print_bool",
            CTypeKind.String => "tcs_print_string",
            _ => throw new LuocException($"print does not support {argument}"),
        };
        return RenderOrderedCall(helper, CType.Void,
            [(argument, RenderExpr(call.Args[0]))]);
    }

    private string RenderToString(IlExpr argument)
    {
        var type = TypeOf(argument);
        var helper = type.Kind switch
        {
            CTypeKind.I32 => "tcs_string_i32",
            CTypeKind.F32 => "tcs_string_f32",
            CTypeKind.Bool => "tcs_string_bool",
            CTypeKind.String => "tcs_string_tostring",
            _ => throw new LuocException($"tostring does not support {type}"),
        };
        return RenderOrderedCall(helper, CType.String, [(type, RenderExpr(argument))]);
    }

    private CType RequireListForAdd(IlExpr receiver, CType valueType)
    {
        if (valueType.Kind is not (CTypeKind.I32 or CTypeKind.F32))
            throw new LuocException($"List.Add supports only int/float, got {valueType}");
        var listType = TypeOf(receiver);
        if (listType.Kind != CTypeKind.List)
            throw new LuocException("table.insert receiver is not a List");
        if (listType.Element is null)
        {
            listType = CType.List(valueType);
            if (receiver is IlVar variable) Resolve(variable.Name).Type = listType;
        }
        RequireAssignable(listType.Element!, valueType, "List.Add argument");
        return listType;
    }

    private string RenderListAdd(IlCall call)
    {
        var listType = RequireListForAdd(call.Args[0], TypeOf(call.Args[1]));
        var list = Temp("list");
        var value = Temp("list_value");
        return $"({{ TcsList *{list} = {RenderExpr(call.Args[0])}; " +
            $"{listType.ElementCName} {value} = {RenderExpr(call.Args[1])}; " +
            $"tcs_list_add({list}, &{value}, sizeof({value})); }})";
    }

    private string RenderUserCall(IlCall call)
    {
        var (cls, method) = ParseUserCallee(call.Callee);
        var fact = _facts.Method(cls, method);
        _ = ValidateMethodCall(fact, null, call.Args);
        return RenderMethodCall(fact, null, call.Args);
    }

    private CType TypeOfDynCall(IlDynCall call)
    {
        var fact = ParseDynCallee(call.Callee);
        return ValidateMethodCall(fact, null, call.Args);
    }

    private string RenderDynCall(IlDynCall call)
    {
        var fact = ParseDynCallee(call.Callee);
        _ = ValidateMethodCall(fact, null, call.Args);
        return RenderMethodCall(fact, null, call.Args);
    }

    private MethodFact ParseDynCallee(IlExpr callee)
    {
        if (callee is not IlField { Recv: IlVar receiver } field
            || !_classes.ContainsKey(receiver.Name))
            throw new LuocException("only type-qualified static IlDynCall is supported");
        var fact = _facts.Method(receiver.Name, field.Name);
        if (!fact.IsStatic)
            throw new LuocException($"dynamic call target is not static: " +
                $"{receiver.Name}.{field.Name}");
        return fact;
    }

    private CType TypeOfInvoke(IlInvoke invoke)
    {
        var receiver = TypeOf(invoke.Recv);
        if (receiver.Kind != CTypeKind.Ref)
            throw new LuocException("IlInvoke receiver is not a class reference");
        var fact = _facts.Method(receiver.Name!, invoke.Method);
        return ValidateMethodCall(fact, receiver, invoke.Args);
    }

    private string RenderInvoke(IlInvoke invoke)
    {
        _ = TypeOfInvoke(invoke);
        var receiver = TypeOf(invoke.Recv);
        var fact = _facts.Method(receiver.Name!, invoke.Method);
        return RenderMethodCall(fact, invoke.Recv, invoke.Args);
    }

    private CType ValidateMethodCall(MethodFact fact, CType? receiver,
        IReadOnlyList<IlExpr> args)
    {
        if (receiver is null && !fact.IsStatic)
            throw new LuocException($"call target is not static: {fact.ClassName}.{fact.Name}");
        if (receiver is not null && fact.IsStatic)
            throw new LuocException($"IlInvoke target is static: {fact.ClassName}.{fact.Name}");
        RequireArity($"{fact.ClassName}.{fact.Name}", args.Count, fact.Parameters.Count);
        for (var i = 0; i < args.Count; i++)
            RequireAssignable(fact.Parameters[i].Type, TypeOf(args[i]),
                $"argument {i} of {fact.ClassName}.{fact.Name}");
        return fact.ReturnType;
    }

    private string RenderMethodCall(MethodFact fact, IlExpr? receiver,
        IReadOnlyList<IlExpr> args)
    {
        var values = new List<(CType Type, string Value)>();
        if (receiver is not null)
        {
            var receiverType = CType.Ref(fact.ClassName);
            values.Add((receiverType,
                $"({receiverType.CName})tcs_nonnull({RenderExpr(receiver)})"));
        }
        values.AddRange(args.Select((arg, i) =>
            (fact.Parameters[i].Type, RenderExpr(arg))));
        return RenderOrderedCall(Names.Method(fact.ClassName, fact.Name),
            fact.ReturnType, values);
    }

    private string RenderOrderedCall(string function, CType returnType,
        IReadOnlyList<(CType Type, string Value)> values)
    {
        if (values.Count == 0) return $"{function}()";
        var declarations = new StringBuilder();
        var arguments = new List<string>();
        foreach (var (type, value) in values)
        {
            var temp = Temp("arg");
            declarations.Append(type.CName).Append(' ').Append(temp)
                .Append(" = ").Append(value).Append("; ");
            arguments.Add(temp);
        }
        _ = returnType;
        return $"({{ {declarations}{function}({string.Join(", ", arguments)}); }})";
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
        if (!_classes.TryGetValue(creation.TypeName, out var cls))
            throw new LuocException($"unknown class: {creation.TypeName}");
        var fields = cls.Fields.Where(f => !f.IsStatic)
            .Select(f => _facts.Field(cls.Name, f.Name)).ToArray();
        if (creation.Args.Length != 0 && creation.Args.Length != fields.Length)
            throw new LuocException($"simple positional constructor {cls.Name}: expected " +
                $"0 or {fields.Length} arguments, got {creation.Args.Length}");
        if (creation.Args.Length == 0) return $"{Names.New(cls.Name)}()";

        var statements = new StringBuilder();
        var arguments = new List<string>();
        for (var i = 0; i < creation.Args.Length; i++)
        {
            RequireAssignable(fields[i].Type, TypeOf(creation.Args[i]),
                $"positional constructor argument {i} of {cls.Name}");
            var temp = Temp("ctor_arg");
            statements.Append(fields[i].Type.CName).Append(' ').Append(temp)
                .Append(" = ").Append(RenderExpr(creation.Args[i])).Append("; ");
            arguments.Add(temp);
        }
        var objectName = Temp("object");
        statements.Append(Names.Class(cls.Name)).Append(" *").Append(objectName)
            .Append(" = ").Append(Names.New(cls.Name)).Append("(); ");
        for (var i = 0; i < fields.Length; i++)
            statements.Append(objectName).Append("->").Append(Names.Field(fields[i].Name))
                .Append(" = ").Append(arguments[i]).Append("; ");
        return $"({{ {statements}{objectName}; }})";
    }

    private CType TypeOfTable(IlTable table)
    {
        if (table.Entries.Any(e => e.Key is not null || e.NameKey is not null))
            throw new LuocException("only array-style IlTable entries are supported");
        CType? element = table.ElementType is null
            ? null : _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            var itemType = TypeOf(entry.Value);
            element = element is null ? itemType : CommonType(element, itemType, "IlTable items");
        }
        if (element is not null && element.Kind is not (CTypeKind.I32 or CTypeKind.F32))
            throw new LuocException($"List supports only int/float, got {element}");
        return CType.List(element);
    }

    private string RenderTable(IlTable table)
    {
        var type = TypeOfTable(table);
        var list = Temp("list");
        var elementSize = type.Element is null ? "0" : $"sizeof({type.ElementCName})";
        var statements = new StringBuilder($"TcsList *{list} = tcs_list_new({elementSize}); ");
        foreach (var entry in table.Entries)
        {
            var value = Temp("list_item");
            statements.Append(type.ElementCName).Append(' ').Append(value)
                .Append(" = ").Append(RenderExpr(entry.Value)).Append("; ")
                .Append("tcs_list_add(").Append(list).Append(", &").Append(value)
                .Append(", sizeof(").Append(value).Append(")); ");
        }
        return $"({{ {statements}{list}; }})";
    }

    private CType TypeOfNewArray(IlNewArray array)
    {
        RequireType(CType.I32, TypeOf(array.Length), "array length");
        var element = _facts.MapType(array.ElementType);
        var result = CType.Array(element);
        EnsureSupportedStorageType(result, "IlNewArray element type");
        return result;
    }

    private string RenderNewArray(IlNewArray array)
    {
        var type = TypeOfNewArray(array);
        return $"tcs_array_new({RenderExpr(array.Length)}, sizeof({type.ElementCName}))";
    }
}

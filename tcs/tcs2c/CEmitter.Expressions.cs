using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private string RenderExpr(IlExpr expr) => expr switch
    {
        IlLit literal => RenderLiteral(literal),
        IlVar variable => Resolve(variable.Name) is { Boxed: true } cell
            ? $"(*{cell.CName})" : Resolve(variable.Name).CName,
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
        IlIndex index => index.PlusOne || TypeOf(index.Recv).Kind != CTypeKind.Dict
            ? RequireSequence(index).Element!
            : TypeOf(index.Recv).Element!,
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
        _ => throw new Tcs2cException($"expression is not a place: {expr.GetType().Name}"),
    };

    private string RenderField(IlField field)
    {
        if (TryStaticField(field, out var staticName, out _)) return staticName;
        var receiver = TypeOf(field.Recv);
        if (receiver.Kind == CTypeKind.Kvp && field.Recv is IlVar kvpVar)
        {
            var node = Resolve(kvpVar.Name).CName;
            return field.Name switch
            {
                "Key" => receiver.Key!.Kind == CTypeKind.String
                    ? $"{node}->key_s" : $"{node}->key_i",
                "Value" => $"(*({receiver.Element!.CName} *){node}->value)",
                _ => throw new Tcs2cException(
                    $"unknown KeyValuePair member: {field.Name}"),
            };
        }
        if (receiver.Kind != CTypeKind.Ref)
            throw new Tcs2cException("field receiver is not a class reference");
        _ = FieldInChain(receiver.Name!, field.Name);
        return $"(({receiver.CName})tcs_nonnull({RenderExpr(field.Recv)}))->" +
            Names.Field(field.Name);
    }

    private CType TypeOfField(IlField field)
    {
        if (TryStaticField(field, out _, out var staticType)) return staticType;
        var receiver = TypeOf(field.Recv);
        if (receiver.Kind == CTypeKind.Kvp)
            return field.Name switch
            {
                "Key" => receiver.Key!,
                "Value" => receiver.Element!,
                _ => throw new Tcs2cException(
                    $"unknown KeyValuePair member: {field.Name}"),
            };
        if (receiver.Kind != CTypeKind.Ref)
            throw new Tcs2cException("field receiver is not a class reference");
        return FieldInChain(receiver.Name!, field.Name).Type;
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
        if (!index.PlusOne && TypeOf(index.Recv).Kind == CTypeKind.Dict)
        {
            var valueType = RequireDict(index.Recv, out var dictType);
            var dictTemp = Temp("dict");
            return $"({{ TcsDict *{dictTemp} = {RenderExpr(index.Recv)}; " +
                $"*({valueType.CName} *)tcs_dict_at({dictTemp}, " +
                $"{DictKeyArgs(dictType.Key!, index.Idx)}); }})";
        }
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
            throw new Tcs2cException("only 0-based array/List indexing is supported");
        RequireType(CType.I32, TypeOf(index.Idx), "sequence index");
        var sequence = TypeOf(index.Recv);
        if (sequence.Kind is not (CTypeKind.Array or CTypeKind.List))
            throw new Tcs2cException("index receiver is not an array/List");
        if (sequence.Element is null)
            throw new Tcs2cException("cannot index a List with unknown element type");
        return sequence;
    }

    private CType TypeOfLength(IlLen length)
    {
        var type = TypeOf(length.E);
        return type.Kind is CTypeKind.Array or CTypeKind.List or CTypeKind.String
            ? CType.I32
            : throw new Tcs2cException("length receiver is not an array/List/string");
    }

    private string RenderLength(IlLen length)
    {
        var type = TypeOf(length.E);
        var helper = type.Kind switch
        {
            CTypeKind.Array => "tcs_array_length",
            CTypeKind.List => "tcs_list_length",
            CTypeKind.String => "tcs_string_length",
            _ => throw new Tcs2cException("length receiver is not an array/List/string"),
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
        _ => throw new Tcs2cException($"unsupported binary operator: {op}"),
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
                    throw new Tcs2cException($"concat operands are not stringifiable: " +
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
                throw new Tcs2cException($"unsupported binary operator: {binary.Op}");
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
            _ => throw new Tcs2cException($"invalid unary operator {unary.Op} for {type}"),
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
            "Dict.ContainsKey" => RenderDictSimple(call, "tcs_dict_contains"),
            "Dict.Remove" => RenderDictSimple(call, "tcs_dict_remove"),
            "Dict.Count" =>
                $"tcs_dict_count({RenderExpr(call.Args[0])})",
            // Lua backend の f32 shortest round-trip helper。C 側の
            // to-string は元々 shortest round-trip なので同一経路で良い
            "__tcs_fstr" => RenderToString(call.Args[0]),
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
            var argument = TypeOf(UnwrapFstr(call.Args[0]));
            if (_digestF32)
                RequireType(CType.F32, argument, "print/digest");
            else if (argument.Kind is not (CTypeKind.I32 or CTypeKind.F32
                or CTypeKind.Bool or CTypeKind.String))
                throw new Tcs2cException($"print does not support {argument}");
            return CType.Void;
        }
        if (call.Callee == "Dict.ContainsKey")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            var valueType0 = RequireDict(call.Args[0], out var dictType0);
            _ = valueType0;
            RequireAssignable(dictType0.Key!, TypeOf(call.Args[1]),
                "Dict.ContainsKey key");
            return CType.Bool;
        }
        if (call.Callee == "Dict.Remove")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            _ = RequireDict(call.Args[0], out var dictTypeR);
            RequireAssignable(dictTypeR.Key!, TypeOf(call.Args[1]),
                "Dict.Remove key");
            return CType.Bool;
        }
        if (call.Callee == "Dict.Count")
        {
            RequireArity(call.Callee, call.Args.Length, 1);
            _ = RequireDict(call.Args[0], out _);
            return CType.I32;
        }
        if (call.Callee is "tostring" or "__tcs_fstr")
        {
            RequireArity(call.Callee, call.Args.Length, 1);
            var argument = TypeOf(call.Args[0]);
            if (argument.Kind is not (CTypeKind.I32 or CTypeKind.F32
                or CTypeKind.Bool or CTypeKind.String))
                throw new Tcs2cException($"tostring does not support {argument}");
            return CType.String;
        }
        if (call.Callee == "table.insert")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            _ = RequireListForAdd(call.Args[0],
                call.Args[1] is IlClosure ? null : TypeOf(call.Args[1]));
            return CType.Void;
        }
        var (cls, method) = ParseUserCallee(call.Callee);
        var fact = _facts.Method(cls, method);
        // base 呼び出し (IlCall "Base.M" with self 先頭) は非仮想の直呼び
        if (!fact.IsStatic && call.Args.Length == fact.Parameters.Count + 1)
        {
            var self = TypeOf(call.Args[0]);
            if (self.Kind != CTypeKind.Ref
                || !IsAncestorOrSame(fact.ClassName, self.Name!))
                throw new Tcs2cException(
                    $"invalid receiver for {cls}.{method}");
            return ValidateMethodCall(fact, self, call.Args.Skip(1).ToArray());
        }
        return ValidateMethodCall(fact, null, call.Args);
    }

    // digest は f32 bit を直接食うため、Lua 向け整形 (__tcs_fstr) を剥がして
    // 元の f32 を見る。通常 print は整形済み文字列のままで良いが、C 側の
    // f32 印字は元々 shortest round-trip なので剥がして直接印字する (同値)
    private static IlExpr UnwrapFstr(IlExpr expr) =>
        expr is IlCall { Callee: "__tcs_fstr", Args.Length: 1 } fstr
            ? fstr.Args[0] : expr;

    private string RenderPrint(IlCall call)
    {
        if (call.Args.Length == 0) return "tcs_print_newline()";
        call = call with { Args = [UnwrapFstr(call.Args[0])] };
        var argument = TypeOf(call.Args[0]);
        var helper = _digestF32 ? "tcs_digest_float" : argument.Kind switch
        {
            CTypeKind.I32 => "tcs_print_i32",
            CTypeKind.F32 => "tcs_print_f32",
            CTypeKind.Bool => "tcs_print_bool",
            CTypeKind.String => "tcs_print_string",
            _ => throw new Tcs2cException($"print does not support {argument}"),
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
            _ => throw new Tcs2cException($"tostring does not support {type}"),
        };
        return RenderOrderedCall(helper, CType.String, [(type, RenderExpr(argument))]);
    }

    private CType RequireListForAdd(IlExpr receiver, CType? valueType)
    {
        var listType = TypeOf(receiver);
        if (listType.Kind != CTypeKind.List)
            throw new Tcs2cException("table.insert receiver is not a List");
        if (listType.Element is null)
        {
            if (valueType is null)
                throw new Tcs2cException(
                    "cannot infer List element type for closure Add");
            listType = CType.List(valueType);
            if (receiver is IlVar variable) Resolve(variable.Name).Type = listType;
        }
        if (valueType is not null)
            RequireAssignable(listType.Element!, valueType, "List.Add argument");
        return listType;
    }

    private string RenderListAdd(IlCall call)
    {
        // closure 引数は要素型を target とした型付き render (TypeOf 不能)
        var argType = call.Args[1] is IlClosure ? null : TypeOf(call.Args[1]);
        var listType = RequireListForAdd(call.Args[0], argType);
        var list = Temp("list");
        var value = Temp("list_value");
        return $"({{ TcsList *{list} = {RenderExpr(call.Args[0])}; " +
            $"{listType.ElementCName} {value} = " +
            $"{RenderCoerced(call.Args[1], listType.Element!)}; " +
            $"tcs_list_add({list}, &{value}, sizeof({value})); }})";
    }

    private string RenderUserCall(IlCall call)
    {
        var (cls, method) = ParseUserCallee(call.Callee);
        var fact = _facts.Method(cls, method);
        if (!fact.IsStatic && call.Args.Length == fact.Parameters.Count + 1)
        {
            // base 呼び出し: dispatcher を通さない直呼び
            return RenderMethodCall(fact, call.Args[0],
                call.Args.Skip(1).ToArray());
        }
        _ = ValidateMethodCall(fact, null, call.Args);
        return RenderMethodCall(fact, null, call.Args);
    }

    private CType TypeOfDynCall(IlDynCall call)
    {
        if (TryTypeOfClosureCallee(call) is { } closureType)
        {
            RequireArity("closure call", call.Args.Length,
                closureType.Parameters!.Count);
            for (var i = 0; i < call.Args.Length; i++)
                RequireAssignable(closureType.Parameters[i],
                    TypeOf(call.Args[i]), $"closure argument {i}");
            return closureType.Element!;
        }
        var fact = ParseDynCallee(call.Callee);
        return ValidateMethodCall(fact, null, call.Args);
    }

    // closure 値の callee (変数/フィールド等) なら closure 型を返す
    private CType? TryTypeOfClosureCallee(IlDynCall call)
    {
        if (call.Callee is IlField { Recv: IlVar receiver }
            && _classes.ContainsKey(receiver.Name))
            return null; // 型修飾 static 呼び出し
        var type = TypeOf(call.Callee);
        return type.Kind == CTypeKind.Closure ? type : null;
    }

    private string RenderDynCall(IlDynCall call)
    {
        if (TryTypeOfClosureCallee(call) is { } closureType)
        {
            _ = TypeOfDynCall(call);
            var values = new List<(CType Type, string Value)>
            {
                (closureType,
                 $"(TcsClosure *)tcs_nonnull({RenderExpr(call.Callee)})"),
            };
            values.AddRange(call.Args.Select((arg, i) =>
                (closureType.Parameters![i],
                 RenderCoerced(arg, closureType.Parameters[i]))));
            var closTemp = Temp("call_closure");
            var argNames = new List<string>();
            var declarations = new StringBuilder();
            for (var i = 0; i < values.Count; i++)
            {
                var temp = i == 0 ? closTemp : Temp("closure_arg");
                declarations.Append(
                    $"{values[i].Type.CName} {temp} = {values[i].Value}; ");
                if (i > 0) argNames.Add(temp);
            }
            var callArgs = string.Join(", ",
                new[] { $"{closTemp}->cells" }.Concat(argNames));
            return $"({{ {declarations}" +
                $"(({ClosureFnPtrType(closureType)}){closTemp}->fn)({callArgs}); }})";
        }
        var fact = ParseDynCallee(call.Callee);
        _ = ValidateMethodCall(fact, null, call.Args);
        return RenderMethodCall(fact, null, call.Args);
    }

    private MethodFact ParseDynCallee(IlExpr callee)
    {
        if (callee is not IlField { Recv: IlVar receiver } field
            || !_classes.ContainsKey(receiver.Name))
            throw new Tcs2cException("only type-qualified static IlDynCall is supported");
        var fact = _facts.Method(receiver.Name, field.Name);
        if (!fact.IsStatic)
            throw new Tcs2cException($"dynamic call target is not static: " +
                $"{receiver.Name}.{field.Name}");
        return fact;
    }

    private MethodFact ResolveInvokeFact(CType receiver, string method)
    {
        if (receiver.Kind != CTypeKind.Ref)
            throw new Tcs2cException("IlInvoke receiver is not a class reference");
        var declaring = FindDeclaringClass(receiver.Name!, method)
            ?? throw new Tcs2cException(
                $"unknown method: {receiver.Name}.{method}");
        return _facts.Method(declaring, method);
    }

    private CType TypeOfInvoke(IlInvoke invoke)
    {
        var receiver = TypeOf(invoke.Recv);
        var fact = ResolveInvokeFact(receiver, invoke.Method);
        return ValidateMethodCall(fact, receiver, invoke.Args);
    }

    private string RenderInvoke(IlInvoke invoke)
    {
        _ = TypeOfInvoke(invoke);
        var receiver = TypeOf(invoke.Recv);
        var fact = ResolveInvokeFact(receiver, invoke.Method);
        // 子孫に再宣言があれば実行時型で dispatch (il-spec §9)
        if (IsPolymorphic(fact.ClassName, fact.Name))
            return RenderMethodCall(fact, invoke.Recv, invoke.Args,
                Names.Dispatch(fact.ClassName, fact.Name));
        return RenderMethodCall(fact, invoke.Recv, invoke.Args);
    }

    private CType ValidateMethodCall(MethodFact fact, CType? receiver,
        IReadOnlyList<IlExpr> args)
    {
        if (receiver is null && !fact.IsStatic)
            throw new Tcs2cException($"call target is not static: {fact.ClassName}.{fact.Name}");
        if (receiver is not null && fact.IsStatic)
            throw new Tcs2cException($"IlInvoke target is static: {fact.ClassName}.{fact.Name}");
        RequireArity($"{fact.ClassName}.{fact.Name}", args.Count, fact.Parameters.Count);
        for (var i = 0; i < args.Count; i++)
            RequireAssignable(fact.Parameters[i].Type, TypeOf(args[i]),
                $"argument {i} of {fact.ClassName}.{fact.Name}");
        return fact.ReturnType;
    }

    private string RenderMethodCall(MethodFact fact, IlExpr? receiver,
        IReadOnlyList<IlExpr> args, string? function = null)
    {
        var values = new List<(CType Type, string Value)>();
        if (receiver is not null)
        {
            var receiverType = CType.Ref(fact.ClassName);
            values.Add((receiverType,
                $"({receiverType.CName})tcs_nonnull({RenderExpr(receiver)})"));
        }
        values.AddRange(args.Select((arg, i) =>
            (fact.Parameters[i].Type,
             RenderCoerced(arg, fact.Parameters[i].Type))));
        return RenderOrderedCall(
            function ?? Names.Method(fact.ClassName, fact.Name),
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
            throw new Tcs2cException($"unsupported intrinsic or callee: {callee}");
        var cls = callee[..dot];
        var method = callee[(dot + 1)..];
        if (!_classes.ContainsKey(cls))
            throw new Tcs2cException($"unknown call target class: {callee}");
        return (cls, method);
    }

    private string RenderNew(IlNewObj creation)
    {
        if (!_classes.TryGetValue(creation.TypeName, out var cls))
            throw new Tcs2cException($"unknown class: {creation.TypeName}");
        var paramFacts = CtorParamFacts(cls);
        if (creation.Args.Length != paramFacts.Count)
            throw new Tcs2cException($"constructor {cls.Name}: expected " +
                $"{paramFacts.Count} arguments, got {creation.Args.Length}");
        if (creation.Args.Length == 0) return $"{Names.New(cls.Name)}()";
        var values = new List<(CType Type, string Value)>();
        for (var i = 0; i < creation.Args.Length; i++)
        {
            RequireAssignable(paramFacts[i].Type, TypeOf(creation.Args[i]),
                $"constructor argument {i} of {cls.Name}");
            values.Add((paramFacts[i].Type,
                RenderCoerced(creation.Args[i], paramFacts[i].Type)));
        }
        return RenderOrderedCall(Names.New(cls.Name),
            CType.Ref(cls.Name), values);
    }

    private CType TypeOfTable(IlTable table)
    {
        if (table.KeyType is not null
            || table.Entries.Any(e => e.Key is not null))
            return TypeOfDictTable(table);
        if (table.Entries.Any(e => e.NameKey is not null))
            throw new Tcs2cException("option-table IlTable is not supported");
        CType? element = table.ElementType is null
            ? null : _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            var itemType = TypeOf(entry.Value);
            element = element is null ? itemType : CommonType(element, itemType, "IlTable items");
        }
        if (element is not null && element.Kind is not (CTypeKind.I32
            or CTypeKind.F32 or CTypeKind.Bool or CTypeKind.String
            or CTypeKind.Ref or CTypeKind.Closure or CTypeKind.List
            or CTypeKind.Dict or CTypeKind.Array))
            throw new Tcs2cException($"unsupported List element type: {element}");
        return CType.List(element);
    }

    private string RenderTable(IlTable table)
    {
        if (table.KeyType is not null || table.Entries.Any(e => e.Key is not null))
            return RenderDictTable(table);
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

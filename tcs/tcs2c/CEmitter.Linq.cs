using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// List.* (runtime/tinysystem.lua の List 表面 + LINQ 小核) を要素型ごとの
// inline loop に展開する。closure は呼び出し側の要素型で型付けし
// (IlClosure は引数型を持たない)、戻り型は本体から推論する。sort は
// runtime の安定 merge sort + call site ごとの lifted 比較関数。
internal sealed partial class CEmitter
{
    private int _comparatorSerial;

    private static bool IsNilLiteral(IlExpr expr) => expr is IlLit { LuaText: "nil" };

    private static (string Len, string At) SeqFns(CType seq) =>
        seq.Kind == CTypeKind.Array
            ? ("tcs_array_length", "tcs_array_at")
            : ("tcs_list_length", "tcs_list_at");

    private CType RequireSeq(IlExpr expr, string where)
    {
        var type = TypeOf(expr);
        if (type.Kind is not (CTypeKind.List or CTypeKind.Array) || type.Element is null)
            throw new Tcs2cException($"{where}: receiver is not a typed List/array ({type})");
        return type;
    }

    // closure 値の戻り型: IlClosure は本体から、static method group は
    // 契約から、closure 型の変数はその型から
    private CType InferClosureResult(IlExpr fn, IReadOnlyList<CType> paramTypes)
    {
        switch (fn)
        {
            case IlClosure closure:
            {
                if (closure.Params.Length != paramTypes.Count)
                    throw new Tcs2cException("closure arity mismatch");
                PushScope();
                for (var i = 0; i < paramTypes.Count; i++)
                    AddVariable(closure.Params[i], new Variable("__infer", paramTypes[i]));
                var result = closure.ExprBody != null
                    ? TypeOf(closure.ExprBody)
                    : ScanReturnType(closure.Body!.Stats);
                PopScope();
                return result ?? CType.Void;
            }
            case IlField { Recv: IlVar recv } group when _classes.ContainsKey(recv.Name):
                return _facts.Method(recv.Name, group.Name).ReturnType;
            default:
            {
                var type = TypeOf(fn);
                if (type.Kind != CTypeKind.Closure)
                    throw new Tcs2cException($"expected a closure, got {type}");
                return type.Element!;
            }
        }
    }

    // block 本体の最初の return の型 (local は型だけ登録、emit しない)
    private CType? ScanReturnType(IEnumerable<IlStat> stats)
    {
        foreach (var stat in stats)
        {
            switch (stat)
            {
                case IlLocal local:
                {
                    var type = (local.Type != null ? _facts.TryMapType(local.Type) : null)
                        ?? (local.Init != null ? TypeOf(local.Init)
                            : throw new Tcs2cException($"cannot type local {local.Name}"));
                    AddVariable(local.Name, new Variable("__infer", type));
                    break;
                }
                case IlReturn { Value: { } value }:
                    return TypeOf(value);
                case IlMultiAssign { Declare: true } multi
                    when multi.Values is [IlCall tryGet]
                        && multi.Targets is [IlVar foundVar, IlVar valueVar]:
                    AddVariable(foundVar.Name, new Variable("__infer", CType.Bool));
                    AddVariable(valueVar.Name, new Variable("__infer",
                        TryParseValueType(tryGet.Callee)
                        ?? RequireDict(tryGet.Args[0], out _)));
                    break;
                case IlIf conditional:
                {
                    foreach (var (_, body) in conditional.Arms)
                        if (Scoped(body.Stats) is { } t) return t;
                    if (conditional.Else != null && Scoped(conditional.Else.Stats) is { } e)
                        return e;
                    break;
                }
                case IlWhile loop:
                    if (Scoped(loop.Body.Stats) is { } w) return w;
                    break;
                case IlRepeat repeat:
                    if (Scoped(repeat.Body.Stats) is { } r) return r;
                    break;
                case IlDo block:
                    if (Scoped(block.Body.Stats) is { } d) return d;
                    break;
                case IlBreakScope scope:
                    if (Scoped(scope.Body.Stats) is { } bs) return bs;
                    break;
                case IlNumericFor loop:
                {
                    PushScope();
                    AddVariable(loop.Var, new Variable("__infer", CType.I32));
                    var t = ScanReturnType(loop.Body.Stats);
                    PopScope();
                    if (t != null) return t;
                    break;
                }
                case IlForeachList loop:
                {
                    PushScope();
                    AddVariable(loop.Var, new Variable("__infer",
                        RequireSeq(loop.Coll, "foreach").Element!));
                    var t = ScanReturnType(loop.Body.Stats);
                    PopScope();
                    if (t != null) return t;
                    break;
                }
            }
        }
        return null;

        CType? Scoped(IEnumerable<IlStat> body)
        {
            PushScope();
            var t = ScanReturnType(body);
            PopScope();
            return t;
        }
    }

    // 式位置の逐次実行 (IlIife) は GNU statement expression。内側の return
    // は結果変数への代入 + 末尾 label への goto (式の外へは飛ばない)
    private Stack<(string Result, string Label, CType Type)> _iifes = new();

    private CType TypeOfIife(IlIife iife)
    {
        PushScope();
        var type = ScanReturnType(iife.Stats);
        PopScope();
        return type ?? CType.Void;
    }

    private string RenderIife(IlIife iife)
    {
        var type = TypeOfIife(iife);
        var result = Temp("iife");
        var label = Temp("iife_done");
        var start = _output.Length;
        var savedIndent = _indent;
        _indent = 1;
        PushScope();
        _iifes.Push((result, label, type));
        EmitStats(iife.Stats);
        _iifes.Pop();
        PopScope();
        var body = _output.ToString(start, _output.Length - start);
        _output.Length = start;
        _indent = savedIndent;
        var declaration = type == CType.Void
            ? "" : $"{type.CName} {result} = {ZeroInit(type)}; ";
        var value = type == CType.Void ? "" : $"{result}; ";
        return $"({{ {declaration}\n{body}{label}: ;\n{value}}})";
    }

    private string CallClosure(string closure, CType closureType, params string[] args) =>
        $"(({ClosureFnPtrType(closureType)}){closure}->fn)(" +
        string.Join(", ", new[] { $"{closure}->cells" }.Concat(args)) + ")";

    private string EqualExpr(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool => $"({left} == {right})",
        CTypeKind.String => $"tcs_string_equal({left}, {right})",
        CTypeKind.Ref when IsRecordClass(type.Name!) =>
            $"{Names.RecordEq(type.Name!)}({left}, {right})",
        CTypeKind.Ref or CTypeKind.Array or CTypeKind.List or CTypeKind.Dict
            or CTypeKind.Closure or CTypeKind.Random => $"({left} == {right})",
        CTypeKind.Nullable => NullableEqualExpr(type, left, right),
        // struct 値は memberwise (C# の ValueType.Equals / record struct の ==)
        CTypeKind.StructVal => $"({{ {type.CName} eq_l = {left}; {type.CName} eq_r = {right}; " +
            $"{Names.StructEq(type.Name!)}(&eq_l, &eq_r); }})",
        _ => throw new Tcs2cException($"equality is not supported for {type}"),
    };

    private static string LessExpr(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 => $"({left} < {right})",
        CTypeKind.String => $"(tcs_string_compare({left}, {right}) < 0)",
        _ => throw new Tcs2cException($"ordering is not supported for {type}"),
    };

    private static string ZeroInit(CType type) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool => "0",
        CTypeKind.StructVal or CTypeKind.Nullable => $"({type.CName}){{0}}",
        _ => "NULL",
    };

    private CType TypeOfListOp(IlCall call)
    {
        var name = call.Callee["List.".Length..];
        var args = call.Args;
        var seq = RequireSeq(args[0], call.Callee);
        var elem = seq.Element!;
        CType Pred(int i) => InferClosureResult(args[i], [elem]) == CType.Bool
            ? CType.Bool : throw new Tcs2cException($"{call.Callee}: predicate must return bool");
        switch (name)
        {
            case "Where":
                RequireArity(call.Callee, args.Length, 2); _ = Pred(1);
                return CType.List(elem);
            case "Select":
                RequireArity(call.Callee, args.Length, 2);
                return CType.List(InferClosureResult(args[1], [elem]));
            case "Any":
                if (args.Length == 2) _ = Pred(1);
                return CType.Bool;
            case "All":
                RequireArity(call.Callee, args.Length, 2); _ = Pred(1);
                return CType.Bool;
            case "First" or "Last":
                if (args.Length == 2) _ = Pred(1);
                return elem;
            case "FirstOrDefault" or "LastOrDefault":
                RequireArity(call.Callee, args.Length, 3);
                if (!IsNilLiteral(args[1])) _ = Pred(1);
                CheckAssignable(elem, args[2], $"{call.Callee} default");
                return elem;
            case "Count":
                if (args.Length == 2) _ = Pred(1);
                return CType.I32;
            case "Sum" or "Min" or "Max":
            {
                var key = args.Length == 2 ? InferClosureResult(args[1], [elem]) : elem;
                if (name == "Sum")
                    return NumericJoin(key, CType.I32, name);
                if (key.Kind is not (CTypeKind.I32 or CTypeKind.F32 or CTypeKind.String))
                    throw new Tcs2cException($"{call.Callee}: keys must be numbers or strings");
                return key;
            }
            case "OrderBy" or "OrderByDescending":
            {
                RequireArity(call.Callee, args.Length, 2);
                var key = InferClosureResult(args[1], [elem]);
                _ = LessExpr(key, "a", "b");
                return CType.List(elem);
            }
            case "Take" or "Skip":
                RequireArity(call.Callee, args.Length, 2);
                RequireType(CType.I32, TypeOf(args[1]), call.Callee);
                return CType.List(elem);
            case "ToList":
                RequireArity(call.Callee, args.Length, 1);
                return CType.List(elem);
            case "ToDictionary":
            {
                if (args.Length is not (2 or 3))
                    throw new Tcs2cException("ToDictionary: expected 2 or 3 arguments");
                var key = InferClosureResult(args[1], [elem]);
                if (key.Kind is not (CTypeKind.I32 or CTypeKind.String))
                    throw new Tcs2cException($"Dictionary key type not supported: {key}");
                var value = args.Length == 3 ? InferClosureResult(args[2], [elem]) : elem;
                return CType.Dict(key, value);
            }
            // struct 要素では Lua 向けの op_Equality 参照が 3 番目に付く
            // (C は要素型から memberwise 比較を生成するので読まない)
            case "Contains":
                RequireArity(call.Callee, args.Length, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                CheckAssignable(elem, args[1], call.Callee);
                return CType.Bool;
            case "IndexOf":
                RequireArity(call.Callee, args.Length, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                CheckAssignable(elem, args[1], call.Callee);
                return CType.I32;
            case "Remove":
                RequireArity(call.Callee, args.Length, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                if (seq.Kind != CTypeKind.List) throw new Tcs2cException("Remove: receiver is not a List");
                CheckAssignable(elem, args[1], call.Callee);
                return CType.Bool;
            case "Sort":
                if (seq.Kind != CTypeKind.List) throw new Tcs2cException("Sort: receiver is not a List");
                if (args.Length == 2)
                    RequireType(CType.I32, InferClosureResult(args[1], [elem, elem]), "Sort comparison");
                else
                    _ = LessExpr(elem, "a", "b");
                return CType.Void;
            default:
                throw new Tcs2cException($"unsupported List member: {name}");
        }
    }

    private string RenderListOp(IlCall call)
    {
        var name = call.Callee["List.".Length..];
        var args = call.Args;
        var resultType = TypeOfListOp(call);
        var seq = RequireSeq(args[0], call.Callee);
        var elem = seq.Element!;
        var (len, at) = SeqFns(seq);
        var s = Temp("seq");
        var n = Temp("n");
        var i = Temp("i");
        var v = Temp("v");
        var f = Temp("fn");
        var sb = new StringBuilder($"{seq.CName} {s} = {RenderExpr(args[0])}; ");
        string Loop(string body, bool reverse = false) =>
            $"int32_t {n} = {len}({s}); " +
            (reverse
                ? $"for (int32_t {i} = {n} - 1; {i} >= 0; {i}--) {{ "
                : $"for (int32_t {i} = 0; {i} < {n}; {i}++) {{ ") +
            $"{elem.CName} {v} = *({elem.CName} *){at}({s}, {i}); {body} }} ";
        string Closure(int index, CType ret, params CType[] parameters)
        {
            var type = CType.Closure(ret, parameters);
            sb.Append($"TcsClosure *{f} = (TcsClosure *)tcs_nonnull(" +
                $"{RenderCoerced(args[index], type)}); ");
            return CallClosure(f, type, parameters.Length == 2 ? [v, $"{v}2"] : [v]);
        }
        switch (name)
        {
            case "Where":
            {
                var call1 = Closure(1, CType.Bool, elem);
                var layout = LayoutRef(elem);
                var outList = Temp("out");
                sb.Append($"TcsList *{outList} = tcs_list_new(sizeof({elem.CName}), {layout}); ");
                sb.Append(Loop($"if ({call1}) tcs_list_add({outList}, &{v}, sizeof({v}), {layout});"));
                return $"({{ {sb}{outList}; }})";
            }
            case "Select":
            {
                var u = resultType.Element!;
                var call1 = Closure(1, u, elem);
                var layout = LayoutRef(u);
                var outList = Temp("out");
                var mapped = Temp("u");
                sb.Append($"TcsList *{outList} = tcs_list_new(sizeof({u.CName}), {layout}); ");
                sb.Append(Loop($"{u.CName} {mapped} = {call1}; " +
                    $"tcs_list_add({outList}, &{mapped}, sizeof({mapped}), {layout});"));
                return $"({{ {sb}{outList}; }})";
            }
            case "Any":
            {
                if (args.Length == 1) return $"({{ {sb}{len}({s}) > 0; }})";
                var call1 = Closure(1, CType.Bool, elem);
                var r = Temp("any");
                sb.Append($"bool {r} = false; ");
                sb.Append(Loop($"if ({call1}) {{ {r} = true; break; }}"));
                return $"({{ {sb}{r}; }})";
            }
            case "All":
            {
                var call1 = Closure(1, CType.Bool, elem);
                var r = Temp("all");
                sb.Append($"bool {r} = true; ");
                sb.Append(Loop($"if (!({call1})) {{ {r} = false; break; }}"));
                return $"({{ {sb}{r}; }})";
            }
            case "First" or "Last" or "FirstOrDefault" or "LastOrDefault":
            {
                var reverse = name.StartsWith("Last", StringComparison.Ordinal);
                var orDefault = name.EndsWith("OrDefault", StringComparison.Ordinal);
                var hasPred = orDefault ? !IsNilLiteral(args[1]) : args.Length == 2;
                var r = Temp("found");
                var has = Temp("has");
                sb.Append($"{elem.CName} {r} = {ZeroInit(elem)}; bool {has} = false; ");
                var cond = hasPred ? $"if ({Closure(1, CType.Bool, elem)}) " : "";
                sb.Append(Loop($"{cond}{{ {r} = {v}; {has} = true; break; }}", reverse));
                if (orDefault)
                    sb.Append($"if (!{has}) {r} = {RenderCoerced(args[2], elem)}; ");
                else
                    sb.Append($"if (!{has}) tcs_fault(\"{(hasPred
                        ? "Sequence contains no matching element"
                        : "Sequence contains no elements")}\"); ");
                return $"({{ {sb}{r}; }})";
            }
            case "Count":
            {
                if (args.Length == 1) return $"({{ {sb}{len}({s}); }})";
                var call1 = Closure(1, CType.Bool, elem);
                var r = Temp("count");
                sb.Append($"int32_t {r} = 0; ");
                sb.Append(Loop($"if ({call1}) {r}++;"));
                return $"({{ {sb}{r}; }})";
            }
            case "Sum":
            {
                var acc = Temp("sum");
                var value = args.Length == 2
                    ? Closure(1, InferClosureResult(args[1], [elem]), elem) : v;
                sb.Append($"{resultType.CName} {acc} = 0; ");
                sb.Append(Loop($"{acc} = {acc} + {value};"));
                return $"({{ {sb}{acc}; }})";
            }
            case "Min" or "Max":
            {
                var best = Temp("best");
                var has = Temp("has");
                var k = Temp("k");
                var value = args.Length == 2 ? Closure(1, resultType, elem) : v;
                var better = name == "Min" ? LessExpr(resultType, k, best) : LessExpr(resultType, best, k);
                sb.Append($"{resultType.CName} {best} = {ZeroInit(resultType)}; bool {has} = false; ");
                sb.Append(Loop($"{resultType.CName} {k} = {value}; " +
                    $"if (!{has} || {better}) {{ {best} = {k}; {has} = true; }}"));
                sb.Append($"if (!{has}) tcs_fault(\"Sequence contains no elements\"); ");
                return $"({{ {sb}{best}; }})";
            }
            case "OrderBy" or "OrderByDescending":
            {
                var key = InferClosureResult(args[1], [elem]);
                var type = CType.Closure(key, [elem]);
                sb.Append($"TcsClosure *{f} = (TcsClosure *)tcs_nonnull(" +
                    $"{RenderCoerced(args[1], type)}); ");
                var copy = Temp("sorted");
                sb.Append($"TcsList *{copy} = {(seq.Kind == CTypeKind.Array ? "tcs_array_to_list" : "tcs_list_copy")}({s}); ");
                var comparator = LiftComparator(elem, key, type,
                    name == "OrderBy" ? "key" : "keydesc");
                sb.Append($"tcs_list_sort({copy}, {comparator}, {f}); ");
                return $"({{ {sb}{copy}; }})";
            }
            case "Take" or "Skip":
            {
                var count = Temp("count");
                var layout = LayoutRef(elem);
                var outList = Temp("out");
                sb.Append($"int32_t {count} = {RenderExpr(args[1])}; if ({count} < 0) {count} = 0; ");
                sb.Append($"TcsList *{outList} = tcs_list_new(sizeof({elem.CName}), {layout}); ");
                var cond = name == "Take" ? $"if ({i} >= {count}) break; " : $"if ({i} < {count}) continue; ";
                sb.Append(Loop($"{cond}tcs_list_add({outList}, &{v}, sizeof({v}), {layout});"));
                return $"({{ {sb}{outList}; }})";
            }
            case "ToList":
                return $"({{ {sb}{(seq.Kind == CTypeKind.Array ? "tcs_array_to_list" : "tcs_list_copy")}({s}); }})";
            case "ToDictionary":
            {
                var key = resultType.Key!;
                var value = resultType.Element!;
                var keyType = CType.Closure(key, [elem]);
                var fk = Temp("keyfn");
                sb.Append($"TcsClosure *{fk} = (TcsClosure *)tcs_nonnull({RenderCoerced(args[1], keyType)}); ");
                string valueExpr = v;
                if (args.Length == 3)
                {
                    var valueType = CType.Closure(value, [elem]);
                    var fv = Temp("valfn");
                    sb.Append($"TcsClosure *{fv} = (TcsClosure *)tcs_nonnull({RenderCoerced(args[2], valueType)}); ");
                    valueExpr = CallClosure(fv, valueType, v);
                }
                var dict = Temp("dict");
                var k = Temp("k");
                var val = Temp("val");
                var keyArgs = key.Kind == CTypeKind.String ? $"0, {k}" : $"{k}, NULL";
                sb.Append($"TcsDict *{dict} = tcs_dict_new({(key.Kind == CTypeKind.String ? 1 : 0)}, " +
                    $"sizeof({value.CName}), {LayoutRef(value)}); ");
                sb.Append(Loop($"{key.CName} {k} = {CallClosure(fk, keyType, v)}; " +
                    $"{value.CName} {val} = {valueExpr}; " +
                    $"*({value.CName} *)tcs_dict_put({dict}, {keyArgs}) = {val};"));
                return $"({{ {sb}{dict}; }})";
            }
            case "Contains" or "IndexOf" or "Remove":
            {
                var target = Temp("target");
                var r = Temp("index");
                sb.Append($"{elem.CName} {target} = {RenderCoerced(args[1], elem)}; int32_t {r} = -1; ");
                sb.Append(Loop($"if ({EqualExpr(elem, v, target)}) {{ {r} = {i}; break; }}"));
                return name switch
                {
                    "Contains" => $"({{ {sb}{r} >= 0; }})",
                    "IndexOf" => $"({{ {sb}{r}; }})",
                    _ => $"({{ {sb}if ({r} >= 0) tcs_list_remove_at({s}, {r}); {r} >= 0; }})",
                };
            }
            case "Sort":
            {
                if (args.Length == 2)
                {
                    var type = CType.Closure(CType.I32, [elem, elem]);
                    sb.Append($"TcsClosure *{f} = (TcsClosure *)tcs_nonnull({RenderCoerced(args[1], type)}); ");
                    var comparator = LiftComparator(elem, null, type, "comparison");
                    return $"({{ {sb}tcs_list_sort({s}, {comparator}, {f}); }})";
                }
                var natural = LiftComparator(elem, null, null, "natural");
                return $"({{ {sb}tcs_list_sort({s}, {natural}, NULL); }})";
            }
            default:
                throw new Tcs2cException($"unsupported List member: {name}");
        }
    }

    // sort 用の lifted 比較関数 (ctx = closure)。mode: natural / comparison /
    // key / keydesc
    private string LiftComparator(CType elem, CType? key, CType? closureType, string mode)
    {
        var fnName = $"tcs_cmp_{_comparatorSerial++}";
        _closureDecls.Add($"static int {fnName}(void *, const void *, const void *);");
        var body = new StringBuilder();
        body.Append($"static int\n{fnName}(void *ctx, const void *a, const void *b)\n{{\n");
        body.Append($"    TcsClosure *f = ctx; (void)f;\n");
        body.Append($"    {elem.CName} x = *(const {elem.CName} *)a;\n");
        body.Append($"    {elem.CName} y = *(const {elem.CName} *)b;\n");
        switch (mode)
        {
            case "natural":
                body.Append($"    return {LessExpr(elem, "x", "y")} ? -1 : {LessExpr(elem, "y", "x")} ? 1 : 0;\n");
                break;
            case "comparison":
                body.Append($"    int32_t r = {CallClosure("f", closureType!, "x", "y")};\n");
                body.Append("    return r < 0 ? -1 : r > 0 ? 1 : 0;\n");
                break;
            default:
            {
                body.Append($"    {key!.CName} kx = {CallClosure("f", closureType!, "x")};\n");
                body.Append($"    {key.CName} ky = {CallClosure("f", closureType!, "y")};\n");
                var (lo, hi) = mode == "key" ? ("kx", "ky") : ("ky", "kx");
                body.Append($"    return {LessExpr(key, lo, hi)} ? -1 : {LessExpr(key, hi, lo)} ? 1 : 0;\n");
                break;
            }
        }
        body.Append("}\n\n");
        _pendingClosures.Add(body.ToString());
        return fnName;
    }
}

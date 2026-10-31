using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// List.* (runtime/tinysystem.lua の List 表面) を要素型ごとの inline loop に展開する。
// closure は呼び出し側の要素型で型付けし (IlClosure は引数型を持たない)、戻り型は本体から
// 推論する。Sort は runtime の introsort + call site ごとの lifted 比較関数。
// LINQ は TinySystem/Linq.cs を user のコードとして変換するのでここには無い
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
            case IlMethodGroup group:
                return _facts.Method(group.TypeRef, group.Method).ReturnType;
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

    // List の検索の等価 (EqualityComparer<T>.Default: float は NaN 同士も等しい)
    private string EqualExpr(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.Bool => $"({left} == {right})",
        CTypeKind.F32 => $"tcs_float_equals({left}, {right})",
        CTypeKind.String => $"tcs_string_equal({left}, {right})",
        CTypeKind.Ref when IsRecordClass(type.Name!) =>
            $"{Names.RecordEq(type.Name!)}({left}, {right})",
        CTypeKind.Ref or CTypeKind.Array or CTypeKind.List or CTypeKind.Dict
            or CTypeKind.Closure or CTypeKind.Random => $"({left} == {right})",
        CTypeKind.Nullable => NullableEqualExpr(type, left, right, equals: true),
        // struct 値は memberwise (C# の ValueType.Equals / record struct の ==)
        CTypeKind.StructVal => $"({{ {type.CName} eq_l = {left}; {type.CName} eq_r = {right}; " +
            $"{Names.StructEq(type.Name!)}(&eq_l, &eq_r); }})",
        _ => throw new Tcs2cException($"equality is not supported for {type}"),
    };

    // sort 用の 3 方向比較。float は Single.CompareTo と同じく NaN を最小にする
    private static string CompareExpr(CType type, string left, string right) =>
        type.Kind == CTypeKind.F32
            ? $"({left} < {right} ? -1 : {left} > {right} ? 1 : {left} == {right} ? 0 : " +
              $"{left} != {left} ? ({right} != {right} ? 0 : -1) : 1)"
            : $"({LessExpr(type, left, right)} ? -1 : {LessExpr(type, right, left)} ? 1 : 0)";

    private static string LessExpr(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 => $"({left} < {right})",
        // null は最小 (.NET の既定の comparer)
        CTypeKind.String => $"(tcs_string_compare_ordinal({left}, {right}) < 0)",
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
        switch (name)
        {
            // 変更可能な struct 要素では Lua 向けの `S.__copy` 参照が 2 番目に付く
            // (C は要素を値で写すので読まない)
            case "Copy":
                if (args.Length is not (1 or 2))
                    throw new Tcs2cException("List.Copy: expected 1 argument");
                return CType.List(elem);
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
            case "Insert":
                RequireArity(call.Callee, args.Length, 3);
                if (seq.Kind != CTypeKind.List) throw new Tcs2cException("Insert: receiver is not a List");
                RequireType(CType.I32, TypeOf(args[1]), "Insert index");
                if (!IsClosureValue(args[2])) CheckAssignable(elem, args[2], call.Callee);
                return CType.Void;
            // 変更可能な struct 要素では Lua 向けの `S.__copy` 参照が末尾に付く
            case "GetRange":
                if (args.Length is not (3 or 4))
                    throw new Tcs2cException("List.GetRange: expected 2 arguments");
                if (seq.Kind != CTypeKind.List) throw new Tcs2cException("GetRange: receiver is not a List");
                RequireType(CType.I32, TypeOf(args[1]), "GetRange index");
                RequireType(CType.I32, TypeOf(args[2]), "GetRange count");
                return CType.List(elem);
            case "FindIndex" or "RemoveAll":
                RequireArity(call.Callee, args.Length, 2);
                if (name == "RemoveAll" && seq.Kind != CTypeKind.List)
                    throw new Tcs2cException("RemoveAll: receiver is not a List");
                if (InferClosureResult(args[1], [elem]) != CType.Bool)
                    throw new Tcs2cException($"{call.Callee}: predicate must return bool");
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
            // C# と同じく受け手、添字、値の順に評価してから範囲を見る
            case "Insert":
            {
                var index = Temp("index");
                sb.Append($"int32_t {index} = {RenderExpr(args[1])}; ");
                sb.Append($"{elem.CName} {v} = {RenderCoerced(args[2], elem)}; ");
                return $"({{ {sb}tcs_list_insert({s}, {index}, &{v}, sizeof({v}), {LayoutRef(elem)}); }})";
            }
            case "GetRange":
            {
                var index = Temp("index");
                var count = Temp("count");
                sb.Append($"int32_t {index} = {RenderExpr(args[1])}; int32_t {count} = {RenderExpr(args[2])}; ");
                return $"({{ {sb}tcs_list_get_range({s}, {index}, {count}); }})";
            }
            case "Copy":
                return $"({{ {sb}{(seq.Kind == CTypeKind.Array ? "tcs_array_to_list" : "tcs_list_copy")}({s}); }})";
            case "FindIndex":
            {
                var match = Closure(1, CType.Bool, elem);
                var r = Temp("index");
                sb.Append($"int32_t {r} = -1; ");
                sb.Append(Loop($"if ({match}) {{ {r} = {i}; break; }}"));
                return $"({{ {sb}{r}; }})";
            }
            // .NET の RemoveAll と同じ手順で、残す要素をその場で前へ詰める (tcs_list_remove_at と
            // 同じく要素の byte を写す)。述語が list を変えても同じ結果になるよう、長さは毎回読む
            case "RemoveAll":
            {
                var match = Closure(1, CType.Bool, elem);
                var free = Temp("free");
                var current = Temp("current");
                var removed = Temp("removed");
                string Element(string index) => $"*({elem.CName} *){at}({s}, {index})";
                string Skip(string index, bool matching) =>
                    $"while ({index} < {len}({s})) {{ {elem.CName} {v} = {Element(index)}; " +
                    $"if ({(matching ? "!" : "")}({match})) break; {index}++; }} ";
                sb.Append($"int32_t {free} = 0; int32_t {removed} = 0; ");
                sb.Append(Skip(free, matching: false));
                sb.Append($"if ({free} < {len}({s})) {{ int32_t {current} = {free} + 1; ");
                sb.Append($"while ({current} < {len}({s})) {{ {Skip(current, matching: true)}");
                sb.Append($"if ({current} < {len}({s})) {{ {Element(free)} = {Element(current)}; {free}++; {current}++; }} }} ");
                // 述語が list を詰めて free より短くした場合は .NET の _size のように伸ばせないので切らない
                sb.Append($"{removed} = {len}({s}) - {free}; if ({removed} > 0) tcs_list_truncate({s}, (size_t){free}); }} ");
                return $"({{ {sb}{removed}; }})";
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
                    var comparator = LiftComparator(elem, type, "comparison");
                    return $"({{ {sb}tcs_list_sort({s}, 0, {comparator}, {f}); }})";
                }
                var natural = LiftComparator(elem, null, "natural");
                // float は .NET と同じく NaN を先頭に寄せてから残りを並べる
                var start = elem.Kind == CTypeKind.F32 ? $"tcs_list_move_nans_to_front({s})" : "0";
                return $"({{ {sb}tcs_list_sort({s}, {start}, {natural}, NULL); }})";
            }
            default:
                throw new Tcs2cException($"unsupported List member: {name}");
        }
    }

    // sort 用の lifted 比較関数。mode: natural / comparison (ctx = closure)
    private string LiftComparator(CType elem, CType? closureType, string mode)
    {
        var fnName = $"tcs_cmp_{_comparatorSerial++}";
        _closureDecls.Add($"static int {fnName}(void *, const void *, const void *);");
        var body = new StringBuilder();
        body.Append($"static int\n{fnName}(void *ctx, const void *a, const void *b)\n{{\n");
        // const は要素そのものに掛ける (後置)。要素が pointer (TcsString * 等) のとき
        // 前置の const は指す先に掛かり、x / y への初期化で const が落ちる
        body.Append($"    TcsClosure *f = ctx; (void)f;\n");
        body.Append($"    {elem.CName} x = *({elem.CName} const *)a;\n");
        body.Append($"    {elem.CName} y = *({elem.CName} const *)b;\n");
        switch (mode)
        {
            case "natural":
                body.Append($"    return {CompareExpr(elem, "x", "y")};\n");
                break;
            case "comparison":
                body.Append($"    int32_t r = {CallClosure("f", closureType!, "x", "y")};\n");
                body.Append("    return r < 0 ? -1 : r > 0 ? 1 : 0;\n");
                break;
            default:
                throw new Tcs2cException($"unknown comparator mode: {mode}");
        }
        body.Append("}\n\n");
        _pendingClosures.Add(body.ToString());
        return fnName;
    }
}

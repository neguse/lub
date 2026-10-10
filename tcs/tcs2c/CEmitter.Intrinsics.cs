using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// TinySystem runtime 表面 (String / Math / Dict.Keys / Values) と Lua 標準
// 関数 (string.* / math.* / tonumber / os.getenv / table.remove) の IlCall
// を C runtime (CRuntime.Strings / Lib) へ写す。List.* は CEmitter.Linq。
internal sealed partial class CEmitter
{
    private static readonly HashSet<string> IntrinsicPrefixes =
        ["String.", "Math.", "List.", "string.", "math.", "Random.", "Char."];

    private static readonly HashSet<string> IntrinsicNames =
        ["tonumber", "os.getenv", "table.remove", "__tcs_trunc",
         "Dict.Keys", "Dict.Values"];

    // facade 経由 (`using TinySystem;`) は "TinySystem.X.Y" で来る
    private static string NormalizeCallee(string callee) =>
        callee.StartsWith("TinySystem.", StringComparison.Ordinal)
            ? callee["TinySystem.".Length..] : callee;

    private bool IsIntrinsicCallee(string rawCallee)
    {
        var callee = NormalizeCallee(rawCallee);
        if (IntrinsicNames.Contains(callee)) return true;
        var dot = callee.IndexOf('.');
        if (dot <= 0) return false;
        // 同名の user class があればそちらが優先 (user call 経路)
        if (_classes.ContainsKey(callee[..dot])
            || _facts.Structs.ContainsKey(callee[..dot])) return false;
        return IntrinsicPrefixes.Contains(callee[..(dot + 1)]);
    }

    private CType TypeOfIntrinsic(IlCall rawCall)
    {
        var call = rawCall with { Callee = NormalizeCallee(rawCall.Callee) };
        var args = call.Args;
        if (call.Callee.StartsWith("Random.", StringComparison.Ordinal))
            return TypeOfRandomOp(call);
        if (call.Callee.StartsWith("Char.", StringComparison.Ordinal))
        {
            RequireArity(call.Callee, args.Length, 1);
            RequireType(CType.I32, TypeOf(args[0]), call.Callee);
            return call.Callee is "Char.ToUpper" or "Char.ToLower" ? CType.I32 : CType.Bool;
        }
        if (call.Callee.StartsWith("List.", StringComparison.Ordinal))
            return TypeOfListOp(call);
        switch (call.Callee)
        {
            case "__tcs_trunc":
                RequireArity(call.Callee, args.Length, 1);
                _ = NumericJoin(TypeOf(args[0]), CType.I32, "trunc");
                return CType.I32;
            case "tonumber":
                RequireArity(call.Callee, args.Length, 1);
                RequireType(CType.String, TypeOf(args[0]), "tonumber");
                return CType.F32;
            case "math.tointeger":
                RequireArity(call.Callee, args.Length, 1);
                if (args[0] is not IlCall { Callee: "tonumber", Args.Length: 1 } inner)
                    throw new Tcs2cException("math.tointeger is only supported on tonumber()");
                RequireType(CType.String, TypeOf(inner.Args[0]), "int.Parse");
                return CType.I32;
            case "math.fmod":
                RequireArity(call.Callee, args.Length, 2);
                _ = NumericJoin(TypeOf(args[0]), TypeOf(args[1]), "fmod");
                return CType.F32;
            case "os.getenv":
                RequireArity(call.Callee, args.Length, 1);
                RequireType(CType.String, TypeOf(args[0]), "os.getenv");
                return CType.String;
            case "string.sub":
                if (args.Length is not (2 or 3))
                    throw new Tcs2cException("string.sub: expected 2 or 3 arguments");
                RequireType(CType.String, TypeOf(args[0]), "string.sub");
                for (var i = 1; i < args.Length; i++)
                    RequireType(CType.I32, TypeOf(args[i]), "string.sub index");
                return CType.String;
            case "string.byte":
                if (args.Length is not (1 or 2))
                    throw new Tcs2cException("string.byte: expected 1 or 2 arguments");
                RequireType(CType.String, TypeOf(args[0]), "string.byte");
                if (args.Length == 2) RequireType(CType.I32, TypeOf(args[1]), "string.byte index");
                return CType.I32;
            case "string.char":
                RequireArity(call.Callee, args.Length, 1);
                RequireType(CType.I32, TypeOf(args[0]), "string.char");
                return CType.String;
            case "string.upper" or "string.lower":
                RequireArity(call.Callee, args.Length, 1);
                RequireType(CType.String, TypeOf(args[0]), call.Callee);
                return CType.String;
            case "string.format":
                if (args.Length == 0) throw new Tcs2cException("string.format: missing format");
                RequireType(CType.String, TypeOf(args[0]), "string.format");
                for (var i = 1; i < args.Length; i++) _ = FormatArgKind(TypeOf(args[i]));
                return CType.String;
            case "table.remove":
                RequireArity(call.Callee, args.Length, 2);
                RequireType(CType.I32, TypeOf(args[1]), "RemoveAt index");
                return RequireList(args[0]).Element!;
            case "Dict.Keys":
                RequireArity(call.Callee, args.Length, 1);
                _ = RequireDict(args[0], out var keyDict);
                return CType.List(keyDict.Key!);
            case "Dict.Values":
                RequireArity(call.Callee, args.Length, 1);
                return CType.List(RequireDict(args[0], out _));
        }
        if (call.Callee.StartsWith("String.", StringComparison.Ordinal))
            return TypeOfStringOp(call);
        if (call.Callee.StartsWith("Math.", StringComparison.Ordinal))
            return TypeOfMathOp(call);
        throw new Tcs2cException($"unsupported intrinsic: {call.Callee}");
    }

    private string RenderIntrinsic(IlCall rawCall)
    {
        var call = rawCall with { Callee = NormalizeCallee(rawCall.Callee) };
        var type = TypeOfIntrinsic(call);
        var args = call.Args;
        if (call.Callee.StartsWith("Random.", StringComparison.Ordinal))
            return RenderRandomOp(call, type);
        if (call.Callee.StartsWith("Char.", StringComparison.Ordinal))
        {
            var fn = call.Callee["Char.".Length..] switch
            {
                "IsDigit" => "tcs_char_is_digit",
                "IsLetter" => "tcs_char_is_letter",
                "IsLetterOrDigit" => "tcs_char_is_letter_or_digit",
                "IsWhiteSpace" => "tcs_char_is_space",
                "IsUpper" => "tcs_char_is_upper",
                "IsLower" => "tcs_char_is_lower",
                "ToUpper" => "tcs_char_to_upper",
                "ToLower" => "tcs_char_to_lower",
                var other => throw new Tcs2cException($"unsupported Char member: {other}"),
            };
            return $"{fn}({RenderExpr(args[0])})";
        }
        if (call.Callee.StartsWith("List.", StringComparison.Ordinal))
            return RenderListOp(call);
        switch (call.Callee)
        {
            case "__tcs_trunc":
                return TypeOf(args[0]).Kind == CTypeKind.I32
                    ? RenderExpr(args[0])
                    : $"tcs_trunc_f32({RenderExpr(args[0])})";
            case "tonumber":
                return $"tcs_parse_f32_or_fault({RenderExpr(args[0])})";
            case "math.tointeger":
                return $"tcs_parse_i32_or_fault({RenderExpr(((IlCall)args[0]).Args[0])})";
            case "math.fmod":
                return RenderOrderedCall("fmodf", type,
                    [(CType.F32, RenderExpr(args[0])), (CType.F32, RenderExpr(args[1]))]);
            case "os.getenv":
                return $"tcs_getenv({RenderExpr(args[0])})";
            case "string.sub":
                return args.Length == 3
                    ? RenderOrderedCall("tcs_string_sub", type,
                        [(CType.String, RenderExpr(args[0])),
                         (CType.I32, RenderExpr(args[1])), (CType.I32, RenderExpr(args[2]))])
                    : RenderOrderedCall("tcs_string_sub_open", type,
                        [(CType.String, RenderExpr(args[0])), (CType.I32, RenderExpr(args[1]))]);
            case "string.byte":
                return RenderOrderedCall("tcs_string_byte", type,
                    [(CType.String, RenderExpr(args[0])),
                     (CType.I32, args.Length == 2 ? RenderExpr(args[1]) : "INT32_C(1)")]);
            case "string.char":
                return $"tcs_string_from_byte({RenderExpr(args[0])})";
            case "string.upper":
                return $"tcs_string_map_case({RenderExpr(args[0])}, 1)";
            case "string.lower":
                return $"tcs_string_map_case({RenderExpr(args[0])}, 0)";
            case "string.format":
                return RenderFormat(call);
            case "table.remove":
            {
                var list = Temp("list");
                var at = Temp("index");
                var removed = Temp("removed");
                var elem = type.CName;
                return $"({{ TcsList *{list} = {RenderExpr(args[0])}; " +
                    $"int32_t {at} = ({RenderExpr(args[1])}) - INT32_C(1); " +
                    $"{elem} {removed} = *({elem} *)tcs_list_at({list}, {at}); " +
                    $"tcs_list_remove_at({list}, {at}); {removed}; }})";
            }
            case "Dict.Keys":
                return $"tcs_dict_keys({RenderExpr(args[0])})";
            case "Dict.Values":
                return $"tcs_dict_values({RenderExpr(args[0])})";
        }
        if (call.Callee.StartsWith("String.", StringComparison.Ordinal))
            return RenderStringOp(call, type);
        if (call.Callee.StartsWith("Math.", StringComparison.Ordinal))
            return RenderMathOp(call, type);
        throw new Tcs2cException($"unsupported intrinsic: {call.Callee}");
    }

    private CType RequireList(IlExpr expr)
    {
        var type = TypeOf(expr);
        if (type.Kind != CTypeKind.List || type.Element is null)
            throw new Tcs2cException($"receiver is not a typed List: {type}");
        return type;
    }

    // ---- string.format ----
    private static string FormatArgKind(CType type) => type.Kind switch
    {
        CTypeKind.I32 => "TCS_FMT_I32",
        CTypeKind.F32 => "TCS_FMT_F32",
        CTypeKind.Bool => "TCS_FMT_BOOL",
        CTypeKind.String => "TCS_FMT_STR",
        _ => throw new Tcs2cException($"string.format does not support {type}"),
    };

    private string RenderFormat(IlCall call)
    {
        var format = Temp("format");
        var sb = new StringBuilder($"TcsString *{format} = {RenderExpr(call.Args[0])}; ");
        var entries = new List<string>();
        for (var i = 1; i < call.Args.Length; i++)
        {
            var argType = TypeOf(call.Args[i]);
            var temp = Temp("fmt_arg");
            sb.Append($"{argType.CName} {temp} = {RenderExpr(call.Args[i])}; ");
            var member = argType.Kind switch
            {
                CTypeKind.I32 => "i", CTypeKind.F32 => "f",
                CTypeKind.Bool => "b", _ => "s",
            };
            entries.Add($"{{ {FormatArgKind(argType)}, .{member} = {temp} }}");
        }
        if (entries.Count == 0)
            return $"({{ {sb}tcs_string_format({format}, 0, NULL); }})";
        var array = Temp("fmt_args");
        sb.Append($"TcsFormatArg {array}[] = {{ {string.Join(", ", entries)} }}; ");
        return $"({{ {sb}tcs_string_format({format}, {entries.Count}, {array}); }})";
    }

    // ---- String.* (TinySystem.String) ----
    private CType TypeOfStringOp(IlCall call)
    {
        var name = call.Callee["String.".Length..];
        var args = call.Args;
        void Str(int i, string where) => RequireType(CType.String, TypeOf(args[i]), where);
        switch (name)
        {
            case "Contains" or "StartsWith" or "EndsWith":
                RequireArity(call.Callee, args.Length, 2);
                Str(0, name); Str(1, name);
                return CType.Bool;
            case "IndexOf":
                if (args.Length is not (2 or 3))
                    throw new Tcs2cException("String.IndexOf: expected 2 or 3 arguments");
                Str(0, name); Str(1, name);
                if (args.Length == 3) RequireType(CType.I32, TypeOf(args[2]), "IndexOf start");
                return CType.I32;
            case "Replace":
                RequireArity(call.Callee, args.Length, 3);
                Str(0, name); Str(1, name); Str(2, name);
                return CType.String;
            case "Trim":
                RequireArity(call.Callee, args.Length, 1);
                Str(0, name);
                return CType.String;
            case "Substring":
                if (args.Length is not (2 or 3))
                    throw new Tcs2cException("String.Substring: expected 2 or 3 arguments");
                Str(0, name);
                for (var i = 1; i < args.Length; i++)
                    RequireType(CType.I32, TypeOf(args[i]), "Substring index");
                return CType.String;
            case "IsNullOrEmpty":
                RequireArity(call.Callee, args.Length, 1);
                if (TypeOf(args[0]).Kind is not (CTypeKind.String or CTypeKind.Null))
                    throw new Tcs2cException("IsNullOrEmpty argument is not a string");
                return CType.Bool;
            case "Split":
                if (args.Length is not (1 or 2))
                    throw new Tcs2cException("String.Split: expected 1 or 2 arguments");
                Str(0, name);
                if (args.Length == 2) Str(1, "Split separator");
                return CType.Array(CType.String);
            case "Join":
            {
                if (args.Length < 2) throw new Tcs2cException("String.Join: expected 2+ arguments");
                Str(0, name);
                var values = TypeOf(args[1]);
                if (args.Length == 2 && values.Kind is CTypeKind.List or CTypeKind.Array)
                {
                    if (values.Element != CType.String)
                        throw new Tcs2cException("String.Join: values must be strings");
                    return CType.String;
                }
                for (var i = 1; i < args.Length; i++) Str(i, "Join value");
                return CType.String;
            }
            default:
                throw new Tcs2cException($"unsupported String member: {name}");
        }
    }

    private string RenderStringOp(IlCall call, CType type)
    {
        var name = call.Callee["String.".Length..];
        var args = call.Args;
        List<(CType, string)> Strings(params int[] indices) =>
            indices.Select(i => (CType.String, RenderExpr(args[i]))).ToList();
        switch (name)
        {
            case "Contains": return RenderOrderedCall("tcs_string_contains", type, Strings(0, 1));
            case "StartsWith": return RenderOrderedCall("tcs_string_starts_with", type, Strings(0, 1));
            case "EndsWith": return RenderOrderedCall("tcs_string_ends_with", type, Strings(0, 1));
            case "IndexOf":
                return RenderOrderedCall("tcs_string_index_of", type,
                    [.. Strings(0, 1), (CType.I32, args.Length == 3 ? RenderExpr(args[2]) : "INT32_C(0)")]);
            case "Replace": return RenderOrderedCall("tcs_string_replace", type, Strings(0, 1, 2));
            case "Trim": return $"tcs_string_trim({RenderExpr(args[0])})";
            case "Substring":
                return RenderOrderedCall("tcs_string_substring", type,
                    [.. Strings(0), (CType.I32, RenderExpr(args[1])),
                     (CType.I32, args.Length == 3 ? RenderExpr(args[2]) : "INT32_C(-1)")]);
            case "IsNullOrEmpty": return $"tcs_string_is_null_or_empty({RenderExpr(args[0])})";
            case "Split":
                return args.Length == 2
                    ? RenderOrderedCall("tcs_string_split", type,
                        [.. Strings(0, 1), (CType.I32, "1")])
                    : $"tcs_string_split({RenderExpr(args[0])}, NULL, 0)";
            case "Join":
            {
                var values = TypeOf(args[1]);
                if (args.Length == 2 && values.Kind == CTypeKind.List)
                    return RenderOrderedCall("tcs_string_join_list", type,
                        [.. Strings(0), (values, RenderExpr(args[1]))]);
                if (args.Length == 2 && values.Kind == CTypeKind.Array)
                    return RenderOrderedCall("tcs_string_join_array", type,
                        [.. Strings(0), (values, RenderExpr(args[1]))]);
                var sep = Temp("sep");
                var items = Temp("join_items");
                var sb = new StringBuilder($"TcsString *{sep} = {RenderExpr(args[0])}; ");
                var names = new List<string>();
                for (var i = 1; i < args.Length; i++)
                {
                    var temp = Temp("join_item");
                    sb.Append($"TcsString *{temp} = {RenderExpr(args[i])}; ");
                    names.Add(temp);
                }
                sb.Append($"TcsString *{items}[] = {{ {string.Join(", ", names)} }}; ");
                return $"({{ {sb}tcs_string_join_items({sep}, {items}, {names.Count}); }})";
            }
            default:
                throw new Tcs2cException($"unsupported String member: {name}");
        }
    }

    // ---- Random (TinySystem.Random: instance + Shared。Lua 5.5 の
    // math.random と同じ xoshiro256** を runtime の TcsRandom が持つ) ----
    private bool IsRandomTypeName(string name) =>
        name == "TinySystem.Random"
        || (name == "Random" && !_classes.ContainsKey("Random"));

    // 静的形 IlCall("Random.X"): Seed は Shared の再 seed、他は Shared 経由
    private CType TypeOfRandomOp(IlCall call)
    {
        var name = call.Callee["Random.".Length..];
        if (name != "Seed") return TypeOfRandomMethod(name, call.Args);
        RequireArity(call.Callee, call.Args.Length, 1);
        RequireType(CType.I32, TypeOf(call.Args[0]), "Random.Seed");
        return CType.Void;
    }

    private string RenderRandomOp(IlCall call, CType type)
    {
        var name = call.Callee["Random.".Length..];
        if (name != "Seed")
            return RenderRandomMethod("tcs_random_shared()", name, call.Args);
        _ = type;
        return $"tcs_rand_seed(tcs_random_shared(), (uint32_t)({RenderExpr(call.Args[0])}), 0)";
    }

    private CType TypeOfRandomNew(IlNewObj creation)
    {
        if (creation.Args.Length > 1)
            throw new Tcs2cException("Random: expected 0 or 1 constructor argument");
        if (creation.Args.Length == 1)
            RequireType(CType.I32, TypeOf(creation.Args[0]), "Random(seed)");
        return CType.Random;
    }

    private string RenderRandomNew(IlNewObj creation)
    {
        _ = TypeOfRandomNew(creation);
        return creation.Args.Length == 0
            ? "tcs_random_new_auto()"
            : $"tcs_random_new((uint32_t)({RenderExpr(creation.Args[0])}))";
    }

    private CType TypeOfRandomMethod(string name, IReadOnlyList<IlExpr> args)
    {
        switch (name)
        {
            case "NextFloat" or "NextSingle":
                RequireArity($"Random.{name}", args.Count, 0);
                return CType.F32;
            case "Next":
                if (args.Count > 2) throw new Tcs2cException("Random.Next: expected 0-2 arguments");
                foreach (var a in args) RequireType(CType.I32, TypeOf(a), "Random.Next");
                return CType.I32;
            case "Range":
                RequireArity("Random.Range", args.Count, 2);
                RequireType(CType.I32, TypeOf(args[0]), "Random.Range");
                RequireType(CType.I32, TypeOf(args[1]), "Random.Range");
                return CType.I32;
            default:
                throw new Tcs2cException($"unsupported Random member: {name}");
        }
    }

    // receiver は評価順を保つため先に temp へ束縛する
    private string RenderRandomMethod(string receiver, string name,
        IReadOnlyList<IlExpr> args)
    {
        var type = TypeOfRandomMethod(name, args);
        var r = Temp("rng");
        var head = $"TcsRandom *{r} = {receiver}; ";
        switch (name)
        {
            case "NextFloat" or "NextSingle":
                return $"({{ {head}tcs_rand_float({r}); }})";
            case "Next":
                // Lua facade: Next() = random(0, 2147483646)、Next(max) =
                // random(0, max - 1)、Next(min, max) = random(min, max - 1)
                if (args.Count == 0)
                    return $"({{ {head}tcs_rand_range({r}, 0, INT32_C(2147483646)); }})";
                if (args.Count == 1)
                    return $"({{ {head}" + RenderOrderedCall("tcs_rand_range", type,
                        [(CType.Random, r), (CType.I32, "0"),
                         (CType.I32, $"({RenderExpr(args[0])}) - INT32_C(1)")]) + "; })";
                return $"({{ {head}" + RenderOrderedCall("tcs_rand_range", type,
                    [(CType.Random, r), (CType.I32, RenderExpr(args[0])),
                     (CType.I32, $"({RenderExpr(args[1])}) - INT32_C(1)")]) + "; })";
            case "Range":
                return $"({{ {head}" + RenderOrderedCall("tcs_rand_range", type,
                    [(CType.Random, r), (CType.I32, RenderExpr(args[0])),
                     (CType.I32, RenderExpr(args[1]))]) + "; })";
            default:
                throw new Tcs2cException($"unsupported Random member: {name}");
        }
    }

    // ---- Math.* (TinySystem.Math) ----
    private CType TypeOfMathOp(IlCall call)
    {
        var name = call.Callee["Math.".Length..];
        var args = call.Args;
        CType Num(int i) => TypeOf(args[i]).Kind is CTypeKind.I32 or CTypeKind.F32
            ? TypeOf(args[i])
            : throw new Tcs2cException($"Math.{name}: argument {i} is not numeric");
        switch (name)
        {
            case "Min" or "Max":
                RequireArity(call.Callee, args.Length, 2);
                return NumericJoin(Num(0), Num(1), name);
            case "Clamp":
                RequireArity(call.Callee, args.Length, 3);
                return NumericJoin(NumericJoin(Num(0), Num(1), name), Num(2), name);
            case "Abs":
                RequireArity(call.Callee, args.Length, 1);
                return Num(0);
            case "Sign":
                RequireArity(call.Callee, args.Length, 1);
                _ = Num(0);
                return CType.I32;
            case "Floor" or "Ceil" or "Sqrt" or "Sin" or "Cos" or "Tan" or "Exp":
                RequireArity(call.Callee, args.Length, 1);
                _ = Num(0);
                return CType.F32;
            case "Atan2" or "Pow":
                RequireArity(call.Callee, args.Length, 2);
                _ = Num(0); _ = Num(1);
                return CType.F32;
            case "Log":
                if (args.Length is not (1 or 2)) throw new Tcs2cException("Math.Log: expected 1 or 2 arguments");
                for (var i = 0; i < args.Length; i++) _ = Num(i);
                return CType.F32;
            case "Round":
                if (args.Length is not (1 or 2)) throw new Tcs2cException("Math.Round: expected 1 or 2 arguments");
                _ = Num(0);
                if (args.Length == 2) RequireType(CType.I32, TypeOf(args[1]), "Round digits");
                return CType.F32;
            default:
                throw new Tcs2cException($"unsupported Math member: {name}");
        }
    }

    private string RenderMathOp(IlCall call, CType type)
    {
        var name = call.Callee["Math.".Length..];
        var args = call.Args;
        (CType, string) F(int i) => (CType.F32, RenderExpr(args[i]));
        switch (name)
        {
            case "Min":
            {
                var a = Temp("min_a"); var b = Temp("min_b");
                return $"({{ {type.CName} {a} = {RenderExpr(args[0])}; {type.CName} {b} = " +
                    $"{RenderExpr(args[1])}; {b} < {a} ? {b} : {a}; }})";
            }
            case "Max":
            {
                var a = Temp("max_a"); var b = Temp("max_b");
                return $"({{ {type.CName} {a} = {RenderExpr(args[0])}; {type.CName} {b} = " +
                    $"{RenderExpr(args[1])}; {b} > {a} ? {b} : {a}; }})";
            }
            case "Clamp":
            {
                var v = Temp("clamp_v"); var lo = Temp("clamp_lo"); var hi = Temp("clamp_hi");
                return $"({{ {type.CName} {v} = {RenderExpr(args[0])}; {type.CName} {lo} = " +
                    $"{RenderExpr(args[1])}; {type.CName} {hi} = {RenderExpr(args[2])}; " +
                    $"{v} < {lo} ? {lo} : {v} > {hi} ? {hi} : {v}; }})";
            }
            case "Abs":
                return type.Kind == CTypeKind.I32
                    ? $"({{ int32_t __a = {RenderExpr(args[0])}; __a < 0 ? -__a : __a; }})"
                    : $"fabsf({RenderExpr(args[0])})";
            case "Sign":
            {
                var v = Temp("sign");
                var vt = TypeOf(args[0]);
                return $"({{ {vt.CName} {v} = {RenderExpr(args[0])}; " +
                    $"{v} > 0 ? INT32_C(1) : {v} < 0 ? -INT32_C(1) : INT32_C(0); }})";
            }
            case "Floor": return $"floorf({RenderExpr(args[0])})";
            case "Ceil": return $"ceilf({RenderExpr(args[0])})";
            case "Sqrt": return $"sqrtf({RenderExpr(args[0])})";
            case "Sin": return $"TCS_MATH(sinf)({RenderExpr(args[0])})";
            case "Cos": return $"TCS_MATH(cosf)({RenderExpr(args[0])})";
            case "Tan": return $"TCS_MATH(tanf)({RenderExpr(args[0])})";
            case "Exp": return $"TCS_MATH(expf)({RenderExpr(args[0])})";
            case "Atan2": return RenderOrderedCall("TCS_MATH(atan2f)", type, [F(0), F(1)]);
            case "Pow": return RenderOrderedCall("tcs_math_pow", type, [F(0), F(1)]);
            case "Log":
                return args.Length == 2
                    ? RenderOrderedCall("tcs_math_log", type, [F(0), F(1), (CType.I32, "1")])
                    : $"tcs_math_log({RenderExpr(args[0])}, 0.0f, 0)";
            case "Round":
                return args.Length == 2
                    ? RenderOrderedCall("tcs_math_round", type,
                        [F(0), (CType.I32, RenderExpr(args[1])), (CType.I32, "1")])
                    : $"tcs_math_round({RenderExpr(args[0])}, 0, 0)";
            default:
                throw new Tcs2cException($"unsupported Math member: {name}");
        }
    }
}

using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// struct / record struct の member (il-spec §10): instance method と
// accessor は `self` を格納場所へのポインタ (Tcs_S *) で受ける自由関数、
// explicit / positional ctor は zero 値から組み立てて値で返す関数、static method と
// operator は self の無い自由関数。
// 呼び出しサイトは IlCall("S.M", [receiver, args]) の静的ディスパッチで、
// receiver が place (変数 / field / 配列要素) ならそのアドレスを渡し (変異が
// その場に残る = C# の変数 receiver)、rvalue (copy / 呼び出し結果) なら
// 一時値のアドレスを渡す (変異は捨てられる = C# の rvalue receiver)。
internal sealed partial class CEmitter
{
    // struct ctor 本文の `return;` が返す値 (ctor 以外では null)
    private string? _ctorReturnValue;

    private IEnumerable<IlStructInfo> StructInfos =>
        _program.Structs.IsDefault ? [] : _program.Structs;

    // static field を持つ型。struct の static は class の static と同じ置き場と初期化の規則に乗る
    private IEnumerable<IlClassInfo> StaticOwners =>
        _program.Classes.Concat(StructStaticOwners.Values);

    private Dictionary<string, IlClassInfo>? _structStaticOwners;

    private Dictionary<string, IlClassInfo> StructStaticOwners =>
        _structStaticOwners ??= StructInfos
            .Where(st => !st.StaticFields.IsDefaultOrEmpty)
            .ToDictionary(st => st.Name,
                st => new IlClassInfo(st.Name, null, st.StaticFields, st.LayoutHash, []));

    private bool IsStaticMemberOwner(string name) =>
        _classes.ContainsKey(name) || _facts.Structs.ContainsKey(name);

    private bool TryStructCallee(string callee, out IlStructInfo st,
        out string member)
    {
        st = null!;
        member = "";
        var dot = callee.IndexOf('.');
        if (dot <= 0 || dot != callee.LastIndexOf('.') || dot == callee.Length - 1)
            return false;
        if (!_facts.Structs.TryGetValue(callee[..dot], out var found))
            return false;
        st = found;
        member = callee[(dot + 1)..];
        return true;
    }

    private void ValidateStructs()
    {
        foreach (var st in StructInfos)
        {
            Names.Id(st.Name);
            foreach (var field in st.Fields)
            {
                Names.Id(field.Name);
                EnsureSupportedStorageType(_facts.MapType(field.Type),
                    $"field {st.Name}.{field.Name}");
            }
            foreach (var field in st.StaticFields.IsDefault ? [] : st.StaticFields)
            {
                Names.Id(field.Name);
                EnsureSupportedStorageType(_facts.MapType(field.Type),
                    $"static field {st.Name}.{field.Name}");
            }
            foreach (var method in st.Methods.IsDefault ? [] : st.Methods)
            {
                Names.Id(method.Name);
                if (method.Body is null)
                    throw new Tcs2cException(
                        $"method has no IL body: {st.Name}.{method.Name}");
                var fact = _facts.Method(st.Name, method.Name);
                EnsureSupportedStorageType(fact.ReturnType,
                    $"return type of {st.Name}.{method.Name}", allowVoid: true);
                foreach (var parameter in fact.Parameters)
                    EnsureSupportedStorageType(parameter.Type,
                        $"parameter {st.Name}.{method.Name}.{parameter.Name}");
            }
            if (StructCtors(st).Any(c => c.Body is null))
                throw new Tcs2cException(
                    $"constructor body is not IL-exportable: {st.Name}");
        }
    }

    private static IEnumerable<IlCtorInfo> StructCtors(IlStructInfo st) =>
        st.Ctors.IsDefault ? [] : st.Ctors;

    private static string StructCtorFunction(IlStructInfo st, IlCtorInfo ctor) =>
        Names.StructCtor(ctor.Name == "ctor" ? st.Name : $"{st.Name}_{ctor.Name}");

    private List<ParameterFact> StructCtorParamFacts(IlStructInfo st, IlCtorInfo ctor)
    {
        if (ctor.Parameters.Length != ctor.ParameterTypes.Length)
            throw new Tcs2cException($"ctor metadata mismatch: {st.Name}");
        return ctor.Parameters.Select((name, i) => new ParameterFact(
            name, _facts.MapType(ctor.ParameterTypes[i]))).ToList();
    }

    private void EmitStructMemberPrototypes()
    {
        foreach (var st in StructInfos)
        {
            foreach (var ctor in StructCtors(st))
            {
                var parameters = StructCtorParamFacts(st, ctor);
                Line($"static {CType.Struct(st.Name).CName} " +
                    $"{StructCtorFunction(st, ctor)}(" +
                    (parameters.Count == 0 ? "void"
                        : string.Join(", ", parameters.Select(p => p.Type.CName))) +
                    ");");
            }
            foreach (var method in st.Methods.IsDefault ? [] : st.Methods)
            {
                var fact = _facts.Method(st.Name, method.Name);
                Line($"static {fact.ReturnType.CName} " +
                    $"{Names.Method(st.Name, method.Name)}({ParameterList(fact)});");
            }
        }
    }

    private void EmitStructMembers()
    {
        foreach (var st in StructInfos)
        {
            foreach (var ctor in StructCtors(st)) EmitStructCtor(st, ctor);
            foreach (var method in st.Methods.IsDefault ? [] : st.Methods)
            {
                var fact = _facts.Method(st.Name, method.Name);
                _structSelfOwner = !fact.IsStatic;
                EmitMethodCore(method, fact, fact.IsStatic ? null
                    : new Variable("v_self", CType.Struct(st.Name)) { Boxed = true });
                _structSelfOwner = false;
            }
        }
    }

    // zero 値 → field initializer → 本文 (Lua の S.ctor と同順)。self は
    // local の値を指すので本文の field 書きはそのまま結果に残る
    private void EmitStructCtor(IlStructInfo st, IlCtorInfo ctor)
    {
        var type = CType.Struct(st.Name);
        var parameters = StructCtorParamFacts(st, ctor);
        _currentMethodFact = new MethodFact(st.Name, ctor.Name, true, type,
            [.. parameters], new IlMethodInfo(ctor.Name, true,
                ctor.Parameters, ctor.Body, st.Name, ctor.ParameterTypes));
        _currentMethod = _currentMethodFact.Metadata;
        _scopes.Clear();
        _continueTargets.Clear();
        _breakTargets.Clear();
        CollectCapturedNames(ctor.Body!);
        PushScope();
        for (var i = 0; i < parameters.Count; i++)
            AddVariable(parameters[i].Name,
                new Variable($"v_{Names.Id(parameters[i].Name)}_{i}",
                    parameters[i].Type));
        AddVariable("self", new Variable("v_self", type) { Boxed = true });
        Line($"static {type.CName}");
        Line($"{StructCtorFunction(st, ctor)}(" +
            (parameters.Count == 0 ? "void"
                : string.Join(", ", parameters.Select((p, i) =>
                    $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}"))) + ")");
        Line("{");
        _indent++;
        Line($"{type.CName} self_value = {ZeroInit(type)};");
        Line($"{type.CName} *v_self = &self_value;");
        Line("void *v_owner = NULL; (void)v_owner;");
        _structSelfOwner = true;
        BoxCapturedParameters();
        foreach (var field in st.Fields)
        {
            if (field.Init is null) continue;
            var fieldType = _facts.MapType(field.Type);
            CheckAssignable(fieldType, field.Init,
                $"initializer of {st.Name}.{field.Name}");
            Line($"(*v_self).{Names.Field(field.Name)} = " +
                $"{RenderCoerced(field.Init, fieldType)};");
        }
        _ctorReturnValue = "(*v_self)";
        EmitStats(ctor.Body!.Stats);
        _ctorReturnValue = null;
        _structSelfOwner = false;
        Line("return (*v_self);");
        _indent--;
        Line("}");
        Line();
        PopScope();
        FlushPendingClosures();
    }

    private CType TypeOfStructCall(IlStructInfo st, string member,
        IReadOnlyList<IlExpr> args)
    {
        var type = CType.Struct(st.Name);
        if (StructCtors(st).FirstOrDefault(c => c.Name == member) is { } ctor)
        {
            var parameters = StructCtorParamFacts(st, ctor);
            RequireArity($"{st.Name}.{member}", args.Count, parameters.Count);
            for (var i = 0; i < args.Count; i++)
                CheckAssignable(parameters[i].Type, args[i],
                    $"argument {i} of {st.Name}.{member}");
            return type;
        }
        switch (member)
        {
            case "ctor":
                throw new Tcs2cException($"struct has no constructor: {st.Name}");
            case "op_Equality":
                RequireArity($"{st.Name}.op_Equality", args.Count, 2);
                CheckAssignable(type, args[0], $"{st.Name}.op_Equality left");
                CheckAssignable(type, args[1], $"{st.Name}.op_Equality right");
                return CType.Bool;
            default:
            {
                var fact = _facts.Method(st.Name, member);
                if (fact.IsStatic) return ValidateMethodCall(fact, null, args);
                if (args.Count != fact.Parameters.Count + 1)
                    throw new Tcs2cException(
                        $"struct method call is missing its receiver: {st.Name}.{member}");
                CheckAssignable(type, args[0], $"receiver of {st.Name}.{member}");
                return ValidateMethodCall(fact, type, args.Skip(1).ToArray());
            }
        }
    }

    private string RenderStructCall(IlStructInfo st, string member,
        IReadOnlyList<IlExpr> args)
    {
        var type = CType.Struct(st.Name);
        if (StructCtors(st).FirstOrDefault(c => c.Name == member) is { } ctor)
        {
            var parameters = StructCtorParamFacts(st, ctor);
            return RenderOrderedCall(StructCtorFunction(st, ctor), type,
                [.. args.Select((a, i) =>
                    (parameters[i].Type, RenderCoerced(a, parameters[i].Type)))]);
        }
        switch (member)
        {
            case "op_Equality":
            {
                var left = Temp("eq_l");
                var right = Temp("eq_r");
                return $"({{ {type.CName} {left} = {RenderExpr(args[0])}; " +
                    $"{type.CName} {right} = {RenderExpr(args[1])}; " +
                    $"{Names.StructEq(st.Name)}(&{left}, &{right}); }})";
            }
            default:
            {
                var fact = _facts.Method(st.Name, member);
                if (fact.IsStatic) return RenderMethodCall(fact, null, args);
                var outArguments = TakeOutArguments();
                var sb = new StringBuilder();
                var receiver = Temp("recv");
                var owner = Temp("owner");
                if (IsStructPlace(args[0]))
                {
                    var (place, placeOwner) = RenderOwnedStructPlace(args[0], sb);
                    sb.Append($"{type.CName} *{receiver} = &{place}; ");
                    sb.Append($"void *{owner} = {placeOwner ?? "NULL"}; ");
                }
                else
                {
                    sb.Append($"{type.CName} {receiver}_value = {RenderExpr(args[0])}; ");
                    sb.Append($"{type.CName} *{receiver} = &{receiver}_value; ");
                    sb.Append($"void *{owner} = NULL; ");
                }
                var rendered = new List<string> { receiver, owner };
                for (var i = 0; i < fact.Parameters.Count; i++)
                {
                    var temp = Temp("arg");
                    sb.Append($"{fact.Parameters[i].Type.CName} {temp} = " +
                        $"{RenderCoerced(args[i + 1], fact.Parameters[i].Type)}; ");
                    rendered.Add(temp);
                }
                rendered.AddRange(outArguments);
                sb.Append($"{Names.Method(st.Name, member)}({string.Join(", ", rendered)}); ");
                return $"({{ {sb}}})";
            }
        }
    }

    // C# の「変数」receiver (il-spec §10): local / parameter / field / 配列要素
    private bool IsStructPlace(IlExpr expr) => expr switch
    {
        IlVar v => TryResolve(v.Name) is not null,
        IlField f => TryStaticField(f, out _, out _) || TypeOf(f.Recv).Kind switch
        {
            // rvalue (property / 呼び出し結果) の field は copy の一部で place ではない
            CTypeKind.StructVal => IsStructPlace(f.Recv),
            CTypeKind.Ref => true,
            _ => false,
        },
        IlIndex index => index.PlusOne,
        _ => false,
    };

    private CType TypeOfStructWith(IlWith with, CType source)
    {
        foreach (var (name, value) in with.Overrides)
            CheckAssignable(_facts.StructField(source.Name!, name), value,
                $"with {name}");
        return source;
    }

    // 値 copy の上に override を書く (record struct の with)
    private string RenderStructWith(IlWith with, CType type)
    {
        var copy = Temp("with_copy");
        var sb = new StringBuilder();
        sb.Append($"{type.CName} {copy} = {RenderExpr(with.Src)}; ");
        foreach (var (name, value) in with.Overrides)
        {
            var fieldType = _facts.StructField(type.Name!, name);
            sb.Append($"{copy}.{Names.Field(name)} = {RenderCoerced(value, fieldType)}; ");
        }
        return $"({{ {sb}{copy}; }})";
    }
}

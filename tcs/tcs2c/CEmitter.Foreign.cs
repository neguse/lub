using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// foreign (--ref の stub 宣言): host が C で実装する static method / static
// field / enum 定数と、host と共有するデータ class (IsExternal)。IL には
// 署名だけが載り (IlExport.Foreign)、C は `extern` の tcs_host_<path> を宣言
// して呼ぶ。out parameter は pointer 渡し、nullable スカラは値 (TcsOpt*)、
// 参照型は tcs の object pointer をそのまま渡す (host は呼び出し中だけ借りる。
// 境界を跨いで持つなら tcs_lib_hold)。
internal sealed partial class CEmitter
{
    private static string HostName(string name) => "tcs_host_" +
        string.Join("_", name.Split('.').Select(Names.Id));

    private IlForeignMethod? ForeignMethod(string name) => _program.ForeignMethods.IsDefault
        ? null : _program.ForeignMethods.FirstOrDefault(m => m.Name == name);

    // 受け手の class chain を下から辿り、最寄りの宣言が stub のものなら host 関数。
    // 途中の user class が同名を宣言していれば通常の method 呼び出しに任せる
    private IlForeignMethod? ForeignInstanceMethod(CType receiver, string method)
    {
        if (receiver.Kind != CTypeKind.Ref || _program.ForeignMethods.IsDefault) return null;
        for (string? cur = receiver.Name; cur != null; cur = _classes[cur].BaseName)
        {
            if (_classes[cur].Methods.Any(m => m.Name == method)) return null;
            if (_program.ForeignMethods.FirstOrDefault(m =>
                    m.Receiver == cur && m.Name.EndsWith("." + method)) is { } found)
                return found;
        }
        return null;
    }

    private static string ShortName(IlForeignMethod method) =>
        method.Name[(method.Name.LastIndexOf('.') + 1)..];

    // IlInvoke は実行時型で解決する (il-reference §9)。stub の instance method を
    // 同じシグネチャで再宣言する user subclass ごとに、type_id → 最寄り実装
    private List<(string Target, string Impl)> ForeignOverriders(IlForeignMethod method)
    {
        var result = new List<(string, string)>();
        if (method.Receiver is not { } owner) return result;
        var name = ShortName(method);
        var parameters = ForeignParameters(method).Select(p => p.Type);
        var returnType = _facts.MapType(method.ReturnType);
        foreach (var target in _program.Classes.Where(c => !c.IsInterface
            && c.Name != owner && IsAncestorOrSame(owner, c.Name)))
            for (string? cur = target.Name; cur != owner; cur = _classes[cur!].BaseName)
                if (_classes[cur!].Methods.Any(m => m.Name == name && !m.IsStatic)
                    && _facts.Method(cur!, name) is var fact
                    && fact.ReturnType == returnType
                    && fact.Parameters.Select(p => p.Type).SequenceEqual(parameters))
                {
                    result.Add((target.Name, cur!));
                    break;
                }
        return result;
    }

    private string ForeignDispatcherSignature(IlForeignMethod method) =>
        $"static {_facts.MapType(method.ReturnType).CName} " +
        $"{Names.Dispatch(method.Receiver!, ShortName(method))}(" +
        string.Join(", ", new[] { $"{CType.Ref(method.Receiver!).CName} v_self" }
            .Concat(ForeignParameters(method).Select((p, i) => $"{p.Type.CName} v_{i}"))) + ")";

    private void EmitForeignDispatchers()
    {
        if (_program.ForeignMethods.IsDefault) return;
        foreach (var method in _program.ForeignMethods)
        {
            var overriders = ForeignOverriders(method);
            if (overriders.Count == 0) continue;
            var args = Enumerable.Range(0, method.Parameters.Length).Select(i => $"v_{i}").ToList();
            var isVoid = _facts.MapType(method.ReturnType) == CType.Void;
            string Call(string callee, string self) =>
                $"{callee}({string.Join(", ", args.Prepend(self))})";
            string Return(string call) => isVoid ? $"{call}; return;" : $"return {call};";
            Line(ForeignDispatcherSignature(method));
            Line("{");
            _indent++;
            Line("switch (((TcsObjectHeader *)v_self)->type_id) {");
            foreach (var (target, impl) in overriders)
                Line($"case {Names.TypeId(target)}: " + Return(Call(
                    Names.Method(impl, ShortName(method)), $"({Names.Class(impl)} *)v_self")));
            Line("default: " + Return(Call(HostName(method.Name), "v_self")));
            Line("}");
            _indent--;
            Line("}");
            Line();
        }
    }

    private IlForeignValue? ForeignValue(IlField field) =>
        field.Recv is IlVar receiver && !_program.ForeignValues.IsDefault
            && TryResolve(receiver.Name) is null
            ? _program.ForeignValues.FirstOrDefault(v => v.Name == $"{receiver.Name}.{field.Name}")
            : null;

    private IlForeignMethod? ForeignCallee(IlExpr callee) =>
        callee is IlField { Recv: IlVar receiver } field && TryResolve(receiver.Name) is null
            ? ForeignMethod($"{receiver.Name}.{field.Name}") : null;

    private ParameterFact[] ForeignParameters(IlForeignMethod method) =>
        [.. method.Parameters.Select(p =>
            new ParameterFact(p.Name, _facts.MapType(p.Type), p.Default))];

    private void EmitForeignPrototypes()
    {
        if (!_program.ForeignMethods.IsDefault)
            foreach (var method in _program.ForeignMethods)
            {
                var parameters = method.Parameters.Select(p =>
                    _facts.MapType(p.Type).CName + (p.IsOut ? " *" : ""));
                if (method.Receiver is { } owner)
                    parameters = parameters.Prepend(CType.Ref(owner).CName);
                Line($"extern {_facts.MapType(method.ReturnType).CName} {HostName(method.Name)}(" +
                    (!parameters.Any() ? "void" : string.Join(", ", parameters)) + ");");
                if (ForeignOverriders(method).Count > 0)
                    Line(ForeignDispatcherSignature(method) + ";");
            }
        if (!_program.ForeignValues.IsDefault)
            foreach (var value in _program.ForeignValues.Where(v => v.Constant == null))
                Line($"extern {_facts.MapType(value.Type).CName} {HostName(value.Name)}(void);");
        Line();
    }

    private CType TypeOfForeignCall(IlForeignMethod method, IReadOnlyList<IlExpr> args)
    {
        if (method.Parameters.Any(p => p.IsOut))
            throw new Tcs2cException($"foreign out call requires assignment: {method.Name}");
        var parameters = ForeignParameters(method);
        args = CompleteArguments(parameters, args, method.Name);
        for (var i = 0; i < args.Count; i++)
            CheckAssignable(parameters[i].Type, args[i], $"argument {i} of {method.Name}");
        return _facts.MapType(method.ReturnType);
    }

    // IlCall("<class>.<method>", [self, args...]) の instance method は明示的 base
    // 呼び出し: 先頭引数が receiver で、dispatcher を通さず host 実装へ送る
    private (IlExpr? Receiver, IReadOnlyList<IlExpr> Args) SplitForeignCall(
        IlForeignMethod method, IReadOnlyList<IlExpr> args)
    {
        if (method.Receiver is not { } owner) return (null, args);
        if (args.Count == 0) throw new Tcs2cException($"{method.Name}: missing receiver");
        CheckAssignable(CType.Ref(owner), args[0], $"receiver of {method.Name}");
        return (args[0], args.Skip(1).ToList());
    }

    private CType TypeOfForeignIlCall(IlForeignMethod method, IReadOnlyList<IlExpr> args) =>
        TypeOfForeignCall(method, SplitForeignCall(method, args).Args);

    private string RenderForeignIlCall(IlForeignMethod method, IReadOnlyList<IlExpr> args)
    {
        var (receiver, rest) = SplitForeignCall(method, args);
        return RenderForeignCall(method, rest, receiver, dispatch: false);
    }

    private string RenderForeignCall(IlForeignMethod method, IReadOnlyList<IlExpr> args,
        IlExpr? receiver = null, bool dispatch = true)
    {
        var result = TypeOfForeignCall(method, args);
        var parameters = ForeignParameters(method);
        args = CompleteArguments(parameters, args, method.Name);
        var values = new List<(CType Type, string Value)>();
        if (receiver is not null)
        {
            var type = CType.Ref(method.Receiver!);
            values.Add((type, $"({type.CName})tcs_nonnull({RenderExpr(receiver)})"));
        }
        values.AddRange(args.Select((a, i) =>
            (parameters[i].Type, RenderCoerced(a, parameters[i].Type))));
        var callee = receiver is not null && dispatch && ForeignOverriders(method).Count > 0
            ? Names.Dispatch(method.Receiver!, ShortName(method)) : HostName(method.Name);
        return RenderOrderedCall(callee, result, values);
    }

    private (string Value, CType Type) RenderForeignValue(IlForeignValue value)
    {
        var type = _facts.MapType(value.Type);
        return value.Constant is { } constant
            ? (Constants.I32(constant), CType.I32)
            : ($"{HostName(value.Name)}()", type);
    }

    // object initializer (new Options { X = ... }) で作る外部 data class
    private string RenderObjectInitializer(IlTable table, CType type)
    {
        if (type.Kind != CTypeKind.Ref)
            throw new Tcs2cException($"object initializer of non-class type: {type}");
        var cls = _classes[type.Name!];
        if (cls.Ctor is not null)
            throw new Tcs2cException($"object initializer needs a parameterless class: {cls.Name}");
        var name = Temp("object");
        var text = new StringBuilder($"{type.CName} {name} = {Names.New(cls.Name)}(); ");
        foreach (var entry in table.Entries)
        {
            if (entry.NameKey == null)
                throw new Tcs2cException("object initializer entry needs a member name");
            var field = FieldInChain(cls.Name, entry.NameKey);
            CheckAssignable(field.Type, entry.Value, $"initializer of {cls.Name}.{entry.NameKey}");
            text.Append($"{name}->{Names.Field(entry.NameKey)} = " +
                $"{RenderCoerced(entry.Value, field.Type)}; ");
        }
        return $"({{ {text}{name}; }})";
    }

    // out parameter 付きの foreign 呼び出し: (a, b) = api.poll(args)
    private bool EmitForeignMultiAssign(IlMultiAssign multi)
    {
        var method = multi.Values switch
        {
            [IlCall call] => ForeignMethod(call.Callee),
            [IlDynCall dyn] => ForeignCallee(dyn.Callee),
            _ => null,
        };
        if (method is null) return false;
        var callArgs = multi.Values[0] switch
        {
            IlCall call => call.Args,
            IlDynCall dyn => dyn.Args,
            _ => throw new Tcs2cException("unreachable"),
        };
        if (_facts.MapType(method.ReturnType) != CType.Void)
            throw new Tcs2cException("foreign out calls with a return value are not supported");
        var inputs = method.Parameters.Where(p => !p.IsOut)
            .Select(p => new ParameterFact(p.Name, _facts.MapType(p.Type), p.Default)).ToArray();
        var args = CompleteArguments(inputs, callArgs, method.Name);
        RequireArity(method.Name, multi.Targets.Length, method.Parameters.Count(p => p.IsOut));
        var values = new List<string>();
        int input = 0, output = 0;
        foreach (var parameter in method.Parameters)
        {
            var type = _facts.MapType(parameter.Type);
            if (parameter.IsOut)
            {
                if (multi.Targets[output++] is not IlVar target)
                    throw new Tcs2cException("foreign out target must be a local");
                if (target.Name == "_" && TryResolve(target.Name) == null)
                {
                    var discard = Temp("discard");
                    Line($"{type.CName} {discard};");
                    values.Add($"&{discard}");
                    continue;
                }
                var variable = multi.Declare && TryResolve(target.Name) is null
                    ? DeclareLocal(target.Name, type) : Resolve(target.Name);
                RequireType(type, variable.Type, method.Name);
                values.Add(variable.Boxed ? variable.CName : $"&{variable.CName}");
                if (variable.Boxed && NeedsBarrier(type)) Line($"tcs_wb({variable.CName});");
            }
            else
            {
                var value = Temp("host_arg");
                CheckAssignable(type, args[input], $"argument {input} of {method.Name}");
                Line($"{type.CName} {value} = {RenderCoerced(args[input++], type)};");
                values.Add(value);
            }
        }
        Line($"{HostName(method.Name)}({string.Join(", ", values)});");
        return true;
    }
}

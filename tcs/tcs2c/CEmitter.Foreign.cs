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
                Line($"extern {_facts.MapType(method.ReturnType).CName} {HostName(method.Name)}(" +
                    (method.Parameters.Length == 0 ? "void" : string.Join(", ", parameters)) + ");");
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

    private string RenderForeignCall(IlForeignMethod method, IReadOnlyList<IlExpr> args)
    {
        var result = TypeOfForeignCall(method, args);
        var parameters = ForeignParameters(method);
        args = CompleteArguments(parameters, args, method.Name);
        return RenderOrderedCall(HostName(method.Name), result,
            [.. args.Select((a, i) => (parameters[i].Type, RenderCoerced(a, parameters[i].Type)))]);
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

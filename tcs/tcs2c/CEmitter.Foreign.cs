using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private static string HostName(string name) => "tcs_host_" +
        string.Join("_", name.Split('.').Select(Names.Id));

    private IlForeignMethod? ForeignMethod(string name) => _program.ForeignMethods.IsDefault
        ? null : _program.ForeignMethods.FirstOrDefault(m => m.Name == name);

    private IlForeignValue? ForeignValue(IlField field) =>
        field.Recv is IlVar receiver && !_program.ForeignValues.IsDefault
            ? _program.ForeignValues.FirstOrDefault(v => v.Name == $"{receiver.Name}.{field.Name}") : null;

    private static string? ForeignCallee(IlExpr callee) =>
        callee is IlField { Recv: IlVar receiver } field ? $"{receiver.Name}.{field.Name}" : null;

    private void EmitForeignPrototypes()
    {
        if (!_program.ForeignMethods.IsDefault)
            foreach (var method in _program.ForeignMethods)
            {
                var parameters = method.Parameters.Select(p => _facts.MapType(p.Type).CName + (p.IsOut ? " *" : ""));
                Line($"extern {_facts.MapType(method.ReturnType).CName} {HostName(method.Name)}(" +
                    (method.Parameters.Length == 0 ? "void" : string.Join(", ", parameters)) + ");");
            }
        if (!_program.ForeignValues.IsDefault)
            foreach (var value in _program.ForeignValues.Where(v => v.Constant == null))
                Line($"extern {_facts.MapType(value.Type).CName} {HostName(value.Name)}(void);");
    }

    private string RenderForeignCall(IlForeignMethod method, IReadOnlyList<IlExpr> args)
    {
        if (method.Parameters.Any(p => p.IsOut))
            throw new Tcs2cException($"foreign out call requires assignment: {method.Name}");
        var parameters = method.Parameters.Select(p => new ParameterFact(p.Name, _facts.MapType(p.Type), p.Default)).ToArray();
        args = CompleteArguments(parameters, args);
        for (var i = 0; i < args.Count; i++) ValidateArgument(parameters[i].Type, args[i], method.Name);
        return RenderOrderedCall(HostName(method.Name), _facts.MapType(method.ReturnType),
            args.Select((a, i) => (parameters[i].Type, RenderCoerced(a, parameters[i].Type))).ToArray());
    }

    private string RenderForeignTable(IlTable table)
    {
        var type = _facts.MapType(table.ObjectType!);
        var cls = _classes[type.Name!];
        var name = Temp("options");
        var text = new StringBuilder($"{type.CName} {name} = {Names.New(cls.Name)}(" +
            $"sizeof({Names.Class(cls.Name)}), tcs_trace_object_{Names.Id(cls.Name)}); ");
        foreach (var entry in table.Entries)
        {
            if (entry.NameKey == null) throw new Tcs2cException("foreign option field needs a name");
            var field = _facts.Field(cls.Name, entry.NameKey);
            ValidateArgument(field.Type, entry.Value, entry.NameKey);
            text.Append($"{name}->{Names.Field(entry.NameKey)} = {RenderCoerced(entry.Value, field.Type)}; ");
        }
        return $"({{ {text}{name}; }})";
    }

    private bool EmitForeignMultiAssign(IlMultiAssign multi)
    {
        if (multi.Values is not [IlCall call] || ForeignMethod(call.Callee) is not { } method) return false;
        if (_facts.MapType(method.ReturnType) != CType.Void)
            throw new Tcs2cException("foreign out calls with a return value are not supported");
        var inputs = method.Parameters.Where(p => !p.IsOut)
            .Select(p => new ParameterFact(p.Name, _facts.MapType(p.Type), p.Default)).ToArray();
        var args = CompleteArguments(inputs, call.Args);
        RequireArity(method.Name, multi.Targets.Length, method.Parameters.Count(p => p.IsOut));
        var values = new List<string>();
        int input = 0, output = 0;
        foreach (var parameter in method.Parameters)
        {
            var type = _facts.MapType(parameter.Type);
            if (parameter.IsOut)
            {
                if (multi.Targets[output++] is not IlVar target) throw new Tcs2cException("foreign out target must be a local");
                var variable = Resolve(target.Name);
                RequireType(type, variable.Type, method.Name);
                values.Add(variable.Boxed ? variable.CName : $"&{variable.CName}");
            }
            else
            {
                var value = Temp("host_arg");
                ValidateArgument(type, args[input], method.Name);
                Line($"{type.CName} {value} = {RenderCoerced(args[input++], type)};");
                values.Add(value);
            }
        }
        Line($"{HostName(method.Name)}({string.Join(", ", values)});");
        return true;
    }
}

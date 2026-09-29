using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private void ValidateArgument(CType target, IlExpr argument, string context)
    {
        if (target.Kind == CTypeKind.Closure && argument is IlClosure closure)
        {
            RequireArity(context, closure.Params.Length, target.Parameters!.Count);
            return;
        }
        RequireAssignable(target, TypeOf(argument), context);
    }

    private static IReadOnlyList<IlExpr> CompleteArguments(
        IReadOnlyList<ParameterFact> parameters, IReadOnlyList<IlExpr> supplied)
    {
        if (supplied.Count > parameters.Count)
            throw new Tcs2cException("too many arguments");
        var result = supplied.ToList();
        for (var i = supplied.Count; i < parameters.Count; i++)
            result.Add(parameters[i].Default
                ?? throw new Tcs2cException($"missing argument: {parameters[i].Name}"));
        return result;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    // user-defined operator は class table 上の static 関数 (LuaNaming.OperatorName:
    // `__add`、overload は `__mul_1` `__mul_2` …)。呼び出し箇所が Roslyn の選んだ
    // overload を直接呼ぶ (TryBuildUserOperatorCall) ので、実行時に operand 型で
    // 振り分ける metamethod は持たない。
    private void EmitOperators(SemanticModel model, string className,
        List<OperatorDeclarationSyntax> operators)
    {
        foreach (var op in operators)
        {
            if (LuaNaming.OperatorName(op) is { } luaName)
                EmitOperatorFunction(model, className, luaName, op);
        }
    }

    private void EmitOperatorFunction(SemanticModel model, string className,
        string luaName, OperatorDeclarationSyntax op)
    {
        SetSource(op);
        _currentType?.DefinitionKeys.Add(luaName);
        var paramNames = op.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList();
        AppendLine($"function {className}.{luaName}({string.Join(", ", paramNames)})");
        _indent++;
        BeginTraceFunction(model);
        // operator は呼び出しが metamethod の形で隠れ引数を足せないので、generic な型の
        // 型引数の既定値は自分の型の operand (instance が持つ) から取る
        var outerOnSelf = _typeArgDefaultsOnSelf;
        _typeArgDefaultsOnSelf = false;
        if (model.GetDeclaredSymbol(op)?.ContainingType is { } type
            && TypeArgDefaultParams(type) is { Count: > 0 } hidden)
        {
            var operands = op.ParameterList.Parameters
                .Where(p => model.GetDeclaredSymbol(p) is IParameterSymbol { Type: INamedTypeSymbol t }
                    && SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, type))
                .Select(p => p.Identifier.ValueText);
            var source = $"({string.Join(" or ", operands.Append("{}"))})";
            foreach (var name in hidden)
                AppendLine($"local {name} = {source}.{name}");
        }

        if (op.Body != null)
        {
            if (!TryEmitStatsViaIl(model, op.Body.Statements))
                ReportUnsupportedBody(model, op.Body.Statements);
        }
        else if (op.ExpressionBody != null)
        {
            if (!TryEmitReturnViaIl(model, op.ExpressionBody.Expression))
                ReportUnsupportedBody(model, [op.ExpressionBody.Expression]);
        }

        _typeArgDefaultsOnSelf = outerOnSelf;
        EndTraceFunction();
        _indent--;
        AppendLine("end");
        AppendLine();
    }
}

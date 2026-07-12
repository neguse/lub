using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    // User-defined operator overloads map to Lua metamethods on the class
    // table (which is also the instance metatable). Multiple C# overloads of
    // one operator share a single metamethod, so the metamethod dispatches on
    // runtime operand types: metatable identity for class/record instances,
    // type() for primitives.
    private void EmitOperators(SemanticModel model, string className,
        List<OperatorDeclarationSyntax> operators)
    {
        foreach (var group in operators
            .GroupBy(op => TinyCsComplianceFacts.TryGetOperatorMetamethod(op,
                out var metamethod) ? metamethod : "")
            .Where(g => g.Key.Length > 0))
        {
            EmitOperatorGroup(model, className, group.Key, [.. group]);
        }
    }

    private void EmitOperatorGroup(SemanticModel model, string className,
        string metamethod, List<OperatorDeclarationSyntax> overloads)
    {
        if (overloads.Count == 1)
        {
            EmitOperatorFunction(model, className, metamethod, overloads[0]);
            return;
        }

        for (var i = 0; i < overloads.Count; i++)
        {
            EmitOperatorFunction(model, className, $"{metamethod}_{i + 1}",
                overloads[i]);
        }

        // All overloads of one metamethod share the same arity
        // (binary metamethods vs __unm), so a common parameter list works.
        var paramCount = overloads[0].ParameterList.Parameters.Count;
        var dispatchParams = paramCount == 1 ? new[] { "a" } : ["a", "b"];
        var paramList = string.Join(", ", dispatchParams);

        SetSource(overloads[0]);
        AppendLine($"function {className}.{metamethod}({paramList})");
        _indent++;
        for (var i = 0; i < overloads.Count; i++)
        {
            var condition = BuildOverloadCondition(model, overloads[i],
                dispatchParams);
            AppendLine($"{(i == 0 ? "if" : "elseif")} {condition} then");
            _indent++;
            AppendLine($"return {className}.{metamethod}_{i + 1}({paramList})");
            _indent--;
        }
        AppendLine("end");
        AppendLine($"error(\"{className}.{metamethod}: no matching operator overload\")");
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    private static string BuildOverloadCondition(SemanticModel model,
        OperatorDeclarationSyntax op, string[] dispatchParams)
    {
        var conditions = new List<string>();
        var parameters = op.ParameterList.Parameters;
        for (var i = 0; i < parameters.Count && i < dispatchParams.Length; i++)
        {
            var type = model.GetTypeInfo(parameters[i].Type!).Type;
            var check = GetOperandTypeCheck(type, dispatchParams[i]);
            if (check != null) conditions.Add(check);
        }
        return conditions.Count > 0 ? string.Join(" and ", conditions) : "true";
    }

    private static string? GetOperandTypeCheck(ITypeSymbol? type, string arg)
    {
        if (type == null) return null;
        if (type.SpecialType is SpecialType.System_Int32
            or SpecialType.System_Int64 or SpecialType.System_UInt32
            or SpecialType.System_Single or SpecialType.System_Double
            || type.TypeKind == TypeKind.Enum)
        {
            return $"type({arg}) == \"number\"";
        }
        if (type.SpecialType == SpecialType.System_String)
            return $"type({arg}) == \"string\"";
        if (type.SpecialType == SpecialType.System_Boolean)
            return $"type({arg}) == \"boolean\"";
        if (type.TypeKind == TypeKind.Class
            && type.SpecialType == SpecialType.None)
        {
            return $"getmetatable({arg}) == {type.Name}";
        }
        return null;
    }

    private void EmitOperatorFunction(SemanticModel model, string className,
        string luaName, OperatorDeclarationSyntax op)
    {
        SetSource(op);
        var paramNames = op.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList();
        AppendLine($"function {className}.{luaName}({string.Join(", ", paramNames)})");
        _indent++;

        if (op.Body != null)
        {
            foreach (var stmt in op.Body.Statements)
                VisitStatement(model, stmt);
        }
        else if (op.ExpressionBody != null)
        {
            AppendLine($"return {VisitExpression(model, op.ExpressionBody.Expression)}");
        }

        _indent--;
        AppendLine("end");
        AppendLine();
    }
}

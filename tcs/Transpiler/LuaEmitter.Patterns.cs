using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// pattern / 型判定、switch 式、null 条件アクセス、lambda
public partial class LuaEmitter
{
    private string VisitSimpleLambda(SemanticModel model,
        SimpleLambdaExpressionSyntax lambda)
    {
        var param = lambda.Parameter.Identifier.ValueText;
        if (lambda.ExpressionBody != null)
            return $"function({param}) {LambdaPatternLocals(lambda.ExpressionBody)}return " +
                   $"{VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({param}) {VisitLambdaBlock(model, lambda.Block!)} end";
    }

    private string VisitParenthesizedLambda(SemanticModel model,
        ParenthesizedLambdaExpressionSyntax lambda)
    {
        var parameters = string.Join(", ",
            lambda.ParameterList.Parameters.Select(p => p.Identifier.ValueText));
        if (lambda.ExpressionBody != null)
            return $"function({parameters}) {LambdaPatternLocals(lambda.ExpressionBody)}return " +
                   $"{VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({parameters}) {VisitLambdaBlock(model, lambda.Block!)} end";
    }

    // expression-bodied lambda 内の is-pattern designation は statement
    // pre-pass が無いため、function 冒頭で local 宣言する

    // expression-bodied lambda 内の is-pattern designation は statement
    // pre-pass が無いため、function 冒頭で local 宣言する
    private static string LambdaPatternLocals(ExpressionSyntax body)
    {
        var names = IsPatternDesignationNames(body).ToList();
        return names.Count > 0 ? $"local {string.Join(", ", names)}; " : "";
    }

    private string VisitLambdaBlock(SemanticModel model, BlockSyntax block)
    {
        var savedSb = _sb.ToString();
        var savedIndent = _indent;
        var savedLuaLine = _luaLine;
        var savedSource = _currentSource;
        _sb.Clear();
        _indent = 0;

        foreach (var s in block.Statements)
            VisitStatement(model, s);

        var body = _sb.ToString().Trim();
        SourceMap.RemoveFrom(savedLuaLine);
        _sb.Clear();
        _sb.Append(savedSb);
        _indent = savedIndent;
        _luaLine = savedLuaLine;
        _currentSource = savedSource;
        return body;
    }

    private string VisitSwitchExpression(SemanticModel model,
        SwitchExpressionSyntax switchExpr)
    {
        // 対象式は IIFE local へ一度だけ評価する。ネストした switch expression
        // は内側 IIFE の local が shadow する。
        var governing = VisitExpression(model, switchExpr.GoverningExpression);
        var parts = new List<string>();

        foreach (var arm in switchExpr.Arms)
        {
            var value = VisitExpression(model, arm.Expression);
            if (arm.Pattern is DiscardPatternSyntax)
            {
                parts.Add($"else return {value}");
            }
            else
            {
                var pattern = VisitPattern(model, arm.Pattern, "__tcs_sw");
                var whenClause = arm.WhenClause != null
                    ? $" and {VisitExpression(model, arm.WhenClause.Condition)}" : "";
                var keyword = parts.Count == 0 ? "if" : "elseif";
                parts.Add($"{keyword} {pattern}{whenClause} then return {value}");
            }
        }

        // arm の declaration pattern designation (`int v => v`) は IIFE 内で
        // 対象値へ束縛する (switch statement の chain 前束縛と同方針)
        var bindings = string.Concat(switchExpr.Arms
            .Select(a => a.Pattern)
            .OfType<DeclarationPatternSyntax>()
            .Where(dp => dp.Designation is SingleVariableDesignationSyntax)
            .Select(dp => $"local {((SingleVariableDesignationSyntax)dp.Designation!).Identifier.ValueText} = __tcs_sw; "));

        return $"(function() local __tcs_sw = {governing}; {bindings}" +
            $"{string.Join(" ", parts)} end end)()";
    }

    // verbatim 型名 (@float) は raw syntax text に @ が残るため、pattern 経路の
    // 型参照は token ValueText から組み立てる。

    // verbatim 型名 (@float) は raw syntax text に @ が残るため、pattern 経路の
    // 型参照は token ValueText から組み立てる。
    private static string FormatTypeReference(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax qualified =>
            $"{FormatTypeReference(qualified.Left)}.{FormatTypeReference(qualified.Right)}",
        _ => type.ToString(),
    };

    // 値型・string は Lua 側に型 table が無く getmetatable 比較だと nil が
    // マッチする (未定義 global との nil == nil)。type() 判定にする。
    // int/float は Lua の integer/float subtype が代入経路で揺れるため
    // "number" 一括 (静的型が異なる組合せは C# コンパイルで弾かれる)。

    // 値型・string は Lua 側に型 table が無く getmetatable 比較だと nil が
    // マッチする (未定義 global との nil == nil)。type() 判定にする。
    // int/float は Lua の integer/float subtype が代入経路で揺れるため
    // "number" 一括 (静的型が異なる組合せは C# コンパイルで弾かれる)。
    private static string? LuaTypeNameFor(ITypeSymbol? patternType)
    {
        var type = UnwrapNullable(patternType);
        if (type == null) return null;
        if (type.SpecialType == SpecialType.System_String) return "string";
        if (type.SpecialType == SpecialType.System_Boolean) return "boolean";
        if (type.TypeKind == TypeKind.Enum || IsIntegralType(type)
            || IsFloatingType(type))
        {
            return "number";
        }
        return null;
    }

    private static string EmitTypeCheck(string expr, ITypeSymbol? patternType,
        string typeRef) =>
        LuaTypeNameFor(patternType) is { } luaType
            ? $"type({expr}) == \"{luaType}\""
            : $"getmetatable({expr}) == {typeRef}";

    private string VisitPattern(SemanticModel model, PatternSyntax pattern,
        string governing)
    {
        return pattern switch
        {
            // 型名だけの arm (Circle => ...) は syntax 上 ConstantPattern に
            // なるため、semantic で型と判れば metatable 比較にする。
            ConstantPatternSyntax cp
                when model.GetSymbolInfo(cp.Expression).Symbol is ITypeSymbol patType =>
                EmitTypeCheck(governing, patType,
                    VisitExpression(model, cp.Expression)),
            ConstantPatternSyntax cp =>
                $"{governing} == {VisitExpression(model, cp.Expression)}",
            DiscardPatternSyntax => "true",
            DeclarationPatternSyntax dp =>
                EmitTypeCheck(governing, model.GetTypeInfo(dp.Type).Type,
                    FormatTypeReference(dp.Type)),
            RecursivePatternSyntax rp => VisitRecursivePattern(model, governing, rp),
            RelationalPatternSyntax rel =>
                $"{governing} {RelationalOp(rel)} {VisitExpression(model, rel.Expression)}",
            BinaryPatternSyntax bp =>
                $"({VisitPattern(model, bp.Left, governing)} " +
                $"{(bp.IsKind(SyntaxKind.AndPattern) ? "and" : "or")} " +
                $"{VisitPattern(model, bp.Right, governing)})",
            _ => $"{WarnUnsupported(pattern, $"pattern: {pattern.Kind()}")} true"
        };
    }

    private string VisitIsPattern(SemanticModel model,
        IsPatternExpressionSyntax isPattern)
    {
        var expr = VisitExpression(model, isPattern.Expression);
        // designation は statement/lambda 前で `local name` 宣言済み。IIFE 内で
        // 一度だけ評価・代入し、型判定は束縛済みの値に対して行う
        if (isPattern.Pattern is DeclarationPatternSyntax
            { Designation: SingleVariableDesignationSyntax sv } dp)
        {
            var name = sv.Identifier.ValueText;
            var check = EmitTypeCheck(name, model.GetTypeInfo(dp.Type).Type,
                FormatTypeReference(dp.Type));
            return $"(function() {name} = {expr}; return {check} end)()";
        }
        return $"({VisitIsSubPattern(model, expr, isPattern.Pattern)})";
    }

    private string VisitIsSubPattern(SemanticModel model, string expr,
        PatternSyntax pattern)
    {
        return pattern switch
        {
            ConstantPatternSyntax cp
                when model.GetSymbolInfo(cp.Expression).Symbol is ITypeSymbol patType =>
                EmitTypeCheck(expr, patType, VisitExpression(model, cp.Expression)),
            ConstantPatternSyntax cp =>
                $"{expr} == {VisitExpression(model, cp.Expression)}",
            TypePatternSyntax tp =>
                EmitTypeCheck(expr, model.GetTypeInfo(tp.Type).Type,
                    FormatTypeReference(tp.Type)),
            DeclarationPatternSyntax dp =>
                EmitTypeCheck(expr, model.GetTypeInfo(dp.Type).Type,
                    FormatTypeReference(dp.Type)),
            UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern } notPat =>
                $"not ({VisitIsSubPattern(model, expr, notPat.Pattern)})",
            RelationalPatternSyntax rp =>
                $"{expr} {RelationalOp(rp)} {VisitExpression(model, rp.Expression)}",
            BinaryPatternSyntax bp =>
                $"({VisitIsSubPattern(model, expr, bp.Left)} " +
                $"{(bp.IsKind(SyntaxKind.AndPattern) ? "and" : "or")} " +
                $"{VisitIsSubPattern(model, expr, bp.Right)})",
            RecursivePatternSyntax rp => VisitRecursivePattern(model, expr, rp),
            _ => $"{WarnUnsupported(pattern, $"pattern: {pattern.Kind()}")} true"
        };
    }

    private string VisitRecursivePattern(SemanticModel model, string expr,
        RecursivePatternSyntax rp)
    {
        var conditions = new List<string>();

        // Type check: is MyType { ... }
        if (rp.Type != null)
        {
            conditions.Add(EmitTypeCheck(expr,
                model.GetTypeInfo(rp.Type).Type, FormatTypeReference(rp.Type)));
        }

        // Property pattern: { X: > 0, Y: < 10 }
        if (rp.PropertyPatternClause != null)
        {
            foreach (var sub in rp.PropertyPatternClause.Subpatterns)
            {
                if (sub.NameColon != null)
                {
                    var propName = sub.NameColon.Name.Identifier.ValueText;
                    var propExpr = model.GetSymbolInfo(sub.NameColon.Name).Symbol
                            is IPropertySymbol patProp && IsCustomProperty(patProp)
                        ? $"{expr}:get_{propName}()"
                        : $"{expr}.{propName}";
                    conditions.Add(VisitIsSubPattern(model, propExpr, sub.Pattern));
                }
            }
        }

        if (conditions.Count == 0) return "true";
        return string.Join(" and ", conditions);
    }

    private static string RelationalOp(RelationalPatternSyntax rp) =>
        rp.OperatorToken.Kind() switch
        {
            SyntaxKind.GreaterThanToken => ">",
            SyntaxKind.GreaterThanEqualsToken => ">=",
            SyntaxKind.LessThanToken => "<",
            SyntaxKind.LessThanEqualsToken => "<=",
            _ => "=="
        };

    private string VisitConditionalAccess(SemanticModel model,
        ConditionalAccessExpressionSyntax condAccess)
    {
        var receiver = VisitExpression(model, condAccess.Expression);
        // receiver は IIFE local (__tcs_ca) へ一度だけ評価する。when-not-null 側の
        // 引数/index は if 分岐内でのみ評価される。ネストした ?. は内側 IIFE の
        // local が外側を shadow する (local の RHS は宣言前に評価されるため安全)。
        var receiverType = model.GetTypeInfo(condAccess.Expression).Type;
        var whenNotNull = VisitConditionalWhenNotNull(model,
            condAccess.WhenNotNull, "__tcs_ca", receiverType);
        return $"(function() local __tcs_ca = {receiver}; " +
            $"if __tcs_ca ~= nil then return {whenNotNull} end end)()";
    }

    private string VisitConditionalWhenNotNull(SemanticModel model,
        ExpressionSyntax expr, string obj, ITypeSymbol? receiverType)
    {
        return expr switch
        {
            MemberBindingExpressionSyntax mb =>
                VisitConditionalMemberBinding(mb, obj, receiverType),
            InvocationExpressionSyntax inv when inv.Expression is MemberBindingExpressionSyntax mb2 =>
                VisitConditionalInvocation(model, mb2, inv.ArgumentList, obj, receiverType),
            ElementBindingExpressionSyntax eb =>
                VisitConditionalElementAccess(model, eb, obj, receiverType),
            // a?.B()?.C 形のネスト: 内側の receiver (.B() 等) を外側の temp 上で
            // 評価してから、内側専用の IIFE local で shadow する
            ConditionalAccessExpressionSyntax nested =>
                VisitNestedConditionalAccess(model, nested, obj, receiverType),
            _ => VisitExpression(model, expr)
        };
    }

    private string VisitNestedConditionalAccess(SemanticModel model,
        ConditionalAccessExpressionSyntax nested, string obj,
        ITypeSymbol? receiverType)
    {
        var receiver = VisitConditionalWhenNotNull(model, nested.Expression,
            obj, receiverType);
        var nestedType = model.GetTypeInfo(nested.Expression).Type;
        var whenNotNull = VisitConditionalWhenNotNull(model, nested.WhenNotNull,
            "__tcs_ca", nestedType);
        return $"(function() local __tcs_ca = {receiver}; " +
            $"if __tcs_ca ~= nil then return {whenNotNull} end end)()";
    }

    private string VisitConditionalMemberBinding(MemberBindingExpressionSyntax mb,
        string obj, ITypeSymbol? receiverType)
    {
        var member = mb.Name.Identifier.ValueText;
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)))
            return IsDictType(typeDef) ? $"Dict.Count({obj})" : $"#{obj}";
        if (member == "Keys" && IsDictType(typeDef))
            return $"Dict.Keys({obj})";
        if (member == "Values" && IsDictType(typeDef))
            return $"Dict.Values({obj})";
        if (member == "Length" && (receiverType?.SpecialType == SpecialType.System_String
            || receiverType is IArrayTypeSymbol))
            return $"#{obj}";

        if (FindInstanceProperty(receiverType, member) is { } condProp
            && IsCustomProperty(condProp))
        {
            return $"{obj}:get_{member}()";
        }

        return $"{obj}.{member}";
    }

    private string VisitConditionalElementAccess(SemanticModel model,
        ElementBindingExpressionSyntax eb, string obj, ITypeSymbol? receiverType)
    {
        var index = VisitExpression(model, eb.ArgumentList.Arguments[0].Expression);
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
        // List<T>: 0-indexed → 1-indexed
        if (IsListType(typeDef) || receiverType is IArrayTypeSymbol)
            return $"{obj}[{index} + 1]";
        return $"{obj}[{index}]";
    }

    private string VisitConditionalInvocation(SemanticModel model,
        MemberBindingExpressionSyntax mb, ArgumentListSyntax argList,
        string obj, ITypeSymbol? receiverType)
    {
        var methodName = mb.Name.Identifier.ValueText;
        var args = argList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        if (receiverType?.SpecialType == SpecialType.System_String)
            return MapStringMethodCall(obj, methodName, args);

        if (IsListType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    return $"table.insert({obj}, {string.Join(", ", args)})";
                case "Remove":
                    return $"List.Remove({obj}, {string.Join(", ", args)})";
                case "RemoveAt":
                    return $"table.remove({obj}, {args[0]} + 1)";
                case "Clear":
                    return $"(function() for k in pairs({obj}) do {obj}[k] = nil end end)()";
                case "FirstOrDefault":
                case "LastOrDefault":
                    var predicate = args.Count > 0 ? args[0] : "nil";
                    var elementType = receiverType is INamedTypeSymbol
                        { TypeArguments.Length: 1 } named
                        ? named.TypeArguments[0]
                        : null;
                    return $"List.{methodName}({obj}, {predicate}, " +
                        $"{GetDefaultValueForType(elementType)})";
            }
            if (ListRuntimeMethods.Contains(methodName))
            {
                var allArgs = new List<string> { obj };
                allArgs.AddRange(args);
                return $"List.{methodName}({string.Join(", ", allArgs)})";
            }
        }

        if (IsDictType(typeDef))
        {
            return methodName switch
            {
                "Add" => $"{obj}[{args[0]}] = {args[1]}",
                "Remove" => $"Dict.Remove({obj}, {args[0]})",
                "ContainsKey" => $"({obj}[{args[0]}] ~= nil)",
                "TryGetValue" => $"(function() local __tcs_value = {obj}[{args[0]}]; " +
                    $"if __tcs_value ~= nil then {args[1]} = __tcs_value; return true " +
                    $"else {args[1]} = nil; return false end end)()",
                _ => $"{obj}:{methodName}({string.Join(", ", args)})"
            };
        }

        return $"{obj}:{methodName}({string.Join(", ", args)})";
    }
}

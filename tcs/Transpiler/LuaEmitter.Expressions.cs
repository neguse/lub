using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    // List/Dict method names that map to runtime library calls
    private static readonly HashSet<string> ListRuntimeMethods =
        ["Where", "Select", "Any", "All", "First", "FirstOrDefault",
         "OrderBy", "OrderByDescending", "Take", "Skip", "Last", "LastOrDefault",
         "Min", "Max", "Sum", "Count", "ToList", "ToDictionary",
         "Contains", "IndexOf"];

    private string VisitExpression(SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax nameOf
            && TinyCsComplianceFacts.TryGetUnsupportedSyntax(
                nameOf, model, out var syntaxName))
        {
            return VisitUnsupportedNameOf(model, nameOf, syntaxName);
        }

        return expr switch
        {
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression } defLit =>
                GetDefaultValueForType(model.GetTypeInfo(defLit).ConvertedType!),
            LiteralExpressionSyntax lit => VisitLiteral(lit),
            IdentifierNameSyntax id => ResolveIdentifier(model, id),
            BinaryExpressionSyntax bin => VisitBinary(model, bin),
            PrefixUnaryExpressionSyntax prefix => VisitPrefixUnary(model, prefix),
            PostfixUnaryExpressionSyntax postfix => VisitPostfixUnary(model, postfix),
            ParenthesizedExpressionSyntax paren =>
                $"({VisitExpression(model, paren.Expression)})",
            InvocationExpressionSyntax invocation => VisitInvocation(model, invocation),
            MemberAccessExpressionSyntax ma => VisitMemberAccess(model, ma),
            AssignmentExpressionSyntax assignment => VisitAssignment(model, assignment),
            ObjectCreationExpressionSyntax creation => VisitObjectCreation(model, creation),
            ImplicitObjectCreationExpressionSyntax ic =>
                VisitImplicitObjectCreation(model, ic),
            ThisExpressionSyntax => "self",
            BaseExpressionSyntax => "self",
            CastExpressionSyntax cast => VisitExpression(model, cast.Expression),
            SwitchExpressionSyntax switchExpr => VisitSwitchExpression(model, switchExpr),
            IsPatternExpressionSyntax isPattern => VisitIsPattern(model, isPattern),
            ConditionalAccessExpressionSyntax condAccess =>
                VisitConditionalAccess(model, condAccess),
            MemberBindingExpressionSyntax memberBinding =>
                $"__tcs_ca.{memberBinding.Name.Identifier.Text}",
            ConditionalExpressionSyntax ternary => VisitTernary(model, ternary),
            InterpolatedStringExpressionSyntax interp =>
                VisitInterpolatedString(model, interp),
            SimpleLambdaExpressionSyntax lambda => VisitSimpleLambda(model, lambda),
            ParenthesizedLambdaExpressionSyntax lambda =>
                VisitParenthesizedLambda(model, lambda),
            ElementAccessExpressionSyntax elemAccess =>
                VisitElementAccess(model, elemAccess),
            DefaultExpressionSyntax def => VisitDefault(model, def),
            ArrayCreationExpressionSyntax arrCreate =>
                VisitArrayCreation(model, arrCreate),
            ImplicitArrayCreationExpressionSyntax implArr =>
                VisitImplicitArrayCreation(model, implArr),
            WithExpressionSyntax withExpr => VisitWithExpression(model, withExpr),
            DeclarationExpressionSyntax declaration =>
                VisitDeclarationExpression(declaration),
            PredefinedTypeSyntax predefined => ResolvePredefinedType(predefined),
            _ => WarnUnsupported(expr, $"expression: {expr.Kind()}")
        };
    }

    private string ResolveIdentifier(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is IMethodSymbol method && method.ContainingType != null)
        {
            if (method.IsStatic)
                return $"{method.ContainingType.Name}.{method.Name}";
            // Instance method without explicit receiver → implicit this (self)
            return $"self:{method.Name}";
        }
        if (symbol is IFieldSymbol { IsStatic: false }
            or IPropertySymbol { IsStatic: false })
            return $"self.{id.Identifier.Text}";
        if (symbol is IFieldSymbol { IsStatic: true } sf && sf.ContainingType != null)
            return $"{sf.ContainingType.Name}.{id.Identifier.Text}";
        if (symbol is IPropertySymbol { IsStatic: true } sp && sp.ContainingType != null)
            return $"{sp.ContainingType.Name}.{id.Identifier.Text}";
        return id.Identifier.Text;
    }

    private static string VisitLiteral(LiteralExpressionSyntax lit) => lit.Kind() switch
    {
        SyntaxKind.NumericLiteralExpression => ConvertNumericLiteral(lit),
        SyntaxKind.StringLiteralExpression => ConvertStringLiteral(lit),
        SyntaxKind.Utf8StringLiteralExpression => ConvertStringLiteral(lit),
        SyntaxKind.CharacterLiteralExpression => EscapeLuaString(lit.Token.ValueText),
        SyntaxKind.TrueLiteralExpression => "true",
        SyntaxKind.FalseLiteralExpression => "false",
        SyntaxKind.NullLiteralExpression => "nil",
        SyntaxKind.DefaultLiteralExpression => "nil", // handled by VisitExpression context
        _ => $"--[[ unsupported literal: {lit.Kind()} ]]"
    };

    private static string ConvertNumericLiteral(LiteralExpressionSyntax lit)
    {
        var text = lit.Token.Text;
        // Binary literals (0b...) → convert to decimal (Lua doesn't support 0b)
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return lit.Token.Value?.ToString() ?? text;
        // Hex literals: strip separators but DON'T strip suffixes (F is a hex digit)
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return text.Replace("_", "");
        // Strip digit separators and numeric suffixes
        var result = StripNumericSuffix(text).Replace("_", "");
        return result;
    }

    private static string ConvertStringLiteral(LiteralExpressionSyntax lit)
    {
        // For verbatim (@"...") and raw ("""...""") strings, use ValueText to get resolved value
        if (lit.Token.Text.StartsWith('@') || lit.Token.Text.StartsWith('"') &&
            lit.Token.Text.Length >= 6 && lit.Token.Text.StartsWith("\"\"\""))
            return EscapeLuaString(lit.Token.ValueText);
        return lit.Token.Text;
    }

    private static string EscapeLuaString(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string StripNumericSuffix(string text)
    {
        // Remove C# numeric suffixes (f, F, d, D, m, M, L, l, u, U, ul, UL)
        if (text.Length > 1)
        {
            char last = text[^1];
            if (last is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'L' or 'l')
                return text[..^1];
            if (text.Length > 2 && text[^2..] is "ul" or "UL" or "Ul" or "uL")
                return text[..^2];
            if (last is 'u' or 'U')
                return text[..^1];
        }
        return text;
    }

    private string VisitBinary(SemanticModel model, BinaryExpressionSyntax bin)
    {
        var left = VisitExpression(model, bin.Left);
        var right = VisitExpression(model, bin.Right);
        var isStringConcat = bin.Kind() == SyntaxKind.AddExpression &&
            (model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin).Type?.SpecialType == SpecialType.System_String);
        var op = bin.Kind() switch
        {
            SyntaxKind.AddExpression => isStringConcat ? ".." : "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "~=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.LogicalAndExpression => "and",
            SyntaxKind.LogicalOrExpression => "or",
            SyntaxKind.CoalesceExpression => "or",
            // Integer bitwise operators map to Lua 5.5 natives. C# `^` is
            // Lua binary `~` (Lua `^` is exponentiation). bool `& | ^` stay
            // unsupported: Lua natives reject booleans and and/or would
            // change C#'s non-short-circuit evaluation.
            SyntaxKind.BitwiseAndExpression when !HasBoolOperand(model, bin) => "&",
            SyntaxKind.BitwiseOrExpression when !HasBoolOperand(model, bin) => "|",
            SyntaxKind.ExclusiveOrExpression when !HasBoolOperand(model, bin) => "~",
            SyntaxKind.LeftShiftExpression => "<<",
            SyntaxKind.RightShiftExpression => ">>",
            _ => WarnUnsupported(bin, $"binary expression: {bin.Kind()}")
        };
        return $"{left} {op} {right}";
    }

    private static bool HasBoolOperand(SemanticModel model,
        BinaryExpressionSyntax bin) =>
        model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_Boolean
        || model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_Boolean;

    private string VisitPrefixUnary(SemanticModel model, PrefixUnaryExpressionSyntax prefix)
    {
        var operand = VisitExpression(model, prefix.Operand);
        return prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => $"-{operand}",
            SyntaxKind.LogicalNotExpression => $"not {operand}",
            SyntaxKind.BitwiseNotExpression => $"~{operand}",
            SyntaxKind.PreIncrementExpression => $"({operand} + 1)",
            SyntaxKind.PreDecrementExpression => $"({operand} - 1)",
            _ => WarnUnsupported(prefix, $"unary expression: {prefix.Kind()}")
        };
    }

    private string VisitPostfixUnary(SemanticModel model,
        PostfixUnaryExpressionSyntax postfix)
    {
        var operand = VisitExpression(model, postfix.Operand);
        return postfix.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => $"{operand} + 1",
            SyntaxKind.PostDecrementExpression => $"{operand} - 1",
            // x! は型チェック専用。Lua 出力には operand をそのまま通す
            SyntaxKind.SuppressNullableWarningExpression => operand,
            _ => WarnUnsupported(postfix, $"postfix expression: {postfix.Kind()}")
        };
    }

    private string VisitInvocation(SemanticModel model,
        InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var symbol = model.GetSymbolInfo(ma).Symbol;
            var methodName = ma.Name.Identifier.Text;

            if (ma.Expression is BaseExpressionSyntax
                && symbol is IMethodSymbol baseMethod)
            {
                var allArgs = new List<string> { "self" };
                allArgs.AddRange(args);
                return $"{baseMethod.ContainingType.Name}.{methodName}({string.Join(", ", allArgs)})";
            }

            if (symbol is IMethodSymbol { IsStatic: true } staticMethod
                && IsTinySystemFacade(staticMethod.ContainingType))
                return $"{staticMethod.ContainingType.Name}.{methodName}({string.Join(", ", args)})";

            // --ref method の out 引数 → Lua multi-return 受け
            // (host 側は `local a, b = f(args)` の形で複数値を返す)
            if (symbol is IMethodSymbol refMethod
                && refMethod.Parameters.Any(p => p.RefKind == RefKind.Out)
                && refMethod.DeclaringSyntaxReferences
                    .Any(r => ReferenceTrees.Contains(r.SyntaxTree)))
                return EmitRefMultiReturnCall(model, invocation, ma, refMethod);

            // Check for List<T>/IEnumerable<T> method calls → runtime library
            if (symbol is IMethodSymbol methodSym && TryMapCollectionMethod(
                    model, ma, methodSym, methodName, args, out var result))
                return result;

            // Nullable<T>.GetValueOrDefault() → x or <default>
            if (methodName == "GetValueOrDefault" && symbol is IMethodSymbol nullableMethod
                && nullableMethod.ContainingType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                var obj = VisitExpression(model, ma.Expression);
                var underlyingType = ((INamedTypeSymbol)nullableMethod.ContainingType).TypeArguments[0];
                var defaultVal = GetDefaultValueForType(underlyingType);
                return $"({obj} or {defaultVal})";
            }

            // Console.WriteLine → print
            if (methodName == "WriteLine" && symbol is IMethodSymbol consoleMethod
                && consoleMethod.ContainingType.ToDisplayString() == "System.Console")
                return $"print({string.Join(", ", args)})";

            // Math method name mapping (C# → Lua runtime)
            if (symbol is IMethodSymbol mathMethod
                && mathMethod.ContainingType.ToDisplayString() == "System.Math")
            {
                var luaName = methodName switch
                {
                    "Ceiling" => "Ceil",
                    _ => methodName
                };
                return $"Math.{luaName}({string.Join(", ", args)})";
            }

            // string.Join / string.IsNullOrEmpty → String.* runtime call
            if (symbol is IMethodSymbol stringStaticMethod
                && stringStaticMethod.ContainingType.SpecialType == SpecialType.System_String
                && methodName is "Join" or "IsNullOrEmpty")
                return $"String.{methodName}({string.Join(", ", args)})";

            // String method calls → String.Method(str, args)
            if (TryMapStringMethod(model, ma, methodName, args, out var strResult))
                return strResult;

            // ToString() on any type → tostring(obj)
            if (TryMapToString(model, ma, methodName, out var toStrResult))
                return toStrResult;

            // Extension method: obj.ExtMethod(args) → ExtClass.ExtMethod(obj, args)
            if (symbol is IMethodSymbol { IsExtensionMethod: true, ReducedFrom: not null } extMethod)
            {
                var obj = VisitExpression(model, ma.Expression);
                var extClass = extMethod.ReducedFrom!.ContainingType.Name;
                var allArgs = new List<string> { obj };
                allArgs.AddRange(args);
                return $"{extClass}.{methodName}({string.Join(", ", allArgs)})";
            }

            // Regular instance method
            if (symbol is IMethodSymbol { IsStatic: false })
            {
                var obj = VisitExpression(model, ma.Expression);
                return $"{obj}:{methodName}({string.Join(", ", args)})";
            }

            var target = VisitExpression(model, invocation.Expression);
            return $"{target}({string.Join(", ", args)})";
        }

        var targetExpr = VisitExpression(model, invocation.Expression);
        return $"{targetExpr}({string.Join(", ", args)})";
    }

    private static string VisitUnsupportedNameOf(SemanticModel model,
        InvocationExpressionSyntax invocation, string syntaxName)
    {
        var constant = model.GetConstantValue(invocation);
        var value = constant is { HasValue: true, Value: string name }
            ? EscapeLuaString(name)
            : "nil";
        return $"({value} --[[ unsupported: {syntaxName} ]])";
    }

    // out 引数は Lua の追加戻り値として宣言順に受ける。out 変数の local 宣言は
    // EmitOutVarDeclarations が statement 冒頭で済ませている。
    private string EmitRefMultiReturnCall(SemanticModel model,
        InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax ma,
        IMethodSymbol method)
    {
        var callArgs = new List<string>();
        var outNames = new List<string>();
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                var name = TryGetOutArgumentName(arg);
                outNames.Add(string.IsNullOrEmpty(name) ? "_" : name!);
            }
            else if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
            {
                return WarnUnsupported(arg,
                    "ref argument on reference-only method");
            }
            else
            {
                callArgs.Add(VisitExpression(model, arg.Expression));
            }
        }

        var methodName = ma.Name.Identifier.Text;
        var call = method.IsStatic
            ? $"{method.ContainingType.Name}.{methodName}({string.Join(", ", callArgs)})"
            : $"{VisitExpression(model, ma.Expression)}:{methodName}({string.Join(", ", callArgs)})";
        var outs = string.Join(", ", outNames);
        if (method.ReturnsVoid)
            return $"{outs} = {call}";
        return $"(function() local __ret; __ret, {outs} = {call}; return __ret end)()";
    }

    private bool TryMapCollectionMethod(SemanticModel model,
        MemberAccessExpressionSyntax ma, IMethodSymbol methodSym,
        string methodName, List<string> args, out string result)
    {
        result = "";
        var receiverType = model.GetTypeInfo(ma.Expression).Type;
        if (receiverType == null) return false;

        var typeDef = receiverType.OriginalDefinition.ToDisplayString();
        var obj = VisitExpression(model, ma.Expression);

        // List<T> instance methods → List.Method(list, args)
        if (IsListType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    result = $"table.insert({obj}, {string.Join(", ", args)})";
                    return true;
                case "Remove":
                    result = $"List.Remove({obj}, {string.Join(", ", args)})";
                    return true;
                case "RemoveAt":
                    result = $"table.remove({obj}, {args[0]} + 1)";
                    return true;
                case "Clear":
                    result = $"(function() for k in pairs({obj}) do {obj}[k] = nil end end)()";
                    return true;
                case "Sort":
                    var allSortArgs = new List<string> { obj };
                    allSortArgs.AddRange(args);
                    result = $"List.Sort({string.Join(", ", allSortArgs)})";
                    return true;
            }
            if (ListRuntimeMethods.Contains(methodName))
            {
                var allArgs = new List<string> { obj };
                allArgs.AddRange(args);
                result = $"List.{methodName}({string.Join(", ", allArgs)})";
                return true;
            }
        }

        // Dictionary<K,V> methods
        if (IsDictType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    result = $"{obj}[{args[0]}] = {args[1]}";
                    return true;
                case "Remove":
                    result = $"Dict.Remove({obj}, {args[0]})";
                    return true;
                case "ContainsKey":
                    result = $"({obj}[{args[0]}] ~= nil)";
                    return true;
                case "TryGetValue":
                    var outTarget = args[1];
                    var defaultValue = GetDefaultValueForType(methodSym.Parameters.Length > 1
                        ? methodSym.Parameters[1].Type
                        : null);
                    result = $"(function() local __tcs_value = {obj}[{args[0]}]; " +
                             $"if __tcs_value ~= nil then {outTarget} = __tcs_value; return true " +
                             $"else {outTarget} = {defaultValue}; return false end end)()";
                    return true;
            }
        }

        // LINQ extension methods on IEnumerable<T>
        if (methodSym.IsExtensionMethod && ListRuntimeMethods.Contains(methodName))
        {
            var allArgs = new List<string> { obj };
            allArgs.AddRange(args);
            result = $"List.{methodName}({string.Join(", ", allArgs)})";
            return true;
        }

        return false;
    }

    private static bool IsListType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.List<");

    private static bool IsDictType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.Dictionary<");

    private static bool IsTinySystemFacade(INamedTypeSymbol? type)
    {
        if (type?.ContainingNamespace.ToDisplayString() != "TinySystem")
            return false;
        return type.Name is "Random" or "Math" or "String" or "List" or "Dict";
    }

    private string VisitMemberAccess(SemanticModel model,
        MemberAccessExpressionSyntax memberAccess)
    {
        var obj = VisitExpression(model, memberAccess.Expression);
        var member = memberAccess.Name.Identifier.Text;

        // Check for .Count on List<T> → #list, .Length on string → #str
        var symbol = model.GetSymbolInfo(memberAccess).Symbol;
        if (symbol is IFieldSymbol { IsStatic: true } staticField
            && IsTinySystemFacade(staticField.ContainingType))
            return $"{staticField.ContainingType.Name}.{member}";
        if (symbol is IPropertySymbol { IsStatic: true } staticProperty
            && IsTinySystemFacade(staticProperty.ContainingType))
            return $"{staticProperty.ContainingType.Name}.{member}";

        if (symbol is IPropertySymbol propSym)
        {
            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
            var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

            // Nullable<T>.HasValue → (x ~= nil), Nullable<T>.Value → x
            if (receiverType?.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                if (member == "HasValue") return $"({obj} ~= nil)";
                if (member == "Value") return obj;
            }
            if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)))
            {
                return IsDictType(typeDef)
                    ? $"Dict.Count({obj})"
                    : $"#{obj}";
            }
            if (member == "Keys" && IsDictType(typeDef))
                return $"Dict.Keys({obj})";
            if (member == "Values" && IsDictType(typeDef))
                return $"Dict.Values({obj})";
            if (member == "Length" && (receiverType?.SpecialType == SpecialType.System_String
                || receiverType is IArrayTypeSymbol))
                return $"#{obj}";
        }

        return $"{obj}.{member}";
    }

    private string VisitElementAccess(SemanticModel model,
        ElementAccessExpressionSyntax elemAccess)
    {
        var obj = VisitExpression(model, elemAccess.Expression);
        var index = VisitExpression(model, elemAccess.ArgumentList.Arguments[0].Expression);
        var receiverType = model.GetTypeInfo(elemAccess.Expression).Type;
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        // List<T> / array indexer: 0-indexed → 1-indexed
        if (IsListType(typeDef) || receiverType is IArrayTypeSymbol)
            return $"{obj}[{index} + 1]";

        // Dictionary<K,V> indexer
        return $"{obj}[{index}]";
    }

    private string VisitAssignment(SemanticModel model, AssignmentExpressionSyntax assign)
    {
        var left = VisitExpression(model, assign.Left);
        var right = VisitExpression(model, assign.Right);
        return assign.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => $"{left} = {right}",
            SyntaxKind.AddAssignmentExpression => $"{left} = {left} + {right}",
            SyntaxKind.SubtractAssignmentExpression => $"{left} = {left} - {right}",
            SyntaxKind.MultiplyAssignmentExpression => $"{left} = {left} * {right}",
            SyntaxKind.DivideAssignmentExpression => $"{left} = {left} / {right}",
            SyntaxKind.ModuloAssignmentExpression => $"{left} = {left} % {right}",
            SyntaxKind.AndAssignmentExpression when !IsBoolTarget(model, assign) =>
                $"{left} = {left} & {right}",
            SyntaxKind.OrAssignmentExpression when !IsBoolTarget(model, assign) =>
                $"{left} = {left} | {right}",
            SyntaxKind.ExclusiveOrAssignmentExpression when !IsBoolTarget(model, assign) =>
                $"{left} = {left} ~ {right}",
            SyntaxKind.LeftShiftAssignmentExpression => $"{left} = {left} << {right}",
            SyntaxKind.RightShiftAssignmentExpression => $"{left} = {left} >> {right}",
            SyntaxKind.CoalesceAssignmentExpression =>
                $"(function() if {left} == nil then {left} = {right} end return {left} end)()",
            _ => WarnUnsupported(assign, $"assignment expression: {assign.Kind()}")
        };
    }

    private static bool IsBoolTarget(SemanticModel model,
        AssignmentExpressionSyntax assign) =>
        model.GetTypeInfo(assign.Left).Type?.SpecialType == SpecialType.System_Boolean;

    private string VisitObjectCreation(SemanticModel model,
        ObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).Type;
        var typeName = typeSymbol?.Name ?? creation.Type.ToString();
        var typeDef = typeSymbol?.OriginalDefinition.ToDisplayString() ?? "";

        // new List<T> { ... } → { items }
        if (IsListType(typeDef))
            return VisitListInitializer(model, creation.Initializer);
        // new Dictionary<K,V> { ... } → { [k]=v, ... }
        if (IsDictType(typeDef))
            return VisitDictInitializer(model, creation.Initializer);

        var args = creation.ArgumentList?.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList() ?? [];

        if (IsReferenceOnlyType(typeSymbol))
        {
            if (args.Count > 0)
                _ = WarnUnsupported(creation,
                    "constructor arguments on reference-only type");
            return EmitRefTypeTable(model, creation.Initializer);
        }

        var ctor = $"{typeName}.new({string.Join(", ", args)})";
        return creation.Initializer != null
            ? EmitObjectInitializer(model, ctor, creation.Initializer)
            : ctor;
    }

    // Reference-only (--ref) types have no Lua-side class table; emit a plain
    // table so host APIs receive `{ key = value, ... }` option tables.
    private string EmitRefTypeTable(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return "{}";
        var entries = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax
                {
                    Left: IdentifierNameSyntax name,
                    Right: not InitializerExpressionSyntax
                } assign)
            {
                var value = VisitExpression(model, assign.Right);
                entries.Add($"{name.Identifier.Text} = {value}");
            }
            else
            {
                _ = WarnUnsupported(expr, "object initializer entry");
            }
        }
        return $"{{{string.Join(", ", entries)}}}";
    }

    private string EmitObjectInitializer(SemanticModel model, string ctor,
        InitializerExpressionSyntax initializer)
    {
        var stmts = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax
                {
                    Left: IdentifierNameSyntax name,
                    Right: not InitializerExpressionSyntax
                } assign)
            {
                var value = VisitExpression(model, assign.Right);
                stmts.Add($"__init.{name.Identifier.Text} = {value}");
            }
            else
            {
                _ = WarnUnsupported(expr, "object initializer entry");
            }
        }
        return $"(function() local __init = {ctor} {string.Join(" ", stmts)} return __init end)()";
    }

    private string VisitListInitializer(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return "{}";
        var items = initializer.Expressions
            .Select(e => VisitExpression(model, e));
        return $"{{{string.Join(", ", items)}}}";
    }

    private string VisitDictInitializer(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return "{}";
        var entries = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is InitializerExpressionSyntax kvInit && kvInit.Expressions.Count == 2)
            {
                var key = VisitExpression(model, kvInit.Expressions[0]);
                var value = VisitExpression(model, kvInit.Expressions[1]);
                entries.Add($"[{key}] = {value}");
            }
            else if (expr is AssignmentExpressionSyntax assignExpr)
            {
                // Indexer initializer: { ["key"] = value }
                var key = assignExpr.Left is ImplicitElementAccessSyntax iea
                    ? VisitExpression(model, iea.ArgumentList.Arguments[0].Expression)
                    : VisitExpression(model, assignExpr.Left);
                var value = VisitExpression(model, assignExpr.Right);
                entries.Add($"[{key}] = {value}");
            }
        }
        return $"{{{string.Join(", ", entries)}}}";
    }

    private string VisitImplicitObjectCreation(SemanticModel model,
        ImplicitObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).ConvertedType;
        var typeName = typeSymbol?.Name ?? "UNKNOWN";
        var typeDef = typeSymbol?.OriginalDefinition.ToDisplayString() ?? "";
        if (IsListType(typeDef))
            return VisitListInitializer(model, creation.Initializer);
        if (IsDictType(typeDef))
            return VisitDictInitializer(model, creation.Initializer);

        var args = creation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();

        if (IsReferenceOnlyType(typeSymbol))
        {
            if (args.Count > 0)
                _ = WarnUnsupported(creation,
                    "constructor arguments on reference-only type");
            return EmitRefTypeTable(model, creation.Initializer);
        }

        var ctor = $"{typeName}.new({string.Join(", ", args)})";
        return creation.Initializer != null
            ? EmitObjectInitializer(model, ctor, creation.Initializer)
            : ctor;
    }

    private string VisitTernary(SemanticModel model, ConditionalExpressionSyntax ternary)
    {
        var cond = VisitExpression(model, ternary.Condition);
        var t = VisitExpression(model, ternary.WhenTrue);
        var f = VisitExpression(model, ternary.WhenFalse);
        return $"(function() if {cond} then return {t} else return {f} end end)()";
    }

    private string VisitInterpolatedString(SemanticModel model,
        InterpolatedStringExpressionSyntax interp)
    {
        var parts = new List<string>();
        foreach (var content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add($"\"{text.TextToken.Text}\"");
                    break;
                case InterpolationSyntax hole:
                    if (hole.FormatClause != null)
                    {
                        var fmt = hole.FormatClause.FormatStringToken.Text;
                        var luaFmt = ConvertFormatSpecifier(fmt);
                        var expr = VisitExpression(model, hole.Expression);
                        parts.Add($"string.format(\"{luaFmt}\", {expr})");
                    }
                    else
                    {
                        parts.Add($"tostring({VisitExpression(model, hole.Expression)})");
                    }
                    break;
            }
        }
        return string.Join(" .. ", parts);
    }

    private static string ConvertFormatSpecifier(string fmt)
    {
        if (string.IsNullOrEmpty(fmt)) return "%s";
        var c = char.ToUpper(fmt[0]);
        var precision = fmt.Length > 1 ? fmt[1..] : "";
        return c switch
        {
            'F' => string.IsNullOrEmpty(precision) ? "%.6f" : $"%.{precision}f",
            'N' => string.IsNullOrEmpty(precision) ? "%.2f" : $"%.{precision}f",
            'D' => string.IsNullOrEmpty(precision) ? "%d" : $"%0{precision}d",
            'X' => string.IsNullOrEmpty(precision) ? "%x" : $"%0{precision}x",
            'E' => string.IsNullOrEmpty(precision) ? "%e" : $"%.{precision}e",
            'G' => string.IsNullOrEmpty(precision) ? "%g" : $"%.{precision}g",
            _ => "%s"
        };
    }

    private string VisitSimpleLambda(SemanticModel model,
        SimpleLambdaExpressionSyntax lambda)
    {
        var param = lambda.Parameter.Identifier.Text;
        if (lambda.ExpressionBody != null)
            return $"function({param}) return " +
                   $"{VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({param}) {VisitLambdaBlock(model, lambda.Block!)} end";
    }

    private string VisitParenthesizedLambda(SemanticModel model,
        ParenthesizedLambdaExpressionSyntax lambda)
    {
        var parameters = string.Join(", ",
            lambda.ParameterList.Parameters.Select(p => p.Identifier.Text));
        if (lambda.ExpressionBody != null)
            return $"function({parameters}) return " +
                   $"{VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({parameters}) {VisitLambdaBlock(model, lambda.Block!)} end";
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
                var pattern = VisitPattern(model, arm.Pattern, governing);
                var whenClause = arm.WhenClause != null
                    ? $" and {VisitExpression(model, arm.WhenClause.Condition)}" : "";
                var keyword = parts.Count == 0 ? "if" : "elseif";
                parts.Add($"{keyword} {pattern}{whenClause} then return {value}");
            }
        }

        return $"(function() {string.Join(" ", parts)} end end)()";
    }

    private string VisitPattern(SemanticModel model, PatternSyntax pattern,
        string governing)
    {
        return pattern switch
        {
            ConstantPatternSyntax cp =>
                $"{governing} == {VisitExpression(model, cp.Expression)}",
            DiscardPatternSyntax => "true",
            DeclarationPatternSyntax dp =>
                $"getmetatable({governing}) == {dp.Type}",
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
        return $"({VisitIsSubPattern(model, expr, isPattern.Pattern)})";
    }

    private string VisitIsSubPattern(SemanticModel model, string expr,
        PatternSyntax pattern)
    {
        return pattern switch
        {
            ConstantPatternSyntax cp =>
                $"{expr} == {VisitExpression(model, cp.Expression)}",
            TypePatternSyntax tp => $"getmetatable({expr}) == {tp.Type}",
            DeclarationPatternSyntax dp =>
                $"getmetatable({expr}) == {dp.Type}",
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
            conditions.Add($"getmetatable({expr}) == {rp.Type}");

        // Property pattern: { X: > 0, Y: < 10 }
        if (rp.PropertyPatternClause != null)
        {
            foreach (var sub in rp.PropertyPatternClause.Subpatterns)
            {
                if (sub.NameColon != null)
                {
                    var propName = sub.NameColon.Name.Identifier.Text;
                    var propExpr = $"{expr}.{propName}";
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
        var obj = VisitExpression(model, condAccess.Expression);
        // Use IIFE for safe nil propagation
        var savedObj = obj;
        // Visit the when-not-null part, which may contain MemberBindingExpression
        var receiverType = model.GetTypeInfo(condAccess.Expression).Type;
        var whenNotNull = VisitConditionalWhenNotNull(model, condAccess.WhenNotNull, savedObj, receiverType);
        return $"(function() if {savedObj} ~= nil then return {whenNotNull} end end)()";
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
            _ => VisitExpression(model, expr)
        };
    }

    private string VisitConditionalMemberBinding(MemberBindingExpressionSyntax mb,
        string obj, ITypeSymbol? receiverType)
    {
        var member = mb.Name.Identifier.Text;
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
        var methodName = mb.Name.Identifier.Text;
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

    private static string MapStringMethodCall(string obj, string methodName,
        List<string> args) => methodName switch
    {
        "Contains" => $"String.Contains({obj}, {string.Join(", ", args)})",
        "IndexOf" => $"String.IndexOf({obj}, {string.Join(", ", args)})",
        "Replace" => $"String.Replace({obj}, {string.Join(", ", args)})",
        "StartsWith" => $"String.StartsWith({obj}, {args[0]})",
        "EndsWith" => $"String.EndsWith({obj}, {args[0]})",
        "Trim" => $"String.Trim({obj})",
        "Substring" => $"String.Substring({obj}, {string.Join(", ", args)})",
        "ToUpper" => $"string.upper({obj})",
        "ToLower" => $"string.lower({obj})",
        "Split" => args.Count == 0
            ? $"String.Split({obj})"
            : $"String.Split({obj}, {string.Join(", ", args)})",
        "ToString" => $"tostring({obj})",
        _ => $"{obj}:{methodName}({string.Join(", ", args)})"
    };

    // String method mapping
    private bool TryMapStringMethod(SemanticModel model,
        MemberAccessExpressionSyntax ma, string methodName, List<string> args,
        out string result)
    {
        result = "";
        var receiverType = model.GetTypeInfo(ma.Expression).Type;
        if (receiverType?.SpecialType != SpecialType.System_String) return false;

        var obj = VisitExpression(model, ma.Expression);
        result = MapStringMethodCall(obj, methodName, args);
        return !result.StartsWith(obj + ":", StringComparison.Ordinal);
    }

    private string VisitWithExpression(SemanticModel model, WithExpressionSyntax withExpr)
    {
        var obj = VisitExpression(model, withExpr.Expression);
        var overrides = new List<string>();
        foreach (var assign in withExpr.Initializer.Expressions)
        {
            if (assign is AssignmentExpressionSyntax a
                && a.Left is IdentifierNameSyntax id)
            {
                var value = VisitExpression(model, a.Right);
                overrides.Add($"__tcs_copy.{id.Identifier.Text} = {value}");
            }
        }
        return $"(function() local __tcs_copy = {{}}; " +
               $"for k,v in pairs({obj}) do __tcs_copy[k] = v end; " +
               $"setmetatable(__tcs_copy, getmetatable({obj})); " +
               $"{string.Join("; ", overrides)}; " +
               $"return __tcs_copy end)()";
    }

    private string VisitDefault(SemanticModel model, DefaultExpressionSyntax def)
    {
        var type = model.GetTypeInfo(def).Type;
        return type != null ? GetDefaultValueForType(type) : "nil";
    }

    private static string VisitDeclarationExpression(DeclarationExpressionSyntax declaration) =>
        declaration.Designation switch
        {
            SingleVariableDesignationSyntax single => single.Identifier.Text,
            DiscardDesignationSyntax => "_",
            _ => "_"
        };

    private static string? TryGetOutArgumentName(ArgumentSyntax argument)
    {
        if (!argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            return null;

        return argument.Expression switch
        {
            DeclarationExpressionSyntax declaration =>
                VisitDeclarationExpression(declaration),
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static string GetDefaultValueForType(ITypeSymbol? type) => type?.SpecialType switch
    {
        SpecialType.System_Boolean => "false",
        SpecialType.System_Int32 or SpecialType.System_Int64
            or SpecialType.System_UInt32 or SpecialType.System_Single
            or SpecialType.System_Double => "0",
        _ => "nil"
    };

    private string VisitArrayCreation(SemanticModel model, ArrayCreationExpressionSyntax arr)
    {
        if (arr.Initializer != null)
        {
            var items = arr.Initializer.Expressions
                .Select(e => VisitExpression(model, e));
            return $"{{{string.Join(", ", items)}}}";
        }
        return "{}";
    }

    private string VisitImplicitArrayCreation(SemanticModel model,
        ImplicitArrayCreationExpressionSyntax arr)
    {
        var items = arr.Initializer.Expressions
            .Select(e => VisitExpression(model, e));
        return $"{{{string.Join(", ", items)}}}";
    }

    private static string ResolvePredefinedType(PredefinedTypeSyntax predefined) =>
        predefined.Keyword.Text switch
        {
            "string" => "string",
            "int" => "math",
            "float" => "math",
            "double" => "math",
            _ => predefined.Keyword.Text
        };

    // Handle ToString() on any type
    private bool TryMapToString(SemanticModel model,
        MemberAccessExpressionSyntax ma, string methodName, out string result)
    {
        result = "";
        if (methodName != "ToString") return false;
        var obj = VisitExpression(model, ma.Expression);
        result = $"tostring({obj})";
        return true;
    }
}

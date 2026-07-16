using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// invocation と API mapping (collection / string / Math / member access)
public partial class LuaEmitter
{
    private string VisitInvocation(SemanticModel model,
        InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var symbol = model.GetSymbolInfo(ma).Symbol;
            var methodName = ma.Name.Identifier.ValueText;

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

            // Nullable<T>.GetValueOrDefault()
            if (methodName == "GetValueOrDefault" && symbol is IMethodSymbol nullableMethod
                && nullableMethod.ContainingType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                var obj = VisitExpression(model, ma.Expression);
                if (args.Count == 1)
                {
                    // 明示 fallback は C# と同じく receiver → 引数の順で常に
                    // 各 1 回評価し、false 値も fallback しない nil 判定にする
                    return $"(function() local __tcs_val = {obj}; " +
                        $"local __tcs_fb = {args[0]}; " +
                        $"if __tcs_val ~= nil then return __tcs_val end " +
                        $"return __tcs_fb end)()";
                }
                // 引数なしは default(T) が false/0/nil なので `or` で足りる
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

        var methodName = ma.Name.Identifier.ValueText;
        var call = method.IsStatic
            ? $"{method.ContainingType.Name}.{methodName}({string.Join(", ", callArgs)})"
            : $"{VisitExpression(model, ma.Expression)}:{methodName}({string.Join(", ", callArgs)})";
        var outs = string.Join(", ", outNames);
        if (method.ReturnsVoid)
            return $"{outs} = {call}";
        return $"(function() local __tcs_ret; __tcs_ret, {outs} = {call}; return __tcs_ret end)()";
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
                    result = $"(function() local __tcs_obj = {obj}; " +
                        $"for k in pairs(__tcs_obj) do __tcs_obj[k] = nil end end)()";
                    return true;
                case "Sort":
                    var allSortArgs = new List<string> { obj };
                    allSortArgs.AddRange(args);
                    result = $"List.Sort({string.Join(", ", allSortArgs)})";
                    return true;
                // empty/miss 時の default は要素型別 (C# default(T))。
                // 呼び出しサイトの return type から埋め込んで runtime へ渡す
                case "FirstOrDefault":
                case "LastOrDefault":
                    var predicate = args.Count > 0 ? args[0] : "nil";
                    result = $"List.{methodName}({obj}, {predicate}, " +
                        $"{GetDefaultValueForType(methodSym.ReturnType)})";
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
        var member = memberAccess.Name.Identifier.ValueText;

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

            // custom property の読みは生成済み getter を呼ぶ (auto は raw field)
            if (IsCustomProperty(propSym))
            {
                return propSym.IsStatic
                    ? $"{propSym.ContainingType.Name}.get_{member}()"
                    : $"{obj}:get_{member}()";
            }
        }

        return $"{obj}.{member}";
    }

    // body / expression body を持つ accessor があるか。auto property と
    // metadata 由来 (BCL) の property は false。

    // body / expression body を持つ accessor があるか。auto property と
    // metadata 由来 (BCL) の property は false。
    internal static bool IsCustomProperty(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not PropertyDeclarationSyntax decl)
                continue;
            if (decl.ExpressionBody != null) return true;
            if (decl.AccessorList != null && decl.AccessorList.Accessors
                    .Any(a => a.Body != null || a.ExpressionBody != null))
                return true;
        }
        return false;
    }

    // custom property への書き込み対象なら (receiver Lua, property 名,
    // receiver に副作用があり得るか, accessor 呼び出しの区切り) を返す。
    // static accessor は class function なので `.`、instance は `:`。

    private static IPropertySymbol? FindInstanceProperty(ITypeSymbol? type,
        string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetMembers(name).OfType<IPropertySymbol>()
                    .FirstOrDefault(p => !p.IsStatic) is { } prop)
                return prop;
        }
        return null;
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

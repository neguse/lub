using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: collection / string / Dict の invocation 写像 (legacy
// TryMapCollectionMethod / MapStringMethodCall 系)。
public partial class LuaEmitter
{
    // legacy TryMapCollectionMethod の写像 (Dict と Clear の IIFE 経路は
    // fallback)。戻り false は「この段は不一致 — funnel 続行」。
    private bool TryBuildCollectionCall(SemanticModel model,
        MemberAccessExpressionSyntax ma, IMethodSymbol methodSym,
        string methodName, ImmutableArray<IlExpr> args, out IlExpr? result)
    {
        result = null;
        // 拡張 method は LINQ (System.Linq.Enumerable) だけを runtime へ写す。
        // 同名の user 拡張 method は通常の拡張呼び出しにする
        if (methodSym.IsExtensionMethod
            && !IsTypeNamed(methodSym.ContainingType, "System.Linq.Enumerable"))
            return false;
        var receiverType = model.GetTypeInfo(ma.Expression).Type;
        if (receiverType == null) return false;
        var typeDef = receiverType.OriginalDefinition.ToDisplayString();
        if (IsDictType(typeDef))
        {
            var recvD = BuildExpr(model, ma.Expression);
            if (recvD == null)
                return methodName is "Add" or "Remove" or "ContainsKey";
            switch (methodName)
            {
                case "Remove" or "ContainsKey":
                    // backend 非依存の intrinsic call (il-spec §13)
                    result = new IlCall($"Dict.{methodName}", [recvD, DictKey(model,
                        receiverType, (ma.Parent as InvocationExpressionSyntax)
                            ?.ArgumentList.Arguments[0].Expression, args[0])]);
                    return true;
                case "Add":
                    // 代入形は statement 側 (BuildDictAddStatInto) が扱う
                    return true;
                default:
                    return false;
            }
        }
        if (!IsListType(typeDef))
        {
            if (methodSym.IsExtensionMethod
                && ListRuntimeMethods.Contains(methodName))
            {
                var recvExt = BuildExpr(model, ma.Expression);
                if (recvExt == null) return true; // fallback
                // OrDefault 系は List receiver 分岐と同様に default(T) を渡す
                if (methodName is "FirstOrDefault" or "LastOrDefault")
                {
                    var predicateExt = args.Length > 0
                        ? args[0]
                        : new IlLit("nil");
                    result = new IlCall($"List.{methodName}",
                        [recvExt, predicateExt, DefaultIl(methodSym.ReturnType)]);
                    return true;
                }
                result = new IlCall($"List.{TypedListMethod(methodName, methodSym)}",
                    [recvExt, .. WithStructElementOps(
                        model.GetTypeInfo(ma.Expression).Type, methodSym, methodName, args)]);
                return true;
            }
            return false;
        }

        var recv = BuildExpr(model, ma.Expression);
        if (recv == null) return true; // List method だが受け手未対応 → fallback
        switch (methodName)
        {
            case "Add":
                result = new IlCall("table.insert", [recv, .. args]);
                return true;
            case "Remove":
                result = new IlCall("List.Remove", [recv, .. WithStructElementOps(
                    model.GetTypeInfo(ma.Expression).Type, methodSym, methodName, args)]);
                return true;
            case "RemoveAt":
                result = new IlCall("table.remove",
                    [recv, new IlBin(IlBinOp.AddNum, args[0], new IlLit("1"))]);
                return true;
            case "Clear":
                result = BuildListClear(recv);
                return true;
            case "Sort":
                result = new IlCall("List.Sort", [recv, .. args]);
                return true;
            case "FirstOrDefault":
            case "LastOrDefault":
            {
                var predicate = args.Length > 0 ? args[0] : new IlLit("nil");
                result = new IlCall($"List.{methodName}",
                    [recv, predicate, DefaultIl(methodSym.ReturnType)]);
                return true;
            }
        }
        if (ListRuntimeMethods.Contains(methodName))
        {
            result = new IlCall($"List.{TypedListMethod(methodName, methodSym)}",
                [recv, .. WithStructElementOps(
                    model.GetTypeInfo(ma.Expression).Type, methodSym, methodName, args)]);
            return true;
        }
        return false;
    }

    // int / float の CompareTo(同じ型): -1 / 0 / 1 を返す backend 非依存の
    // intrinsic (float の NaN は他のどの値より小さい)。他の形は TCS1002
    private static bool IsNumericCompareTo(ISymbol? symbol) =>
        symbol is IMethodSymbol m && TinyCsComplianceFacts.IsSupportedNumericCompareTo(m);

    private static IlExpr? BuildNumericCompareTo(IlExpr? recv,
        ImmutableArray<IlExpr> args) =>
        recv == null ? null : new IlCall("Math.CompareTo", [recv, .. args]);

    // int の Sum は checked な List.SumInt (float の List.Sum と静的な型で分ける。
    // Lua の値の subtype では float の値が integer のこともある)
    private static string TypedListMethod(string methodName, IMethodSymbol method) =>
        methodName == "Sum" && method.ReturnType.SpecialType == SpecialType.System_Int32
            ? "SumInt" : methodName;

    // string method の char 引数 (IndexOf(char) / Split(char) 等) は 1 文字
    // string にして runtime へ渡す (char は整数 code unit)
    private static ImmutableArray<IlExpr> WrapCharArgs(SemanticModel model,
        ArgumentListSyntax argumentList, ImmutableArray<IlExpr> args)
    {
        var result = args.ToArray();
        for (var i = 0; i < result.Length && i < argumentList.Arguments.Count; i++)
            if (IsCharType(model.GetTypeInfo(argumentList.Arguments[i].Expression).Type))
                result[i] = new IlCall("string.char", [result[i]]);
        return [.. result];
    }

    // List.Clear: 全 key を nil に (IIFE。C backend は runtime 呼びに認識する)
    private static IlExpr BuildListClear(IlExpr recv) =>
        new IlIife([
            new IlLocal("__tcs_obj", recv),
            new IlForPairs("k", null, new IlVar("__tcs_obj"),
                new IlBlock([new IlAssign(
                    new IlIndex(new IlVar("__tcs_obj"), new IlVar("k"), false),
                    new IlLit("nil"))]))]);

    // struct 要素の Contains / IndexOf / Remove は値等価 (C# の
    // EqualityComparer<T>.Default)。Lua の raw == は table identity なので
    // 型別の op_Equality を末尾引数で渡す。ToList は新しい List へ要素の
    // copy を入れるので、変更可能な struct なら型別 __copy を渡す。float key の
    // ToDictionary は key の NaN 置換 (Dict.FloatKey) を渡す (C backend は
    // いずれも要素型から判るので無視)
    private IlExpr[] WithStructElementOps(ITypeSymbol? listType,
        IMethodSymbol? methodSym, string methodName, IEnumerable<IlExpr> argList)
    {
        var args = argList.ToArray();
        if (methodName == "ToDictionary" && HasFloatKey(methodSym?.ReturnType))
            return [.. args, .. args.Length < 2 ? [new IlLit("nil")] : Array.Empty<IlExpr>(),
                new IlField(new IlVar("Dict"), "FloatKey")];
        if (methodName == "ToList"
            && methodSym?.TypeArguments.FirstOrDefault() is { } item
            && IsMutableStruct(item))
            return [.. args, new IlField(new IlVar(TypeName(item)), "__copy")];
        if (methodName is not ("Contains" or "IndexOf" or "Remove")) return args;
        var elem = (listType as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
        if (!IsUserStruct(elem)) return args;
        return [.. args, new IlField(new IlVar(TypeName(elem!)), "op_Equality")];
    }

    // legacy MapStringMethodCall の写像 (default の `obj:m(...)` 形は不一致
    // として null → funnel 続行)
    private static IlExpr? TryBuildStringCall(IlExpr recv, string methodName,
        ImmutableArray<IlExpr> args) => methodName switch
    {
        "Contains" => new IlCall("String.Contains", [recv, .. args]),
        "IndexOf" => new IlCall("String.IndexOf", [recv, .. args]),
        "Replace" => new IlCall("String.Replace", [recv, .. args]),
        "StartsWith" => new IlCall("String.StartsWith", [recv, args[0]]),
        "EndsWith" => new IlCall("String.EndsWith", [recv, args[0]]),
        "Trim" => new IlCall("String.Trim", [recv]),
        "Substring" => new IlCall("String.Substring", [recv, .. args]),
        "ToUpper" => new IlCall("string.upper", [recv]),
        "ToLower" => new IlCall("string.lower", [recv]),
        "Split" => new IlCall("String.Split", [recv, .. args]),
        "ToString" => new IlCall("tostring", [recv]),
        _ => null,
    };

    // int.TryParse(s, out v) / float.TryParse — runtime の
    // Math.TryParseInt / TryParseFloat (found, value) を TryGetValue と同形の
    // multi-return IIFE で受ける
    private IlExpr? BuildNumericTryParse(SemanticModel model,
        InvocationExpressionSyntax invocation, string kind)
    {
        if (invocation.ArgumentList.Arguments.Count != 2) return null;
        var textArg = invocation.ArgumentList.Arguments[0];
        var outArg = invocation.ArgumentList.Arguments[1];
        if (!textArg.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
        var text = BuildExpr(model, textArg.Expression);
        if (text == null) return null;
        IlExpr? target = outArg.Expression switch
        {
            DeclarationExpressionSyntax decl =>
                new IlVar(VisitDeclarationExpression(decl)),
            IdentifierNameSyntax id => new IlVar(id.Identifier.ValueText),
            _ => null,
        };
        if (target == null) return null;
        return new IlIife([
            new IlMultiAssign(
                [new IlVar("__tcs_found"), new IlVar("__tcs_v")],
                [new IlCall($"Math.TryParse{kind}",
                    [text, new IlLit(kind == "Float" ? "0.0" : "0")])],
                Declare: true),
            new IlAssign(target, new IlVar("__tcs_v")),
            new IlReturn(new IlVar("__tcs_found"))]);
    }

    // Dictionary.TryGetValue(key, out v) — legacy IIFE の写像
    private IlExpr? BuildDictTryGetValue(SemanticModel model,
        InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax ma,
        IMethodSymbol methodSym)
    {
        if (invocation.ArgumentList.Arguments.Count != 2) return null;
        var keyArg = invocation.ArgumentList.Arguments[0];
        var outArg = invocation.ArgumentList.Arguments[1];
        if (!keyArg.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
        var key = BuildExpr(model, keyArg.Expression);
        var recv = BuildExpr(model, ma.Expression);
        if (key == null || recv == null) return null;
        IlExpr? target = outArg.Expression switch
        {
            DeclarationExpressionSyntax decl =>
                new IlVar(VisitDeclarationExpression(decl)),
            IdentifierNameSyntax id => new IlVar(id.Identifier.ValueText),
            _ => null,
        };
        if (target == null) return null;
        var valueType = methodSym.Parameters.Length > 1
            ? methodSym.Parameters[1].Type : null;
        var defaultValue = DefaultIl(valueType);
        // multi-return intrinsic (il-spec §13)。nil 比較の desugar を IL に
        // 残さない (C backend が「nil = 不在」を型付けできないため)
        return new IlIife([
            new IlMultiAssign(
                [new IlVar("__tcs_found"), new IlVar("__tcs_v")],
                [new IlCall("Dict.TryGet",
                    [recv, DictKey(model, model.GetTypeInfo(ma.Expression).Type,
                        keyArg.Expression, key), defaultValue])],
                Declare: true),
            // out への書き出しは格納値の copy (il-spec §10 の代入)
            new IlAssign(target, CopyIfMutableStruct(valueType, new IlVar("__tcs_v"))),
            new IlReturn(new IlVar("__tcs_found"))]);
    }

    // C# は省略した optional 引数の既定値を呼び出し側で、静的に束縛した宣言
    // から決める (interface / 基底の virtual 経由なら実装側の既定値ではない)。
    // ユーザー source の method 呼び出しは末尾の省略引数をここで埋める。
    // callee 冒頭の補完 (EmitParameterDefaults) は host など C# 以外からの
    // 呼び出しのために残す。既定値を IL にできなければ埋めずに callee に任せる
    private ImmutableArray<IlExpr> WithOmittedDefaults(SemanticModel model,
        InvocationExpressionSyntax invocation, ImmutableArray<IlExpr> args)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension,
            } method
            || args.Length >= method.Parameters.Length)
            return args;
        var filled = args.ToBuilder();
        foreach (var parameter in method.Parameters.Skip(args.Length))
        {
            if (parameter.OriginalDefinition.DeclaringSyntaxReferences
                    .FirstOrDefault()?.GetSyntax() is not ParameterSyntax
                    { Default.Value: { } value } syntax
                || ReferenceTrees.Contains(syntax.SyntaxTree))
                return args;
            var declModel = syntax.SyntaxTree == model.SyntaxTree
                ? model : model.Compilation.GetSemanticModel(syntax.SyntaxTree);
            if (BuildExpr(declModel, value) is not { } built) return args;
            filled.Add(built);
        }
        return filled.ToImmutable();
    }
}

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
                case "Remove":
                    result = new IlCall("Dict.Remove", [recvD, args[0]]);
                    return true;
                case "ContainsKey":
                    // backend 非依存の intrinsic call (il-spec §13)
                    result = new IlCall("Dict.ContainsKey", [recvD, args[0]]);
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
                result = new IlCall($"List.{methodName}",
                    [recvExt, .. WithStructEquality(
                        model.GetTypeInfo(ma.Expression).Type, methodName, args)]);
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
                result = new IlCall("List.Remove", [recv, .. WithStructEquality(
                    model.GetTypeInfo(ma.Expression).Type, methodName, args)]);
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
            result = new IlCall($"List.{methodName}", [recv, .. WithStructEquality(
                model.GetTypeInfo(ma.Expression).Type, methodName, args)]);
            return true;
        }
        return false;
    }

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
    // 型別の op_Equality を末尾引数で渡す (C backend は要素型から判るので無視)
    private static IlExpr[] WithStructEquality(ITypeSymbol? listType,
        string methodName, IEnumerable<IlExpr> argList)
    {
        var args = argList.ToArray();
        if (methodName is not ("Contains" or "IndexOf" or "Remove")) return args;
        var elem = (listType as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
        if (!IsUserStruct(elem)) return args;
        return [.. args, new IlField(new IlVar(elem!.Name), "op_Equality")];
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
                [new IlCall($"Math.TryParse{kind}", [text, new IlLit("0")])],
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
        var defaultValue = DefaultIl(
            methodSym.Parameters.Length > 1
                ? methodSym.Parameters[1].Type : null);
        // multi-return intrinsic (il-spec §13)。nil 比較の desugar を IL に
        // 残さない (C backend が「nil = 不在」を型付けできないため)
        return new IlIife([
            new IlMultiAssign(
                [new IlVar("__tcs_found"), new IlVar("__tcs_v")],
                [new IlCall("Dict.TryGet",
                    [recv, key, defaultValue])],
                Declare: true),
            new IlAssign(target, new IlVar("__tcs_v")),
            new IlReturn(new IlVar("__tcs_found"))]);
    }
}

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
        ExpressionSyntax receiver, IMethodSymbol methodSym,
        string methodName, ImmutableArray<IlExpr> args, out IlExpr? result)
    {
        result = null;
        // 拡張 method (TinySystem.Linq を含む) は user の静的関数の呼び出し
        if (methodSym.IsExtensionMethod) return false;
        var receiverType = model.GetTypeInfo(receiver).Type;
        if (receiverType == null) return false;
        var typeDef = receiverType.OriginalDefinition.ToDisplayString();
        if (IsDictType(typeDef))
        {
            var recvD = BuildExpr(model, receiver);
            if (recvD == null)
                return methodName is "Add" or "Remove" or "ContainsKey";
            switch (methodName)
            {
                case "Remove" or "ContainsKey":
                    // backend 非依存の intrinsic call (il-spec §13)
                    result = new IlCall($"Dict.{methodName}", [recvD, DictKey(model,
                        receiverType, (receiver.Parent?.Parent as InvocationExpressionSyntax)
                            ?.ArgumentList.Arguments[0].Expression, args[0])]);
                    return true;
                case "Add":
                    // 代入形は statement 側 (BuildDictAddStatInto) が扱う
                    return true;
                default:
                    return false;
            }
        }
        if (!IsListType(typeDef)) return false;

        var recv = BuildExpr(model, receiver);
        if (recv == null) return true; // List method だが受け手未対応 → fallback
        switch (methodName)
        {
            case "Add":
                result = new IlCall("table.insert", [recv, .. args]);
                return true;
            case "Remove":
                result = new IlCall("List.Remove", [recv, .. WithStructElementOps(
                    model.GetTypeInfo(receiver).Type, methodName, args)]);
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
            case "Contains" or "IndexOf":
                result = new IlCall($"List.{methodName}", [recv, .. WithStructElementOps(
                    model.GetTypeInfo(receiver).Type, methodName, args)]);
                return true;
        }
        return false;
    }

    // 拡張 method の static の形 `Ext.M(x, ...)`: receiver は第 1 引数で、
    // 拡張の形 `x.M(...)` と同じ静的関数の呼び出しにする
    private IlExpr? BuildStaticFormExtensionCall(IMethodSymbol ext,
        ImmutableArray<IlExpr> args) =>
        ext.Locations.Any(l => l.IsInSource)
            ? new IlCall($"{TypeRef(ext.ContainingType)}.{N(ext)}", args)
            : null;

    // int / float の CompareTo(同じ型): -1 / 0 / 1 を返す backend 非依存の
    // intrinsic (float の NaN は他のどの値より小さい)。他の形は TCS1002
    private static bool IsNumericCompareTo(ISymbol? symbol) =>
        symbol is IMethodSymbol m && TinyCsComplianceFacts.IsSupportedNumericCompareTo(m);

    private static IlExpr? BuildNumericCompareTo(IlExpr? recv,
        ImmutableArray<IlExpr> args) =>
        recv == null ? null : new IlCall("Math.CompareTo", [recv, .. args]);

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
    private IlExpr[] WithStructElementOps(ITypeSymbol? listType,
        string methodName, IEnumerable<IlExpr> argList)
    {
        var args = argList.ToArray();
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
    // 呼び出しのために残す。既定値を IL にできなければ nil を渡して callee に任せる。
    // user の generic method / generic class の static method には、末尾に型引数の
    // 既定値 (TypeArgDefaultArgs) を足す
    private ImmutableArray<IlExpr> WithOmittedDefaults(SemanticModel model,
        InvocationExpressionSyntax invocation, ImmutableArray<IlExpr> args)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension,
            } method
            || !method.Locations.Any(l => l.IsInSource
                && !ReferenceTrees.Contains(l.SourceTree!)))
            return args;
        var typeArgDefaults = TypeArgDefaultArgs(method).ToList();
        var filled = args.ToBuilder();
        foreach (var parameter in method.Parameters.Skip(args.Length))
        {
            if (OmittedDefault(model, parameter) is { } built)
                filled.Add(built);
            else if (typeArgDefaults.Count > 0)
                // 後ろに型引数の既定値を置くので位置を詰めない (callee が nil を埋める)
                filled.Add(new IlLit("nil"));
            else
                return args;
        }
        filled.AddRange(typeArgDefaults);
        return filled.ToImmutable();
    }

    private IlExpr? OmittedDefault(SemanticModel model, IParameterSymbol parameter)
    {
        if (parameter.OriginalDefinition.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax() is not ParameterSyntax
                { Default.Value: { } value } syntax
            || ReferenceTrees.Contains(syntax.SyntaxTree))
            return null;
        var declModel = syntax.SyntaxTree == model.SyntaxTree
            ? model : model.Compilation.GetSemanticModel(syntax.SyntaxTree);
        return BuildExpr(declModel, value);
    }
}

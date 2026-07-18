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
                    result = new IlParen(new IlBin(IlBinOp.Ne,
                        new IlIndex(recvD, args[0], false), new IlLit("nil")));
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
                result = new IlCall($"List.{methodName}",
                    [recvExt, .. args]);
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
                result = new IlCall("List.Remove", [recv, .. args]);
                return true;
            case "RemoveAt":
                result = new IlCall("table.remove",
                    [recv, new IlBin(IlBinOp.AddNum, args[0], new IlLit("1"))]);
                return true;
            case "Clear":
                result = new IlIife([
                    new IlLocal("__tcs_obj", recv),
                    new IlForPairs("k", null, new IlVar("__tcs_obj"),
                        new IlBlock([new IlAssign(
                            new IlIndex(new IlVar("__tcs_obj"),
                                new IlVar("k"), false),
                            new IlLit("nil"))]))]);
                return true;
            case "Sort":
                result = new IlCall("List.Sort", [recv, .. args]);
                return true;
            case "FirstOrDefault":
            case "LastOrDefault":
            {
                var predicate = args.Length > 0 ? args[0] : new IlLit("nil");
                result = new IlCall($"List.{methodName}",
                    [recv, predicate,
                     new IlLit(GetDefaultValueForType(methodSym.ReturnType))]);
                return true;
            }
        }
        if (ListRuntimeMethods.Contains(methodName))
        {
            result = new IlCall($"List.{methodName}", [recv, .. args]);
            return true;
        }
        return false;
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
}

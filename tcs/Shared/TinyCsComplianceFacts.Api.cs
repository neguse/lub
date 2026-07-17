using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// TCS1002: BCL API allowlist (完全シグネチャ単位) と判定
public static partial class TinyCsComplianceFacts
{
    private static readonly string[] UnsupportedNamespacePrefixes =
    [
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Text",
        "System.Threading",
    ];

    private static readonly HashSet<string> UnsupportedFullTypeNames =
        new(StringComparer.Ordinal)
        {
            "System.DateTime",
            "System.DateTimeOffset",
            "System.TimeSpan",
            "System.Guid",
        };

    private static readonly HashSet<string> SupportedMathFields =
        new(StringComparer.Ordinal) { "PI" };

    private static readonly HashSet<string> SupportedStringProperties =
        new(StringComparer.Ordinal) { "Length" };

    private static readonly HashSet<string> SupportedListProperties =
        new(StringComparer.Ordinal) { "Count" };

    private static readonly HashSet<string> SupportedDictionaryProperties =
        new(StringComparer.Ordinal) { "Count", "Keys", "Values" };

    // Method / constructor allowlist は完全シグネチャ単位。名前だけ一致する
    // 未実装 overload (indexed Select、comparer、StringComparison、capacity
    // constructor など) は Lua runtime が引数を黙って無視・誤処理するため、
    // ここに無いシグネチャは TCS1002 にする。

    // Method / constructor allowlist は完全シグネチャ単位。名前だけ一致する
    // 未実装 overload (indexed Select、comparer、StringComparison、capacity
    // constructor など) は Lua runtime が引数を黙って無視・誤処理するため、
    // ここに無いシグネチャは TCS1002 にする。
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        typeQualificationStyle:
            SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly HashSet<string> SupportedApiSignatures =
        new(StringComparer.Ordinal)
        {
            "System.Math.Min(int, int)",
            "System.Math.Min(long, long)",
            "System.Math.Min(float, float)",
            "System.Math.Min(double, double)",
            "System.Math.Max(int, int)",
            "System.Math.Max(long, long)",
            "System.Math.Max(float, float)",
            "System.Math.Max(double, double)",
            "System.Math.Clamp(int, int, int)",
            "System.Math.Clamp(long, long, long)",
            "System.Math.Clamp(float, float, float)",
            "System.Math.Clamp(double, double, double)",
            "System.Math.Abs(int)",
            "System.Math.Abs(long)",
            "System.Math.Abs(float)",
            "System.Math.Abs(double)",
            "System.Math.Floor(double)",
            "System.Math.Ceiling(double)",
            "System.Math.Sqrt(double)",
            "System.Math.Sin(double)",
            "System.Math.Cos(double)",
            "System.Math.Tan(double)",
            "System.Math.Exp(double)",
            "System.Math.Atan2(double, double)",
            "System.Math.Pow(double, double)",
            "System.Math.Log(double)",
            "System.Math.Log(double, double)",
            "System.Math.Round(double)",
            "System.Math.Round(double, int)",
            "System.Math.Sign(int)",
            "System.Math.Sign(long)",
            "System.Math.Sign(float)",
            "System.Math.Sign(double)",

            "string.Contains(string)",
            "string.Replace(string, string)",
            "string.StartsWith(string)",
            "string.EndsWith(string)",
            "string.Trim()",
            "string.Substring(int)",
            "string.Substring(int, int)",
            "string.Split(params System.ReadOnlySpan<char>)",
            "string.Split(string, System.StringSplitOptions)",
            "string.ToUpper()",
            "string.ToLower()",
            "string.ToString()",
            "string.IndexOf(string)",
            "string.IndexOf(string, int)",
            "string.Join(string, System.Collections.Generic.IEnumerable<string>)",
            "string.Join(string, params string[])",
            "string.Join(string, params System.ReadOnlySpan<string>)",
            "string.Join<T>(string, System.Collections.Generic.IEnumerable<T>)",
            "string.IsNullOrEmpty(string)",

            "System.Collections.Generic.List<T>.List()",
            "System.Collections.Generic.List<T>.Add(T)",
            "System.Collections.Generic.List<T>.Remove(T)",
            "System.Collections.Generic.List<T>.RemoveAt(int)",
            "System.Collections.Generic.List<T>.Clear()",
            "System.Collections.Generic.List<T>.Contains(T)",
            "System.Collections.Generic.List<T>.IndexOf(T)",
            "System.Collections.Generic.List<T>.Sort()",
            "System.Collections.Generic.List<T>.Sort(System.Comparison<T>)",

            "System.Collections.Generic.Dictionary<TKey, TValue>.Dictionary()",
            "System.Collections.Generic.Dictionary<TKey, TValue>.Add(TKey, TValue)",
            "System.Collections.Generic.Dictionary<TKey, TValue>.Remove(TKey)",
            "System.Collections.Generic.Dictionary<TKey, TValue>.ContainsKey(TKey)",
            "System.Collections.Generic.Dictionary<TKey, TValue>.TryGetValue(TKey, out TValue)",

            "System.Linq.Enumerable.Where<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.Select<TSource, TResult>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TResult>)",
            "System.Linq.Enumerable.Any<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.Any<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.All<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.First<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.First<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.FirstOrDefault<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.FirstOrDefault<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.Last<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.Last<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.LastOrDefault<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.LastOrDefault<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.OrderBy<TSource, TKey>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TKey>)",
            "System.Linq.Enumerable.OrderByDescending<TSource, TKey>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TKey>)",
            "System.Linq.Enumerable.Take<TSource>(System.Collections.Generic.IEnumerable<TSource>, int)",
            "System.Linq.Enumerable.Skip<TSource>(System.Collections.Generic.IEnumerable<TSource>, int)",
            "System.Linq.Enumerable.Min(System.Collections.Generic.IEnumerable<int>)",
            "System.Linq.Enumerable.Min(System.Collections.Generic.IEnumerable<long>)",
            "System.Linq.Enumerable.Min(System.Collections.Generic.IEnumerable<float>)",
            "System.Linq.Enumerable.Min(System.Collections.Generic.IEnumerable<double>)",
            "System.Linq.Enumerable.Min<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.Min<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, int>)",
            "System.Linq.Enumerable.Min<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, long>)",
            "System.Linq.Enumerable.Min<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, float>)",
            "System.Linq.Enumerable.Min<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, double>)",
            "System.Linq.Enumerable.Min<TSource, TResult>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TResult>)",
            "System.Linq.Enumerable.Max(System.Collections.Generic.IEnumerable<int>)",
            "System.Linq.Enumerable.Max(System.Collections.Generic.IEnumerable<long>)",
            "System.Linq.Enumerable.Max(System.Collections.Generic.IEnumerable<float>)",
            "System.Linq.Enumerable.Max(System.Collections.Generic.IEnumerable<double>)",
            "System.Linq.Enumerable.Max<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.Max<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, int>)",
            "System.Linq.Enumerable.Max<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, long>)",
            "System.Linq.Enumerable.Max<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, float>)",
            "System.Linq.Enumerable.Max<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, double>)",
            "System.Linq.Enumerable.Max<TSource, TResult>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TResult>)",
            "System.Linq.Enumerable.Sum(System.Collections.Generic.IEnumerable<int>)",
            "System.Linq.Enumerable.Sum(System.Collections.Generic.IEnumerable<long>)",
            "System.Linq.Enumerable.Sum(System.Collections.Generic.IEnumerable<float>)",
            "System.Linq.Enumerable.Sum(System.Collections.Generic.IEnumerable<double>)",
            "System.Linq.Enumerable.Sum<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, int>)",
            "System.Linq.Enumerable.Sum<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, long>)",
            "System.Linq.Enumerable.Sum<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, float>)",
            "System.Linq.Enumerable.Sum<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, double>)",
            "System.Linq.Enumerable.Count<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.Count<TSource>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, bool>)",
            "System.Linq.Enumerable.ToList<TSource>(System.Collections.Generic.IEnumerable<TSource>)",
            "System.Linq.Enumerable.ToDictionary<TSource, TKey>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TKey>)",
            "System.Linq.Enumerable.ToDictionary<TSource, TKey, TElement>(System.Collections.Generic.IEnumerable<TSource>, System.Func<TSource, TKey>, System.Func<TSource, TElement>)",
        };

    // シグネチャは許可するが、明示引数の個数に上限がある overload。
    // runtime が処理しない optional / params 引数を明示的に渡す呼び出しは
    // TCS1002 にする (Split(",", StringSplitOptions.None) 等)。

    // シグネチャは許可するが、明示引数の個数に上限がある overload。
    // runtime が処理しない optional / params 引数を明示的に渡す呼び出しは
    // TCS1002 にする (Split(",", StringSplitOptions.None) 等)。
    private static readonly Dictionary<string, int> MaxExplicitArguments =
        new(StringComparer.Ordinal)
        {
            ["string.Split(params System.ReadOnlySpan<char>)"] = 0,
            ["string.Split(string, System.StringSplitOptions)"] = 1,
        };

    public static bool TryGetUnsupportedApi(ISymbol? symbol, out string apiName)
        => TryGetUnsupportedApi(symbol, null, out apiName);

    public static bool TryGetUnsupportedApi(ISymbol? symbol,
        int? explicitArgumentCount, out string apiName)
    {
        apiName = "";
        if (symbol == null) return false;

        var targetSymbol = symbol is IMethodSymbol { ReducedFrom: not null } method
            ? method.ReducedFrom
            : symbol;
        var containingType = GetContainingType(targetSymbol);
        if (containingType == null) return false;

        var typeName = FormatTypeName(containingType);
        var namespaceName = containingType.ContainingNamespace?.ToDisplayString()
            ?? "";

        if (IsPartiallySupportedApi(targetSymbol, containingType))
        {
            if (IsSupportedApiMember(targetSymbol, containingType,
                    explicitArgumentCount))
                return false;

            apiName = FormatApiName(targetSymbol, containingType, typeName);
            return true;
        }

        if (!UnsupportedFullTypeNames.Contains(typeName)
            && !IsUnsupportedNamespace(namespaceName))
        {
            return false;
        }

        apiName = FormatApiName(targetSymbol, containingType, typeName);
        return true;
    }

    public static IEnumerable<string> AnalyzeUnsupportedApis(
        SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!TryGetUnsupportedApi(node, model, out var apiName))
                continue;

            yield return FormatWarning(node,
                TinyCsDiagnosticIds.UnsupportedApi,
                $"unsupported API: {apiName}");
        }
    }

    public static bool TryGetUnsupportedApi(SyntaxNode node,
        SemanticModel model, out string apiName)
    {
        apiName = "";
        if (IsWithinNameOf(node, model)) return false;

        ISymbol? symbol = node switch
        {
            InvocationExpressionSyntax invocation =>
                GetInvokedMethod(invocation, model),
            ObjectCreationExpressionSyntax creation =>
                model.GetSymbolInfo(creation).Symbol,
            ImplicitObjectCreationExpressionSyntax creation =>
                model.GetSymbolInfo(creation).Symbol,
            MemberAccessExpressionSyntax memberAccess
                when !IsInvocationTarget(memberAccess) =>
                model.GetSymbolInfo(memberAccess).Symbol,
            _ => null,
        };

        // A named type in System.Math.Min is a qualifier. The outer member
        // symbol is analyzed separately as the actual API access.
        if (node is MemberAccessExpressionSyntax qualifierAccess
            && symbol is INamedTypeSymbol
            && qualifierAccess.Parent is MemberAccessExpressionSyntax parentAccess
            && parentAccess.Expression == qualifierAccess)
        {
            return false;
        }

        return TryGetUnsupportedApi(symbol, CountExplicitArguments(node),
            out apiName);
    }

    public static bool IsWithinNameOf(IOperation? operation)
    {
        for (var current = operation; current != null;
             current = current.Parent)
        {
            if (current is INameOfOperation) return true;
        }
        return false;
    }

    private static bool IsWithinNameOf(SyntaxNode node,
        SemanticModel model) => node.AncestorsAndSelf()
        .OfType<InvocationExpressionSyntax>()
        .Any(invocation => model.GetOperation(invocation)
            is INameOfOperation);

    private static INamedTypeSymbol? GetContainingType(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol type => type,
            IMethodSymbol method => method.ContainingType,
            IPropertySymbol property => property.ContainingType,
            IFieldSymbol field => field.ContainingType,
            IEventSymbol evt => evt.ContainingType,
            _ => symbol.ContainingType,
        };

    private static IMethodSymbol? GetInvokedMethod(
        InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(invocation);
        return symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static bool IsInvocationTarget(
        MemberAccessExpressionSyntax memberAccess) =>
        memberAccess.Parent is InvocationExpressionSyntax invocation
        && invocation.Expression == memberAccess;

    private static bool IsListType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Collections.Generic.List<T>";

    private static bool IsDictType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Collections.Generic.Dictionary<TKey, TValue>";

    private static bool IsSystemLinqEnumerable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Linq.Enumerable";

    private static bool IsTinySystemList(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "TinySystem.List";

    private static bool IsPartiallySupportedApi(ISymbol symbol,
        INamedTypeSymbol containingType)
    {
        if (IsStringType(containingType)
            || IsMathType(containingType)
            || IsListType(containingType)
            || IsDictType(containingType)
            || IsSystemLinqEnumerable(containingType)
            || IsConsoleType(containingType))
        {
            return true;
        }

        return false;
    }

    // emitter が print へマップするのは WriteLine のみ。In/Out/Write/ReadLine
    // 等は Lua 出力に対応物がなく silent nil アクセスになるため allowlist 外。
    private static bool IsConsoleType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Console";

    private static bool IsSupportedApiMember(ISymbol symbol,
        INamedTypeSymbol containingType, int? explicitArgumentCount)
    {
        if (symbol is IMethodSymbol method)
        {
            if (IsConsoleType(containingType))
                return method.Name == "WriteLine";
            var signature = (method.ReducedFrom ?? method).OriginalDefinition
                .ToDisplayString(SignatureFormat);
            if (!SupportedApiSignatures.Contains(signature)) return false;
            return explicitArgumentCount == null
                || !MaxExplicitArguments.TryGetValue(signature, out var max)
                || explicitArgumentCount <= max;
        }

        if (symbol is IPropertySymbol property)
        {
            if (property.IsIndexer)
                return IsListType(containingType) || IsDictType(containingType);
            if (IsStringType(containingType))
                return SupportedStringProperties.Contains(property.Name);
            if (IsListType(containingType))
                return SupportedListProperties.Contains(property.Name);
            if (IsDictType(containingType))
                return SupportedDictionaryProperties.Contains(property.Name);
            return false;
        }

        if (symbol is IFieldSymbol field)
            return IsMathType(containingType)
                && SupportedMathFields.Contains(field.Name);

        return false;
    }

    // 明示的に書かれた引数の個数。optional 引数の compiler 補完や params の
    // 暗黙配列化を数えないよう、operation ではなく呼び出し syntax から数える
    // (Analyzer / transpiler check の判定を同じ正本に揃える)。

    // 明示的に書かれた引数の個数。optional 引数の compiler 補完や params の
    // 暗黙配列化を数えないよう、operation ではなく呼び出し syntax から数える
    // (Analyzer / transpiler check の判定を同じ正本に揃える)。
    public static int? CountExplicitArguments(SyntaxNode? node) => node switch
    {
        InvocationExpressionSyntax invocation =>
            invocation.ArgumentList.Arguments.Count,
        ImplicitObjectCreationExpressionSyntax creation =>
            creation.ArgumentList.Arguments.Count,
        BaseObjectCreationExpressionSyntax creation =>
            creation.ArgumentList?.Arguments.Count ?? 0,
        _ => null,
    };

    private static bool IsStringType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_String;

    private static bool IsMathType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Math";

    private static bool IsUnsupportedNamespace(string namespaceName)
    {
        foreach (var prefix in UnsupportedNamespacePrefixes)
        {
            if (namespaceName == prefix
                || namespaceName.StartsWith(prefix + ".",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatTypeName(INamedTypeSymbol type) =>
        type.IsGenericType
            ? type.OriginalDefinition.ToDisplayString(
                SymbolDisplayFormat.CSharpErrorMessageFormat)
            : type.ToDisplayString(
                SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string FormatApiName(ISymbol symbol, INamedTypeSymbol type,
        string typeName) =>
        symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => typeName,
            IMethodSymbol method => $"{typeName}.{method.Name}",
            IPropertySymbol property => $"{typeName}.{property.Name}",
            IFieldSymbol field => $"{typeName}.{field.Name}",
            IEventSymbol evt => $"{typeName}.{evt.Name}",
            INamedTypeSymbol => typeName,
            _ when SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type)
                => $"{typeName}.{symbol.Name}",
            _ => typeName,
        };
}

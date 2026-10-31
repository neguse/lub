using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// TCS1002: BCL API allowlist (完全シグネチャ単位) と判定
public static partial class TinyCsComplianceFacts
{
    // support-matrix の一覧 (§12、§16) で member を丸ごと通す BCL の型。member を絞る型は
    // IsPartiallySupportedApi。どちらにも無い BCL の型は TCS1002 で、乱数は TinySystem.Random、
    // float の Math は System.Math の overload、LINQ は TinySystem.Linq を使う
    private static readonly HashSet<string> SupportedBclTypes =
        new(StringComparer.Ordinal)
        {
            "System.Action",
            "System.Func",
            "System.Predicate",
            "System.Comparison",
            "System.ValueTuple",
            "System.Collections.Generic.KeyValuePair",
            "System.Collections.Generic.Dictionary.KeyCollection",
            "System.Collections.Generic.Dictionary.ValueCollection",
        };

    // 実行時の表現を持たず、型の検査にだけ現れてよい BCL の型 (§12、§16 の N/A)。member は TCS1002
    private static readonly HashSet<string> TypeCheckOnlyBclTypes =
        new(StringComparer.Ordinal)
        {
            "System.Attribute",
            "System.Type",
            "System.Collections.Generic.IEnumerable",
            "System.Collections.Generic.ICollection",
            "System.Collections.Generic.IList",
            "System.Collections.Generic.IDictionary",
            "System.Collections.Generic.IReadOnlyList",
            "System.Collections.Generic.IReadOnlyCollection",
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
            "string.Contains(char)",
            "string.Replace(string, string)",
            "string.Replace(char, char)",
            "string.StartsWith(string)",
            "string.StartsWith(char)",
            "string.EndsWith(string)",
            "string.EndsWith(char)",
            "string.IndexOf(char)",
            "string.IndexOf(char, int)",
            // char (整数 code unit) の ASCII 判定 / 変換
            "char.ToString()",
            "char.IsDigit(char)",
            "char.IsLetter(char)",
            "char.IsLetterOrDigit(char)",
            "char.IsWhiteSpace(char)",
            "char.IsUpper(char)",
            "char.IsLower(char)",
            "char.ToUpper(char)",
            "char.ToLower(char)",
            "char.ConvertFromUtf32(int)",
            "System.Text.Rune.ToString()",
            "string.Trim()",
            "string.Substring(int)",
            "string.Substring(int, int)",
            "string.Split(params System.ReadOnlySpan<char>)",
            "string.Split(char, System.StringSplitOptions)",
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
            "string.CompareOrdinal(string, string)",
            // host 連携で使う BCL: 環境変数と codepoint 走査。Lua 側は
            // os.getenv / utf8.codes に落ちる。int.Parse / double.Parse は
            // 数値型の member として元から allowlist の外側 (許可) で、
            // emitter が tonumber へ写す
            "System.Environment.GetEnvironmentVariable(string)",
            "string.EnumerateRunes()",

            "System.Collections.Generic.List<T>.List()",
            "System.Collections.Generic.List<T>.List(System.Collections.Generic.IEnumerable<T>)",
            "System.Collections.Generic.List<T>.Add(T)",
            "System.Collections.Generic.List<T>.Remove(T)",
            "System.Collections.Generic.List<T>.RemoveAt(int)",
            "System.Collections.Generic.List<T>.Clear()",
            "System.Collections.Generic.List<T>.Contains(T)",
            "System.Collections.Generic.List<T>.IndexOf(T)",
            "System.Collections.Generic.List<T>.FindIndex(System.Predicate<T>)",
            "System.Collections.Generic.List<T>.RemoveAll(System.Predicate<T>)",
            "System.Collections.Generic.List<T>.Sort()",
            "System.Collections.Generic.List<T>.Sort(System.Comparison<T>)",
            "System.Collections.Generic.List<T>.Insert(int, T)",
            "System.Collections.Generic.List<T>.GetRange(int, int)",

            "System.Collections.Generic.Dictionary<TKey, TValue>.Dictionary()",
            "System.Collections.Generic.Dictionary<TKey, TValue>.Add(TKey, TValue)",
            "System.Collections.Generic.Dictionary<TKey, TValue>.Remove(TKey)",
            "System.Collections.Generic.Dictionary<TKey, TValue>.ContainsKey(TKey)",
            "System.Collections.Generic.Dictionary<TKey, TValue>.TryGetValue(TKey, out TValue)",

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
            ["string.Split(params System.ReadOnlySpan<char>)"] = 1,
            ["string.Split(char, System.StringSplitOptions)"] = 1,
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

        if (targetSymbol is IMethodSymbol { Name: "CompareTo" } compareTo
            && !compareTo.Locations.Any(l => l.IsInSource)
            && !IsSupportedNumericCompareTo(compareTo))
        {
            apiName = FormatApiName(targetSymbol, containingType, typeName);
            return true;
        }

        // object の member は Lua / C に対応物が無い (record の合成するものも。値の比較は ==)。
        // user が宣言した同名の method は対象外
        if (targetSymbol is IMethodSymbol
            {
                Name: "Equals" or "GetHashCode" or "ReferenceEquals" or "GetType" or "MemberwiseClone",
            } objectMember
            && (objectMember.IsImplicitlyDeclared || objectMember.DeclaringSyntaxReferences.Length == 0))
        {
            apiName = FormatApiName(targetSymbol, containingType, typeName);
            return true;
        }

        if (IsPartiallySupportedApi(targetSymbol, containingType))
        {
            if (IsSupportedApiMember(targetSymbol, containingType,
                    explicitArgumentCount))
                return false;

            apiName = FormatApiName(targetSymbol, containingType, typeName);
            return true;
        }

        if (!IsBclType(containingType) || IsSupportedBclType(containingType))
            return false;

        apiName = FormatApiName(targetSymbol, containingType, typeName);
        return true;
    }

    private static bool IsBclType(INamedTypeSymbol type)
    {
        if (type.Locations.Any(l => l.IsInSource)) return false;
        var root = type.ContainingNamespace;
        while (root is { ContainingNamespace.IsGlobalNamespace: false })
            root = root.ContainingNamespace;
        return root?.Name is "System" or "Microsoft";
    }

    // 入れ子の型も含めた、型引数を除く名前 (`System.Collections.Generic.Dictionary.KeyCollection`)
    private static string BclTypeKey(INamedTypeSymbol type) =>
        type.ContainingType is { } outer
            ? $"{BclTypeKey(outer)}.{type.Name}"
            : $"{type.ContainingNamespace.ToDisplayString()}.{type.Name}";

    private static bool IsSupportedBclType(INamedTypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
            or SpecialType.System_Nullable_T or SpecialType.System_Boolean
            or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            or SpecialType.System_Single or SpecialType.System_Double
        || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        || SupportedBclTypes.Contains(BclTypeKey(type));

    // 型を書いた地点 (宣言、基底、キャスト、型引数)。member の呼び出しと new は
    // TryGetUnsupportedApi が見るので、修飾の左と new の型は数えない
    public static bool TryGetUnsupportedTypeMention(SyntaxNode node, SemanticModel model,
        out string apiName)
    {
        apiName = "";
        if (node is not (IdentifierNameSyntax { IsVar: false } or GenericNameSyntax
                or QualifiedNameSyntax or AliasQualifiedNameSyntax)
            || node.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax
                or ObjectCreationExpressionSyntax
            || node.Parent is MemberAccessExpressionSyntax
            || model.GetSymbolInfo(node).Symbol is not INamedTypeSymbol type)
            return false;
        if (type.TypeKind == TypeKind.Error || !IsBclType(type) || IsSupportedBclType(type)
            || IsPartiallySupportedType(type) || TypeCheckOnlyBclTypes.Contains(BclTypeKey(type))
            || IsWithinNameOf(node, model) || IsInAttribute(node))
            return false;
        apiName = FormatTypeName(type);
        return true;
    }

    // CompareTo は int / float の同じ型どうしだけを intrinsic (-1 / 0 / 1) に
    // する。object overload・enum・IComparable<T> 経由 (generic の T を含む)・
    // 差を返す char は対象外
    public static bool IsSupportedNumericCompareTo(IMethodSymbol method) =>
        method is { Name: "CompareTo", IsStatic: false, Parameters.Length: 1 }
        && method.ContainingType.SpecialType is SpecialType.System_Int32
            or SpecialType.System_Single
        && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type,
            method.ContainingType);

    // Enum.ToString は引数なしと、定数の書式 "G" / "F" / "D" (小文字も) だけ
    // (名前 / 名前の組み合わせ / 数値)。X 等の書式と IFormatProvider の
    // overload は対象外。format は第 1 引数の定数値 (無ければ HasValue = false)
    public static bool TryGetUnsupportedEnumToString(ISymbol? symbol,
        Optional<object?> format, out string apiName)
    {
        apiName = "";
        if (symbol is not IMethodSymbol { Name: "ToString" } method
            || method.ContainingType?.SpecialType != SpecialType.System_Enum)
            return false;
        if (method.Parameters.Length == 0
            || method.Parameters.Length == 1
                && method.Parameters[0].Type.SpecialType == SpecialType.System_String
                && format is { HasValue: true, Value: "G" or "g" or "F" or "f" or "D" or "d" })
            return false;
        apiName = $"{FormatTypeName(method.ContainingType)}.ToString";
        return true;
    }

    public static IEnumerable<string> AnalyzeUnsupportedApis(
        SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!TryGetUnsupportedApi(node, model, out var apiName)
                && !TryGetUnsupportedTypeMention(node, model, out apiName))
                continue;

            yield return FormatError(node,
                TinyCsDiagnosticIds.UnsupportedApi,
                $"unsupported API: {apiName}");
        }
    }

    public static bool TryGetUnsupportedApi(SyntaxNode node,
        SemanticModel model, out string apiName)
    {
        apiName = "";
        if (IsWithinNameOf(node, model) || IsInAttribute(node)) return false;

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

        if (node is InvocationExpressionSyntax enumCall
            && TryGetUnsupportedEnumToString(symbol,
                enumCall.ArgumentList.Arguments.Count > 0
                    ? model.GetConstantValue(enumCall.ArgumentList.Arguments[0].Expression)
                    : default, out apiName))
            return true;
        return TryGetUnsupportedApi(symbol, CountExplicitArguments(node),
            out apiName);
    }

    /// <summary>属性の中。実行時の意味を持たない metadata で、Lua / C に出ないので API の対象外
    /// (build が生成する AssemblyInfo も)。</summary>
    public static bool IsInAttribute(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<AttributeListSyntax>().Any();

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

    private static bool IsPartiallySupportedApi(ISymbol symbol,
        INamedTypeSymbol containingType) => IsPartiallySupportedType(containingType);

    private static bool IsPartiallySupportedType(INamedTypeSymbol type) =>
        IsStringType(type)
        || IsCharType(type)
        || IsMathType(type)
        || IsListType(type)
        || IsDictType(type)
        || IsConsoleType(type)
        || IsArrayType(type)
        || IsEnvironmentType(type)
        || IsRuneType(type)
        || IsEnumBaseType(type);

    private static bool IsEnumBaseType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_Enum;

    // 配列の member は Length だけ (配列の型の member も Array.Sort などの static も System.Array に属す)
    private static bool IsArrayType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_Array;

    private static bool IsEnvironmentType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Environment";

    private static bool IsRuneType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Text.Rune";

    // emitter が写すのは WriteLine (print) と 1 引数の Write (io.write)。In/Out/ReadLine
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
                return method.Name == "WriteLine"
                    || method is { Name: "Write", Parameters.Length: 1 };
            // 書式の判定は TryGetUnsupportedEnumToString。static member は対象外
            if (IsEnumBaseType(containingType))
                return method is { Name: "ToString", IsStatic: false };
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
                return IsListType(containingType) || IsDictType(containingType)
                    || IsStringType(containingType);
            if (IsArrayType(containingType))
                return property.Name == "Length";
            if (IsStringType(containingType))
                return SupportedStringProperties.Contains(property.Name);
            if (IsRuneType(containingType))
                return property.Name == "Value";
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

    private static bool IsCharType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_Char;

    private static bool IsMathType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Math";

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

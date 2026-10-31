using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// user の宣言の識別子は C# の綴りのまま Lua / IL / C の名前になる (q.md Q16)。
// Lua と生成コードの予約と重なる綴りは黙って別の意味になるので、build を止める
// TCS1001 にする:
//   - `__` で始まる識別子 (Lua の metamethod `__index` / `__eq`、生成コードの
//     `__copy` と `__tcs_*` の temp と同じ領域)
//   - 非 ASCII の識別子 (Lua の識別子は ASCII のみ)
//   - 生成コードが class table に置く key (`new` / `ctor`) と同名の member
//   - 型名と namespace 名の `_` (namespace の中の型と入れ子の型は修飾名を `_` で
//     結合した平らな global `A_B_C` になるので、`_` を含む名前は別の修飾名と
//     同じ global になり得る)
// --ref の型 (host の宣言) は診断の対象の木に入らないので対象外
public static partial class TinyCsComplianceFacts
{
    /// <summary>生成コードが class table (struct の table も) に置く key。
    /// `__index` / `__eq` / `__copy` は `__` の規則が受け持つ。</summary>
    public static readonly string[] ReservedClassKeys = ["new", "ctor"];

    // 構文だけで決まる種類。他の TCS1001 に隠れないよう、
    // TryGetUnsupportedSyntax の switch の先頭で見る
    private static bool TryGetIdentifierSyntax(SyntaxNode node,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            BaseTypeDeclarationSyntax type => TypeIdentifierSyntax(type.Identifier),
            DelegateDeclarationSyntax del => TypeIdentifierSyntax(del.Identifier),
            BaseNamespaceDeclarationSyntax ns => NamespaceNameSyntax(ns.Name),
            MethodDeclarationSyntax method => MemberIdentifierSyntax(method.Identifier),
            PropertyDeclarationSyntax prop => MemberIdentifierSyntax(prop.Identifier),
            EnumMemberDeclarationSyntax member => MemberIdentifierSyntax(member.Identifier),
            VariableDeclaratorSyntax variable
                when variable.Parent?.Parent is FieldDeclarationSyntax
                    or EventFieldDeclarationSyntax
                    => MemberIdentifierSyntax(variable.Identifier),
            VariableDeclaratorSyntax local => IdentifierSyntax(local.Identifier),
            ParameterSyntax positional
                when positional.Parent?.Parent is RecordDeclarationSyntax
                    => MemberIdentifierSyntax(positional.Identifier),
            ParameterSyntax param => IdentifierSyntax(param.Identifier),
            ForEachStatementSyntax forEach => IdentifierSyntax(forEach.Identifier),
            SingleVariableDesignationSyntax designation
                => IdentifierSyntax(designation.Identifier),
            _ => "",
        };
        return syntaxName.Length > 0;
    }

    // 全ての宣言に共通の規則。verbatim (@__x) も ValueText で比べる (emit と同じ)
    private static string IdentifierSyntax(SyntaxToken identifier)
    {
        var name = identifier.ValueText;
        if (name.StartsWith("__", StringComparison.Ordinal))
            return $"DoubleUnderscoreIdentifier({name})";
        if (name.Any(c => c > 127))
            return $"NonAsciiIdentifier({name})";
        return "";
    }

    private static string MemberIdentifierSyntax(SyntaxToken identifier)
    {
        var common = IdentifierSyntax(identifier);
        if (common.Length > 0) return common;
        return Array.IndexOf(ReservedClassKeys, identifier.ValueText) >= 0
            ? $"ReservedClassKey({identifier.ValueText})"
            : "";
    }

    private static string TypeIdentifierSyntax(SyntaxToken identifier)
    {
        var common = IdentifierSyntax(identifier);
        if (common.Length > 0) return common;
        return identifier.ValueText.IndexOf('_') >= 0
            ? $"UnderscoreTypeName({identifier.ValueText})"
            : "";
    }

    // `namespace A.B_C` は宣言 1 つに名前の部分が複数ある。先頭から順に見る
    private static string NamespaceNameSyntax(NameSyntax name)
    {
        foreach (var part in name.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var common = IdentifierSyntax(part.Identifier);
            if (common.Length > 0) return common;
            if (part.Identifier.ValueText.IndexOf('_') >= 0)
                return $"UnderscoreNamespaceName({part.Identifier.ValueText})";
        }
        return "";
    }
}

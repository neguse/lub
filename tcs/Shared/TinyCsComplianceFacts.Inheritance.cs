using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// class / record の継承は TinyC# の対象外 (q.md Q15)。多態は interface と型
// switch、組み合わせは field で書く。基底 class の宣言と、継承があって初めて
// 意味を持つ member (virtual / abstract / override / sealed / protected / new
// による隠蔽、base. と base(...)) を build を止める TCS1001 にする。interface の
// 実装 (`class C : IFoo`) はそのまま書ける。
// object の member の override (ToString / Equals / GetHashCode) も止める。Lua は
// ToString() を tostring に、文字列連結を `..` に、List.Contains を同一性に写し、
// user の override を呼ばない (継承を外した後の値の表示と比較は別に決める)。
// --ref の型 (host の宣言) は診断の対象の木に入らないので止めない
public static partial class TinyCsComplianceFacts
{
    public const string ClassInheritancePrefix = "ClassInheritance(";

    // 構文だけで決まる種類。他の TCS1001 に隠れないよう、
    // TryGetUnsupportedSyntax の switch の先頭で見る
    private static bool TryGetInheritanceSyntax(SyntaxNode node,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            BaseExpressionSyntax => "BaseMemberAccess",
            ConstructorInitializerSyntax init
                when init.IsKind(SyntaxKind.BaseConstructorInitializer)
                    => "BaseConstructorInitializer",
            ClassDeclarationSyntax abstractClass
                when abstractClass.Modifiers.Any(SyntaxKind.AbstractKeyword)
                    => "AbstractClass",
            RecordDeclarationSyntax abstractRecord
                when abstractRecord.IsKind(SyntaxKind.RecordDeclaration)
                    && abstractRecord.Modifiers.Any(SyntaxKind.AbstractKeyword)
                    => "AbstractClass",
            MemberDeclarationSyntax hiding
                when hiding is not BaseTypeDeclarationSyntax
                    && hiding.Modifiers.Any(SyntaxKind.NewKeyword)
                    => "NewMemberHiding",
            MemberDeclarationSyntax member
                when member is not BaseTypeDeclarationSyntax
                    && IsClassBody(member.Parent)
                    => InheritanceModifierName(member.Modifiers),
            _ => "",
        };
        return syntaxName.Length > 0;
    }

    private static string InheritanceModifierName(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.OverrideKeyword) ? "OverrideMember"
        : modifiers.Any(SyntaxKind.VirtualKeyword) ? "VirtualMember"
        : modifiers.Any(SyntaxKind.AbstractKeyword) ? "AbstractMember"
        : modifiers.Any(SyntaxKind.SealedKeyword) ? "SealedMember"
        : modifiers.Any(SyntaxKind.ProtectedKeyword) ? "ProtectedMember"
        : "";

    // class と record class の本体。struct の member は StructMember が、
    // interface の member は interface の規則が受け持つ
    private static bool IsClassBody(SyntaxNode? parent) =>
        parent is ClassDeclarationSyntax
        || parent is RecordDeclarationSyntax
        {
            RawKind: (int)SyntaxKind.RecordDeclaration,
        };

    // `class D : B` と `record D(...) : B(...)` の基底 class。interface は
    // 型を引かないと区別できないので semantic model で見る。analyzer は
    // SimpleBaseType / PrimaryConstructorBaseType の node action で呼ぶ
    public static bool TryGetClassInheritance(SyntaxNode node,
        SemanticModel model, out string syntaxName)
    {
        syntaxName = "";
        if (node is not BaseTypeSyntax { Parent: BaseListSyntax { Parent: { } owner } } baseType
            || !IsClassBody(owner)
            || model.GetTypeInfo(baseType.Type).Type is not
                { TypeKind: TypeKind.Class } baseClass
            || baseClass.SpecialType == SpecialType.System_Object)
            return false;
        syntaxName = $"{ClassInheritancePrefix}{baseClass.ToDisplayString()})";
        return true;
    }
}

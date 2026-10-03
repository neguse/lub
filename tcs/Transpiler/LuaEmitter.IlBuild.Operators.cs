using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: 単項演算 (++ / -- / - / ! / ~、T? は lifted)、配列 literal、
// with 式。式構築 (LuaEmitter.IlBuild.Expressions) のファイル長上限のため別 partial。
public partial class LuaEmitter
{
    private IlExpr? BuildArrayItems(SemanticModel model,
        InitializerExpressionSyntax? initializer, string? elementType = null)
    {
        if (initializer == null) return new IlTable([], elementType, IsArray: true);
        var items = new List<IlTableEntry>();
        foreach (var e in initializer.Expressions)
        {
            var built = BuildExpr(model, e);
            if (built == null) return null;
            items.Add(new IlTableEntry(null, built));
        }
        return new IlTable([.. items], elementType, IsArray: true);
    }

    private static IlExpr IntMinValueIl() =>
        new IlParen(new IlBin(IlBinOp.Sub,
            new IlUn(IlUnOp.Neg, new IlLit("2147483647")), new IlLit("1")));

    // const の IL literal。int.MinValue だけは式形 (上記)。float / double の
    // const は Type = "float" を付け、C backend が整数 literal に見える値
    // (2f 等) を F32 のまま扱えるようにする
    private static IlExpr LitFromConst(string text, ISymbol? symbol = null) =>
        text == "-2147483648" ? IntMinValueIl() : new IlLit(text, symbol switch
        {
            IFieldSymbol { Type.SpecialType: SpecialType.System_Single or SpecialType.System_Double }
                or ILocalSymbol { Type.SpecialType: SpecialType.System_Single or SpecialType.System_Double } => "float",
            _ => null,
        });

    private IlExpr? BuildWithExpr(SemanticModel model,
        WithExpressionSyntax withExpr)
    {
        var src = BuildExpr(model, withExpr.Expression);
        if (src == null) return null;
        var overrides = new List<(string, IlExpr)>();
        foreach (var assign in withExpr.Initializer.Expressions)
        {
            if (assign is not AssignmentExpressionSyntax
                { Left: IdentifierNameSyntax id } a)
                continue; // legacy も非対応 entry は黙って skip する
            var value = BuildExpr(model, a.Right);
            if (value == null) return null;
            overrides.Add((model.GetSymbolInfo(id).Symbol is { } overrideSym
                ? N(overrideSym) : N(id.Identifier.ValueText), value));
        }
        return new IlWith(src, [.. overrides]);
    }

    // legacy ResolveIdentifier の写像 (bare method group と custom property は
    // fallback、未解決 symbol も安全側で fallback)
    private IlExpr? BuildPrefixUnary(SemanticModel model,
        PrefixUnaryExpressionSyntax prefix)
    {
        var operand = BuildExpr(model, prefix.Operand);
        if (operand == null) return null;
        if (IsNullableValueType(model.GetTypeInfo(prefix.Operand).Type))
            return prefix.Kind() switch
            {
                SyntaxKind.UnaryMinusExpression => new IlLiftedUn(IlUnOp.Neg, operand),
                SyntaxKind.LogicalNotExpression => new IlLiftedUn(IlUnOp.Not, operand),
                SyntaxKind.BitwiseNotExpression => new IlLiftedUn(IlUnOp.BitNot, operand),
                SyntaxKind.PreIncrementExpression => new IlLiftedBin(IlLiftedOp.Add,
                    operand, new IlNullableWrap(new IlLit("1"), "int")),
                SyntaxKind.PreDecrementExpression => new IlLiftedBin(IlLiftedOp.Sub,
                    operand, new IlNullableWrap(new IlLit("1"), "int")),
                _ => null,
            };
        // `-2147483648` (int.MinValue) は literal 2147483648 が i32 に収まらない
        // ので (-2147483647 - 1) の形で両 backend に渡す
        if (prefix.IsKind(SyntaxKind.UnaryMinusExpression)
            && model.GetConstantValue(prefix) is { HasValue: true, Value: int.MinValue })
            return IntMinValueIl();
        return prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => new IlUn(IlUnOp.Neg, operand),
            SyntaxKind.LogicalNotExpression => new IlUn(IlUnOp.Not, operand),
            SyntaxKind.BitwiseNotExpression => new IlUn(IlUnOp.BitNot, operand),
            SyntaxKind.PreIncrementExpression =>
                new IlParen(new IlBin(IlBinOp.AddNum, operand, new IlLit("1"))),
            SyntaxKind.PreDecrementExpression =>
                new IlParen(new IlBin(IlBinOp.Sub, operand, new IlLit("1"))),
            _ => null,
        };
    }

    private IlExpr? BuildPostfixUnary(SemanticModel model,
        PostfixUnaryExpressionSyntax postfix)
    {
        var operand = BuildExpr(model, postfix.Operand);
        if (operand == null) return null;
        if (IsNullableValueType(model.GetTypeInfo(postfix.Operand).Type))
            return postfix.Kind() switch
            {
                SyntaxKind.PostIncrementExpression => new IlLiftedBin(IlLiftedOp.Add,
                    operand, new IlNullableWrap(new IlLit("1"), "int")),
                SyntaxKind.PostDecrementExpression => new IlLiftedBin(IlLiftedOp.Sub,
                    operand, new IlNullableWrap(new IlLit("1"), "int")),
                SyntaxKind.SuppressNullableWarningExpression => operand,
                _ => null,
            };
        return postfix.Kind() switch
        {
            SyntaxKind.PostIncrementExpression =>
                new IlBin(IlBinOp.AddNum, operand, new IlLit("1")),
            SyntaxKind.PostDecrementExpression =>
                new IlBin(IlBinOp.Sub, operand, new IlLit("1")),
            SyntaxKind.SuppressNullableWarningExpression => operand,
            _ => null,
        };
    }

}

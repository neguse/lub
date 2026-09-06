using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder の補間文字列。式構築 (LuaEmitter.IlBuild.Expressions) が
// ファイル長の上限に当たるので別 partial に置く。
public partial class LuaEmitter
{
    // legacy VisitInterpolatedString の写像 (alignment はリテラルのみ対応)
    private IlExpr? BuildInterpolatedString(SemanticModel model,
        InterpolatedStringExpressionSyntax interp)
    {
        var parts = new List<IlExpr>();
        foreach (var content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(new IlLit(EscapeLuaString(text.TextToken.ValueText
                        .Replace("{{", "{", StringComparison.Ordinal)
                        .Replace("}}", "}", StringComparison.Ordinal))));
                    break;
                case InterpolationSyntax hole:
                {
                    var inner = BuildExpr(model, hole.Expression);
                    if (inner == null) return null;
                    IlExpr rendered;
                    if (hole.FormatClause != null)
                    {
                        var luaFmt = ConvertFormatSpecifier(
                            hole.FormatClause.FormatStringToken.Text);
                        rendered = new IlCall("string.format",
                            [new IlLit($"\"{luaFmt}\""), inner]);
                    }
                    else
                    {
                        rendered = WrapFloatToString(model, hole.Expression,
                            inner);
                    }
                    if (hole.AlignmentClause != null)
                    {
                        if (hole.AlignmentClause.Value is not
                            (LiteralExpressionSyntax
                             or PrefixUnaryExpressionSyntax
                             {
                                 RawKind: (int)SyntaxKind.UnaryMinusExpression,
                                 Operand: LiteralExpressionSyntax
                             }))
                            return null;
                        var align = hole.AlignmentClause.Value.ToString();
                        rendered = new IlCall("string.format",
                            [new IlLit($"\"%{align}s\""), rendered]);
                    }
                    parts.Add(rendered);
                    break;
                }
                default:
                    return null;
            }
        }
        if (parts.Count == 0) return new IlLit("\"\"");
        var result = parts[0];
        for (var i = 1; i < parts.Count; i++)
            result = new IlBin(IlBinOp.Concat, result, parts[i]);
        // `..` 連接は atomic でない (IlLen / IlField が先頭要素にだけ結合する)
        // ため、receiver 位置でも安全なように IlParen で閉じる
        return parts.Count == 1 ? result : new IlParen(result);
    }
}

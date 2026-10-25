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
        // raw 補間 ($"""...""") の text は ValueText が既に literal の内容
        // ($ の個数より短い brace の連続は文字のまま)。{{ / }} の escape は
        // 通常 / verbatim の補間だけ
        var raw = interp.StringStartToken.Kind() is
            SyntaxKind.InterpolatedSingleLineRawStringStartToken
            or SyntaxKind.InterpolatedMultiLineRawStringStartToken;
        foreach (var content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(new IlLit(EscapeLuaString(raw
                        ? text.TextToken.ValueText
                        : text.TextToken.ValueText
                            .Replace("{{", "{", StringComparison.Ordinal)
                            .Replace("}}", "}", StringComparison.Ordinal))));
                    break;
                case InterpolationSyntax hole:
                {
                    var inner = BuildExpr(model, hole.Expression);
                    if (inner == null) return null;
                    IlExpr rendered;
                    // bool / string / char は IFormattable でなく、C# は書式を使わない
                    var formatClause = UnwrapNullable(model.GetTypeInfo(hole.Expression).Type)?.SpecialType
                        is SpecialType.System_Boolean or SpecialType.System_String or SpecialType.System_Char
                        ? null : hole.FormatClause;
                    if (formatClause != null
                        && WrapEnumFormat(model.GetTypeInfo(hole.Expression).Type, inner,
                            formatClause.FormatStringToken.ValueText) is
                            IlCall { Callee: "String.FormatEnum" } enumName)
                        rendered = enumName;
                    else if (formatClause != null
                        && WrapFloatFormat(model.GetTypeInfo(hole.Expression).Type, inner,
                            formatClause.FormatStringToken.ValueText) is { } floatText)
                        rendered = floatText;
                    else if (formatClause != null)
                    {
                        var luaFmt = ConvertFormatSpecifier(
                            formatClause.FormatStringToken.Text);
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

    // float (と float?) の G / E 書式は .NET の float.ToString(format) と同じ文字列にする (String.FormatFloat、il-spec §13)。
    // 精度は .NET の標準書式と同じく 9 桁までの数で、"G03" は "G3" と同じなので数に直して渡す
    private static IlExpr? WrapFloatFormat(ITypeSymbol? type, IlExpr value, string format)
    {
        if (!IsFloatingType(type) || format.Length is 0 or > 10
            || "GgEe".IndexOf(format[0]) < 0 || !format.Skip(1).All(char.IsAsciiDigit))
            return null;
        var spec = format.Length == 1 ? format
            : format[0] + int.Parse(format[1..], System.Globalization.CultureInfo.InvariantCulture)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new IlCall("String.FormatFloat", [value, new IlLit(EscapeLuaString(spec))]);
    }
}

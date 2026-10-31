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
                    // bool / string / char は IFormattable でなく、C# は書式を使わない。Rune は IFormattable だが書式を無視する
                    var holeType = UnwrapNullable(model.GetTypeInfo(hole.Expression).Type);
                    var formatClause = IsRuneType(holeType) || TinyCsComplianceFacts.HasToStringOverride(holeType)
                        || holeType?.SpecialType
                        is SpecialType.System_Boolean or SpecialType.System_String or SpecialType.System_Char
                        ? null : hole.FormatClause;
                    if (formatClause != null
                        && WrapEnumFormat(model.GetTypeInfo(hole.Expression).Type, inner,
                            formatClause.FormatStringToken.ValueText) is
                            IlCall { Callee: "String.FormatEnum" } enumName)
                        rendered = enumName;
                    else if (formatClause != null
                        && WrapNumberFormat(model.GetTypeInfo(hole.Expression).Type, inner,
                            formatClause.FormatStringToken.ValueText) is { } numberText)
                        rendered = numberText;
                    else if (formatClause != null)
                    {
                        var luaFmt = ConvertFormatSpecifier(
                            formatClause.FormatStringToken.ValueText);
                        rendered = new IlCall("string.format",
                            [new IlLit(EscapeLuaString(luaFmt)), inner]);
                    }
                    else
                    {
                        rendered = WrapFloatToString(model, hole.Expression,
                            inner);
                    }
                    if (hole.AlignmentClause != null)
                    {
                        if (model.GetConstantValue(hole.AlignmentClause.Value)
                            is not { HasValue: true, Value: int align })
                            return null;
                        rendered = new IlCall("string.format",
                            [new IlLit(EscapeLuaString(string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"%{align}s"))), rendered]);
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

    // 数値 (と T?) の標準書式は .NET の ToString(format) と同じ文字列にする (String.FormatFloat / String.FormatInt、il-spec §13)。
    // 精度は .NET の標準書式と同じく 9 桁までの数で、"G03" は "G3" と同じなので数に直して渡す。
    // R は .NET でも G と同じで、float は精度を使わない。float の D / X は .NET でも FormatException で対象外
    private static IlExpr? WrapNumberFormat(ITypeSymbol? type, IlExpr value, string format)
    {
        var isFloat = IsFloatingType(type);
        if (!isFloat && !IsIntegralType(type) || format.Length is 0 or > 10
            || !format.Skip(1).All(char.IsAsciiDigit))
            return null;
        var kind = format[0] switch { 'R' => 'G', 'r' => 'g', var c => c };
        if ((isFloat ? "GgEeFfNn" : "GgEeFfNnDdXx").IndexOf(kind) < 0) return null;
        var spec = format.Length == 1 || (isFloat && kind != format[0]) ? kind.ToString()
            : kind + int.Parse(format[1..], System.Globalization.CultureInfo.InvariantCulture)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new IlCall(isFloat ? "String.FormatFloat" : "String.FormatInt",
            [value, new IlLit(EscapeLuaString(spec))]);
    }
}

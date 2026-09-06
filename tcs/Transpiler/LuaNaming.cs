using System.Text;
using Microsoft.CodeAnalysis;

namespace TinyCs;

/// <summary>
/// Lua 出力の名前規則。C# 名 (PascalCase / camelCase) を中立表記の lowerCamel
/// にしてから Lua の snake_case に落とす。表は持たず、規則だけで写す。
///   member:  BeginPass → begin_pass、ColorEdit3 → color_edit3、_hp → _hp
///   const:   Depth24Stencil8 → DEPTH24_STENCIL8 (enum メンバ)
///   keyword: End → end_ (Lua の予約語には `_` を後置)
/// 参照専用型 (--ref) の static アクセスは入れ子の型名を全小文字で `.` 結合し
/// (Lub.Gfx → lub.gfx)、入れ子 enum は親の下に平らに置く
/// (Lub.Gfx.PixelFormat.Rgba8 → lub.gfx.RGBA8)。ユーザ型の型名は写さない。
/// </summary>
public static class LuaNaming
{
    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for",
        "function", "goto", "if", "in", "local", "nil", "not", "or", "repeat",
        "return", "then", "true", "until", "while",
    };

    public static bool IsLuaKeyword(string name) => LuaKeywords.Contains(name);

    public static string Member(string name)
    {
        var snake = ToSnake(name);
        return LuaKeywords.Contains(snake) ? snake + "_" : snake;
    }

    public static string Const(string name) => ToSnake(name).ToUpperInvariant();

    /// <summary>symbol の種類で member / const を選ぶ (enum メンバは const)。
    /// source に宣言の無い symbol (BCL / TinySystem の metadata) は runtime 側の
    /// 名前で呼ぶので写さない。</summary>
    public static string MemberName(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0) return symbol.Name;
        return symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum }
            ? Const(symbol.Name)
            : Member(symbol.Name);
    }

    /// <summary>参照専用型の Lua 側パス。</summary>
    public static string RefTypePath(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        var cur = type.TypeKind == TypeKind.Enum && type.ContainingType != null
            ? type.ContainingType
            : type;
        for (; cur != null; cur = cur.ContainingType)
            parts.Add(cur.Name.ToLowerInvariant());
        parts.Reverse();
        return string.Join(".", parts);
    }

    private static string ToSnake(string name)
    {
        // 小文字を含まない名前 (CLEAR / DEPTH24_STENCIL8) は既に snake_case の
        // 全大文字とみなし、そのまま小文字化する
        if (!name.Any(char.IsLower)) return name.ToLowerInvariant();
        var sb = new StringBuilder(name.Length + 4);
        var i = 0;
        while (i < name.Length && name[i] == '_')
            sb.Append(name[i++]);
        var first = true;
        for (; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (!first) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
            first = false;
        }
        return sb.ToString();
    }
}

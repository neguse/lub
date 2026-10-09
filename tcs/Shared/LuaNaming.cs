using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

/// <summary>
/// Lua 出力の名前規則。C# 名 (PascalCase / camelCase) を中立表記の lowerCamel
/// にしてから Lua の snake_case に落とす。表は持たず、規則だけで写す。
///   member:  BeginPass → begin_pass、ColorEdit3 → color_edit3、_hp → _hp
///   const:   Depth24Stencil8 → DEPTH24_STENCIL8 (enum メンバ)
///   keyword: End → end_ (Lua の予約語には `_` を後置)
/// 参照専用型 (--ref) の static アクセスは namespace と入れ子の型名を全小文字で
/// `.` 結合し (Lub.Gfx → lub.gfx)、enum は入っている型か namespace の下に平らに
/// 置く (Lub.Gfx.PixelFormat.Rgba8 → lub.gfx.RGBA8、namespace Lub の EventKind.Quit
/// → lub.QUIT)。予約語になる区切りは member と同じく `_` を後置する (End → end_)。
/// ユーザ型の型名は写さない。
/// </summary>
public static class LuaNaming
{
    private static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for",
        "function", "global", "goto", "if", "in", "local", "nil", "not", "or",
        "repeat", "return", "then", "true", "until", "while",
    };

    public static bool IsLuaKeyword(string name) => LuaKeywords.Contains(name);

    public static string Member(string name)
    {
        var snake = ToSnake(name);
        return LuaKeywords.Contains(snake) ? snake + "_" : snake;
    }

    public static string Const(string name) => ToSnake(name).ToUpperInvariant();

    /// <summary>user-defined operator の Lua 関数名 (class table 上の static
    /// 関数)。metamethod 名 (`__add` 等) を基本とし、同じ operator の overload
    /// は宣言順に `__mul_1` `__mul_2` … と別名にする。Lua 出力 / IlExport /
    /// 呼び出し箇所 (`V.__mul_2(a, b)`) で共通。対象外の operator は null。</summary>
    public static string? OperatorName(OperatorDeclarationSyntax op)
    {
        if (!TinyCsComplianceFacts.TryGetOperatorMetamethod(op, out var metamethod))
            return null;
        if (op.Parent is not TypeDeclarationSyntax owner) return metamethod;
        var overloads = owner.Members.OfType<OperatorDeclarationSyntax>()
            .Where(o => TinyCsComplianceFacts.TryGetOperatorMetamethod(o, out var m)
                && m == metamethod)
            .ToList();
        return overloads.Count == 1
            ? metamethod
            : $"{metamethod}_{overloads.IndexOf(op) + 1}";
    }

    /// <summary>symbol 版。source に宣言の無い operator (BCL metadata) は null。</summary>
    public static string? OperatorName(IMethodSymbol op) =>
        op.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is OperatorDeclarationSyntax decl ? OperatorName(decl) : null;

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
        ISymbol cur = type;
        if (type.TypeKind == TypeKind.Enum)
        {
            if (type.ContainingType != null)
                cur = type.ContainingType;
            else if (type.ContainingNamespace is { IsGlobalNamespace: false } ns)
                cur = ns;
        }
        var parts = new List<string>();
        for (; cur is INamedTypeSymbol t;
            cur = (ISymbol?)t.ContainingType ?? t.ContainingNamespace)
            parts.Add(PathSegment(t.Name));
        for (; cur is INamespaceSymbol { IsGlobalNamespace: false } n;
            cur = n.ContainingNamespace)
            parts.Add(PathSegment(n.Name));
        parts.Reverse();
        return string.Join(".", parts);
    }

    private static string PathSegment(string name)
    {
        var lower = name.ToLowerInvariant();
        return LuaKeywords.Contains(lower) ? lower + "_" : lower;
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

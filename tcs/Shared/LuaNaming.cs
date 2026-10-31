using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

/// <summary>
/// Lua 出力の名前規則。表は持たず、規則だけで写す。
/// user の型 (入力の tree に宣言された型) の member は C# の識別子をそのまま
/// Lua / IL の名前にする (BeginPass → BeginPass、hp → hp、enum の BigValue →
/// BigValue)。Lua の予約語と同じ綴りの member は宣言で error (TCS1001) になる。
/// 参照専用型 (--ref、host) の member は host の wire format の snake_case に写す:
///   member:  BeginPass → begin_pass、ColorEdit3 → color_edit3、_hp → _hp
///   const:   Depth24Stencil8 → DEPTH24_STENCIL8 (enum メンバ)
///   keyword: End → end_ (Lua の予約語には `_` を後置)
/// 参照専用型の static アクセスは namespace と入れ子の型名を全小文字で
/// `.` 結合し (Lub.Gfx → lub.gfx)、enum は入っている型か namespace の下に平らに
/// 置く (Lub.Gfx.PixelFormat.Rgba8 → lub.gfx.RGBA8、namespace Lub の EventKind.Quit
/// → lub.QUIT)。予約語になる区切りは member と同じく `_` を後置する (End → end_)。
/// host の規則 (Member / Const / RefTypePath / IsLuaKeyword) は lub の lub-gen も
/// binding の名前に使うので変えない。
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

    /// <summary>参照専用型 (host) の member 名。</summary>
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

    /// <summary>参照専用型 (host) の member symbol の Lua 側の名前。symbol の種類で
    /// member / const を選ぶ (enum メンバは const)。source に宣言の無い symbol
    /// (BCL / TinySystem の metadata) は runtime 側の名前で呼ぶので写さない。</summary>
    public static string MemberName(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0) return symbol.Name;
        return symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum }
            ? Const(symbol.Name)
            : Member(symbol.Name);
    }

    /// <summary>user の型の method の Lua / IL 名。同じ型に同名の method が複数ある
    /// (overload) ときだけ parameter の型を `__` でつないで付ける
    /// (`Sum(List&lt;T&gt;, Func&lt;T, int&gt;)` → `Sum__List_T__Func_T_int`)。
    /// parameter の無い overload は C# の名前のまま。interface の method を実装する
    /// method は interface の method の名前にする (interface 経由の呼び出しは名前で
    /// dispatch する)。宣言と呼び出しの両方がこの名前を使い、他の member と衝突する
    /// 綴りは TCS1001 MethodOverload。</summary>
    public static string MethodName(IMethodSymbol method)
    {
        var m = (method.ReducedFrom ?? method).OriginalDefinition;
        if (m.MethodKind != MethodKind.Ordinary) return m.Name;
        if (ImplementedInterfaceMethods(m).FirstOrDefault() is { } implemented)
            return MethodName(implemented);
        if (m.ContainingType.GetMembers(m.Name).OfType<IMethodSymbol>()
                .Count(x => x.MethodKind == MethodKind.Ordinary) < 2)
            return m.Name;
        var sb = new StringBuilder(m.Name);
        foreach (var p in m.Parameters)
        {
            sb.Append("__");
            if (p.RefKind != RefKind.None)
                sb.Append(p.RefKind.ToString().ToLowerInvariant()).Append('_');
            sb.Append(TypeToken(p.Type));
        }
        return sb.ToString();
    }

    /// <summary>user の型の constructor の Lua / IL 名。class は `new`、struct は `ctor`
    /// (struct の `new` は ctor を通らない zero 値)。constructor が複数ある型は、引数のある
    /// ものに method の overload と同じ規則で parameter の型を付ける
    /// (`new__int__float`)。宣言と呼び出しの両方がこの名前を使う。</summary>
    public static string CtorName(IMethodSymbol ctor)
    {
        var m = ctor.OriginalDefinition;
        var name = m.ContainingType.TypeKind == TypeKind.Struct ? "ctor" : "new";
        var explicitCtors = m.ContainingType.InstanceConstructors.Count(c =>
            c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ConstructorDeclarationSyntax));
        if (explicitCtors < 2 || m.Parameters.Length == 0) return name;
        return name + string.Concat(m.Parameters.Select(p => "__" + TypeToken(p.Type)));
    }

    /// <summary>instance method が実装する interface の method (宣言の型の interface から)。</summary>
    public static IEnumerable<IMethodSymbol> ImplementedInterfaceMethods(IMethodSymbol method)
    {
        if (method.IsStatic || method.ContainingType is not { TypeKind: not TypeKind.Interface } type)
            yield break;
        foreach (var iface in type.AllInterfaces)
            foreach (var candidate in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
                if (SymbolEqualityComparer.Default.Equals(
                        type.FindImplementationForInterfaceMember(candidate)?.OriginalDefinition,
                        method))
                    yield return candidate.OriginalDefinition;
    }

    // 入れ子の型は外側の型 (型引数を含む) も名前に入れる (`Dictionary<TKey, int>.ValueCollection`)
    private static readonly SymbolDisplayFormat TokenFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static string TypeToken(ITypeSymbol type)
    {
        var text = type.ToDisplayString(TokenFormat)
            .Replace("[]", "_arr").Replace("?", "_opt");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                sb.Append('_');
        }
        return sb.ToString().TrimEnd('_');
    }

    private static bool IsTupleOriginal(this INamedTypeSymbol type) =>
        type is { Name: "ValueTuple", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } };

    /// <summary>source に宣言された型の Lua global 名。入れ子の型は外側の型の名前を、
    /// namespace の中の型は namespace 修飾名を `_` で結合し (`Outer.Inner` → `Outer_Inner`、
    /// `Game.Gfx.Color` → `Game_Gfx_Color`)、どちらでもない型は simple 名。</summary>
    public static string TypeGlobalName(INamedTypeSymbol type)
    {
        // System.ValueTuple は型引数の数ごとに別の型で、Lua の global も分ける
        var parts = new List<string>
        {
            type.OriginalDefinition.IsTupleOriginal()
                ? $"ValueTuple{type.Arity}" : type.Name,
        };
        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType)
            parts.Add(outer.Name);
        // ToDisplayString は keyword の namespace を `@event` と逃がすので Name をつなぐ
        for (var ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false };
            ns = ns.ContainingNamespace)
            parts.Add(ns.Name);
        parts.Reverse();
        return string.Join("_", parts);
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

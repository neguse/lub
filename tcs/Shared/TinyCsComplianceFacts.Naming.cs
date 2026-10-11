using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// Lua 名の写像で衝突するメンバ (`flash` と `Flash`) と型 (`A_B.C` と `A.B.C`)
// の判定。analyzer / `tcs check` は TCS1001 LuaNameCollision、transpiler は
// error として使う。
public static partial class TinyCsComplianceFacts
{
    /// <summary>同じ型 (class / record) の中で、写像後の Lua 名が先に宣言された
    /// 別名メンバと衝突するか。field / property / method / record の positional
    /// parameter が対象。最初の宣言は衝突しない (MethodOverload と同じ方針)。</summary>
    public static bool TryGetLuaNameCollision(SyntaxNode node,
        out string previousName, out string name, out string luaName)
    {
        previousName = name = luaName = "";
        if (!TryGetLuaMemberIdentifier(node, out var owner, out var token))
            return false;
        name = token.ValueText;
        luaName = LuaNaming.Member(name);
        foreach (var (otherName, otherToken) in EnumerateLuaMemberNames(owner!))
        {
            if (otherToken == token) return false;
            if (otherName == name) continue; // overload は同名で衝突しない
            if (LuaNaming.Member(otherName) != luaName) continue;
            previousName = otherName;
            return true;
        }
        return false;
    }

    /// <summary>tree 内の Lua 名衝突を transpile error の書式で列挙する。</summary>
    public static IEnumerable<string> AnalyzeLuaNameCollisions(SyntaxTree tree)
    {
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (!TryGetLuaNameCollision(node, out var prev, out var name,
                    out var lua))
                continue;
            TryGetLuaMemberIdentifier(node, out _, out var token);
            yield return FormatCollisionError(token.GetLocation(),
                $"LuaNameCollision({prev}/{name})",
                $"'{prev}' and '{name}' both map to Lua '{lua}'");
        }
    }

    /// <summary>trees に宣言された型のうち、Lua global 名
    /// (<see cref="LuaNaming.TypeGlobalName"/>) が先に宣言された別の型と同じに
    /// なるもの。namespace 修飾名は `_` で結合するので `A_B.C` と `A.B.C` は共に
    /// `A_B_C` になり、Lua では後勝ちで片方が消える。宣言順は trees の順と tree
    /// 内の位置。interface / delegate は Lua の global を持たず、入れ子の型は
    /// NestedTypeDeclaration の領分なので対象外。</summary>
    public static IEnumerable<(INamedTypeSymbol Type, Location Location,
        string SyntaxName, string Detail)> FindLuaTypeNameCollisions(
            Compilation compilation, IReadOnlyList<SyntaxTree> trees)
    {
        var order = new Dictionary<SyntaxTree, int>();
        for (var i = 0; i < trees.Count; i++) order[trees[i]] = i;
        var types = new List<(INamedTypeSymbol Type, SyntaxReference Decl)>();
        var namespaces = new Stack<INamespaceSymbol>();
        namespaces.Push(compilation.Assembly.GlobalNamespace);
        while (namespaces.Count > 0)
        {
            var ns = namespaces.Pop();
            foreach (var child in ns.GetNamespaceMembers()) namespaces.Push(child);
            foreach (var type in ns.GetTypeMembers())
            {
                if (type.IsImplicitlyDeclared
                    || type.TypeKind is TypeKind.Interface or TypeKind.Delegate)
                    continue;
                var decl = type.DeclaringSyntaxReferences
                    .Where(r => order.ContainsKey(r.SyntaxTree))
                    .OrderBy(r => order[r.SyntaxTree]).ThenBy(r => r.Span.Start)
                    .FirstOrDefault();
                if (decl != null) types.Add((type, decl));
            }
        }
        var first = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        foreach (var (type, decl) in types
            .OrderBy(t => order[t.Decl.SyntaxTree]).ThenBy(t => t.Decl.Span.Start))
        {
            var lua = LuaNaming.TypeGlobalName(type);
            if (!first.TryGetValue(lua, out var previous))
            {
                first[lua] = type;
                continue;
            }
            var prev = previous.ToDisplayString();
            var name = type.ToDisplayString();
            var location = decl.GetSyntax() is BaseTypeDeclarationSyntax declaration
                ? declaration.Identifier.GetLocation()
                : Location.Create(decl.SyntaxTree, decl.Span);
            yield return (type, location, $"LuaNameCollision({prev}/{name})",
                $"'{prev}' and '{name}' both map to Lua '{lua}'");
        }
    }

    /// <summary>型の Lua global 名の衝突を、報告先の tree と transpile error の
    /// 書式の組で列挙する。</summary>
    public static IEnumerable<(SyntaxTree Tree, string Error)>
        AnalyzeLuaTypeNameCollisions(Compilation compilation,
            IReadOnlyList<SyntaxTree> trees) =>
        FindLuaTypeNameCollisions(compilation, trees).Select(c =>
            (c.Location.SourceTree!,
                FormatCollisionError(c.Location, c.SyntaxName, c.Detail)));

    private static string FormatCollisionError(Location location,
        string syntaxName, string detail)
    {
        var loc = location.GetLineSpan();
        var file = string.IsNullOrEmpty(loc.Path) ? "" : loc.Path;
        // 書式は他の TCS1001 と同じ (`error TCS1001: unsupported syntax: ...`)。
        // 増分 session は `): error ` で error を見分け、spec conformance の
        // 分類は TCS100x をサブセット外の報告 (Diag) として数える
        return $"{file}({loc.StartLinePosition.Line + 1}," +
            $"{loc.StartLinePosition.Character + 1}): error " +
            $"{TinyCsDiagnosticIds.UnsupportedSyntax}: unsupported syntax: " +
            $"{syntaxName}: {detail}";
    }

    private static bool TryGetLuaMemberIdentifier(SyntaxNode node,
        out TypeDeclarationSyntax? owner, out SyntaxToken identifier)
    {
        (owner, identifier) = node switch
        {
            VariableDeclaratorSyntax
            {
                Parent.Parent: FieldDeclarationSyntax { Parent: var o }
            } v => (o as TypeDeclarationSyntax, v.Identifier),
            PropertyDeclarationSyntax { Parent: var o } p
                => (o as TypeDeclarationSyntax, p.Identifier),
            MethodDeclarationSyntax { Parent: var o } m
                => (o as TypeDeclarationSyntax, m.Identifier),
            ParameterSyntax
            {
                Parent: ParameterListSyntax { Parent: RecordDeclarationSyntax o }
            } param => (o, param.Identifier),
            _ => (null, default),
        };
        return owner is ClassDeclarationSyntax or RecordDeclarationSyntax;
    }

    private static IEnumerable<(string Name, SyntaxToken Token)>
        EnumerateLuaMemberNames(TypeDeclarationSyntax type)
    {
        if (type is RecordDeclarationSyntax { ParameterList: not null } rec)
            foreach (var p in rec.ParameterList.Parameters)
                yield return (p.Identifier.ValueText, p.Identifier);
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                        yield return (v.Identifier.ValueText, v.Identifier);
                    break;
                case PropertyDeclarationSyntax prop:
                    yield return (prop.Identifier.ValueText, prop.Identifier);
                    break;
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.ValueText, method.Identifier);
                    break;
            }
        }
    }
}

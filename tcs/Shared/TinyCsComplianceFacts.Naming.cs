using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// Lua 名の写像で衝突するメンバの判定。user の member は C# の識別子のままなので、
// 衝突するのは Lua の予約語に `_` を後置した名前と同じ綴り (`end` と `end_`) だけ。
// analyzer / `tcs check` は TCS1001 LuaNameCollision、transpiler は error として使う。
public static partial class TinyCsComplianceFacts
{
    public const string RefInterfaceImplementationPrefix = "RefInterfaceImplementation(";

    /// <summary>user の型の base list にある `--ref` (host) の interface。host の
    /// member は snake_case、user の実装は C# の名前なので、interface 経由の呼び出し
    /// (`h:do_thing()`) が実装 (`Impl:DoThing`) に届かない。build を止める。
    /// host の interface は、宣言が参照専用の tree にあるもの (isReferenceTree) か、
    /// source の無い metadata で runtime (TinySystem / BCL) 以外の assembly のもの。
    /// analyzer は SimpleBaseType の node action で呼ぶ (参照専用の tree は無い)。</summary>
    public static bool TryGetRefInterfaceImplementation(SyntaxNode node,
        SemanticModel model, Func<SyntaxTree, bool>? isReferenceTree,
        out string syntaxName)
    {
        syntaxName = "";
        if (node is not BaseTypeSyntax { Parent: BaseListSyntax { Parent: TypeDeclarationSyntax } } baseType
            || model.GetTypeInfo(baseType.Type).Type is not
                INamedTypeSymbol { TypeKind: TypeKind.Interface } iface
            || !IsHostInterface(iface, isReferenceTree))
            return false;
        syntaxName = $"{RefInterfaceImplementationPrefix}{iface.ToDisplayString()})";
        return true;
    }

    private static bool IsHostInterface(INamedTypeSymbol iface,
        Func<SyntaxTree, bool>? isReferenceTree)
    {
        var declarations = iface.OriginalDefinition.DeclaringSyntaxReferences;
        if (declarations.Length > 0)
            return isReferenceTree != null
                && declarations.All(r => isReferenceTree(r.SyntaxTree));
        var assembly = iface.ContainingAssembly?.Name ?? "";
        return assembly != RuntimeRootGlobal
            && !assembly.StartsWith("System", StringComparison.Ordinal)
            && assembly != "mscorlib" && assembly != "netstandard";
    }

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
        luaName = LuaNaming.UserMember(name);
        foreach (var (otherName, otherToken) in EnumerateLuaMemberNames(owner!))
        {
            if (otherToken == token) return false;
            if (otherName == name) continue; // overload は同名で衝突しない
            if (LuaNaming.UserMember(otherName) != luaName) continue;
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

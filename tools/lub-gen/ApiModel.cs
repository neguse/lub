using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TinyCs;

namespace LubGen;

// cs-lib/lub_stub.cs から読んだ API 面。名前は C# 名 (中立表記) と、tcs の
// LuaNaming で導いた Lua 側の名前を両方持つ。C 名は段階 3 で足す。
public sealed record ApiParam(string Name, string Type, bool Optional, bool IsOut);

public sealed record ApiFunction(string Name, string LuaName, string Return,
    IReadOnlyList<ApiParam> Params, string Doc);

public sealed record ApiEnumMember(string Name, string LuaName, int Value);

public sealed record ApiEnum(string Name, string LuaPath,
    IReadOnlyList<ApiEnumMember> Members, string Doc);

public sealed record ApiField(string Name, string LuaName, string Type,
    bool Optional, string Doc);

public sealed record ApiNamespace(string Name, string LuaPath,
    IReadOnlyList<ApiFunction> Functions, IReadOnlyList<ApiEnum> Enums,
    IReadOnlyList<ApiField> StaticFields, string Doc);

/// <summary>Kind: handle (runtime 所有の不透明参照) / view (frame 有効の
/// バイト列) / record (option table や戻り値の平らな構造)。</summary>
public sealed record ApiType(string Name, string Kind,
    IReadOnlyList<ApiField> Fields, IReadOnlyList<ApiFunction> Methods, string Doc);

public sealed record ApiModel(IReadOnlyList<ApiNamespace> Namespaces,
    IReadOnlyList<ApiType> Types);

public static class ApiModelLoader
{
    public const string RootClass = "Lub";

    public static ApiModel Load(string stubPath, out IReadOnlyList<string> errors)
    {
        var text = File.ReadAllText(stubPath);
        var tree = CSharpSyntaxTree.ParseText(text, path: stubPath);
        var compilation = CSharpCompilation.Create("lub-stub", [tree],
            Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var diags = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString()).ToList();
        errors = diags;

        var global = compilation.GlobalNamespace;
        var root = global.GetTypeMembers(RootClass).FirstOrDefault()
            ?? throw new InvalidOperationException($"root class {RootClass} not found");

        var namespaces = new List<ApiNamespace>
        {
            LoadNamespace(root, includeNested: false),
        };
        foreach (var nested in root.GetTypeMembers()
                     .Where(t => t.TypeKind == TypeKind.Class && t.IsStatic)
                     .OrderBy(t => t.Locations[0].SourceSpan.Start))
            namespaces.Add(LoadNamespace(nested, includeNested: true));

        var types = new List<ApiType>();
        foreach (var t in global.GetTypeMembers()
                     .Where(t => t.TypeKind == TypeKind.Class && t.Name != RootClass
                         && !t.Name.EndsWith("Attribute", StringComparison.Ordinal))
                     .OrderBy(t => t.Locations[0].SourceSpan.Start))
            types.Add(LoadType(t));
        return new ApiModel(namespaces, types);
    }

    private static ApiNamespace LoadNamespace(INamedTypeSymbol cls, bool includeNested)
    {
        var luaPath = LuaNaming.RefTypePath(cls);
        var functions = cls.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.IsStatic
                && m.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadFunction).ToList();
        var enums = includeNested
            ? cls.GetTypeMembers().Where(t => t.TypeKind == TypeKind.Enum)
                .Select(LoadEnum).ToList()
            : [];
        var fields = cls.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.IsStatic && !f.IsConst
                && f.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadField).ToList();
        return new ApiNamespace(cls.Name, luaPath, functions, enums, fields, Doc(cls));
    }

    private static ApiEnum LoadEnum(INamedTypeSymbol e)
    {
        var members = e.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => new ApiEnumMember(f.Name, LuaNaming.Const(f.Name),
                Convert.ToInt32(f.ConstantValue)))
            .ToList();
        return new ApiEnum(e.Name, LuaNaming.RefTypePath(e), members, Doc(e));
    }

    private static ApiType LoadType(INamedTypeSymbol t)
    {
        var kind = t.GetAttributes()
            .Select(a => a.AttributeClass?.Name)
            .FirstOrDefault(n => n is "LubHandleAttribute" or "LubViewAttribute") switch
        {
            "LubHandleAttribute" => "handle",
            "LubViewAttribute" => "view",
            _ => "record",
        };
        var fields = t.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && f.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadField).ToList();
        var methods = t.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic
                && m.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadFunction).ToList();
        return new ApiType(t.Name, kind, fields, methods, Doc(t));
    }

    private static ApiFunction LoadFunction(IMethodSymbol m)
    {
        var ps = m.Parameters.Select(p => new ApiParam(p.Name,
            p.Type.ToDisplayString(), p.HasExplicitDefaultValue,
            p.RefKind == RefKind.Out)).ToList();
        return new ApiFunction(m.Name, LuaNaming.Member(m.Name),
            m.ReturnType.ToDisplayString(), ps, Doc(m));
    }

    private static ApiField LoadField(IFieldSymbol f) =>
        new(f.Name, LuaNaming.Member(f.Name), f.Type.ToDisplayString(),
            f.NullableAnnotation == NullableAnnotation.Annotated, Doc(f));

    private static readonly Regex SummaryRegex = new(
        @"<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string Doc(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml() ?? "";
        var m = SummaryRegex.Match(xml);
        if (!m.Success) return "";
        var body = TagRegex.Replace(m.Groups[1].Value, "");
        return Regex.Replace(body, @"\s+", " ").Trim();
    }
}

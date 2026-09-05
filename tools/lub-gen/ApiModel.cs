using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TinyCs;

namespace LubGen;

// cs-lib/lub_stub.cs から読んだ API 面。名前は C# 名 (中立表記) と、tcs の
// LuaNaming で導いた Lua 側の名前を両方持つ。C の名前と型は CHeader が規則で
// 導く (docs/log/2026-09-05-language-architecture-plan.md の段階 4)。

public enum LubTypeKind
{
    Void, Int, Double, Bool, String, Enum, Handle, Keyed, View, Record, List,
    Array, Func, Dict,
}

/// <summary>stub の型。Name は enum なら "Gfx.LoadAction"、class なら
/// class 名。List / Array は Elem、Func は FuncParams と FuncReturn。</summary>
public sealed record TypeRef(LubTypeKind Kind, string Name, bool Nullable,
    TypeRef? Elem, IReadOnlyList<TypeRef>? FuncParams, TypeRef? FuncReturn)
{
    public static readonly TypeRef VoidType = new(LubTypeKind.Void, "void", false, null, null, null);

    public TypeRef AsNullable() => this with { Nullable = true };

    [JsonIgnore] public bool IsScalar => Kind is LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum;

    public override string ToString() => Kind switch
    {
        LubTypeKind.List => $"List<{Elem}>" + (Nullable ? "?" : ""),
        LubTypeKind.Array => $"{Elem}[]" + (Nullable ? "?" : ""),
        LubTypeKind.Func => $"Func<{string.Join(", ", FuncParams!)}, {FuncReturn}>" + (Nullable ? "?" : ""),
        _ => Name + (Nullable ? "?" : ""),
    };
}

public sealed record ApiParam(string Name, string LuaName, TypeRef Type, bool Optional,
    bool IsOut, int? ArrayLen);

public sealed record ApiFunction(string Name, string LuaName, TypeRef Return,
    IReadOnlyList<ApiParam> Params, string Doc, bool NoFail, bool Maybe, bool NoC);

public sealed record ApiEnumMember(string Name, string LuaName, int Value, string Doc);

public sealed record ApiEnum(string Name, string Namespace, string LuaPath,
    IReadOnlyList<ApiEnumMember> Members, string Doc, bool LuaString);

public sealed record ApiField(string Name, string LuaName, TypeRef Type,
    bool Optional, string Doc, int? ArrayLen, bool Bits);

public sealed record ApiConst(string Name, string LuaName, object Value, string Doc);

public sealed record ApiNamespace(string Name, string LuaPath,
    IReadOnlyList<ApiFunction> Functions, IReadOnlyList<ApiEnum> Enums,
    IReadOnlyList<ApiField> StaticFields, IReadOnlyList<ApiConst> Consts, string Doc);

/// <summary>Kind: handle (runtime 所有の不透明参照) / view (frame 有効の
/// バイト列) / keyed (key で参照する resource) / record (option table や
/// 戻り値の平らな構造)。</summary>
public sealed record ApiType(string Name, string Kind, string? Base,
    IReadOnlyList<ApiField> Fields, IReadOnlyList<ApiFunction> Methods, string Doc);

public sealed record ApiModel(IReadOnlyList<ApiNamespace> Namespaces,
    IReadOnlyList<ApiType> Types)
{
    public ApiType? FindType(string name) => Types.FirstOrDefault(t => t.Name == name);

    public ApiEnum? FindEnum(string qualified) =>
        Namespaces.SelectMany(n => n.Enums).FirstOrDefault(e => e.Namespace + "." + e.Name == qualified);
}

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
                         && t.DeclaringSyntaxReferences.Length > 0
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
                .OrderBy(t => t.Locations[0].SourceSpan.Start)
                .Select(e => LoadEnum(e, cls.Name)).ToList()
            : [];
        var fields = cls.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.IsStatic && !f.IsConst
                && f.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadField).ToList();
        var consts = cls.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.IsConst && f.DeclaredAccessibility == Accessibility.Public)
            .Select(f => new ApiConst(f.Name, LuaNaming.Const(f.Name), f.ConstantValue!, Doc(f)))
            .ToList();
        return new ApiNamespace(cls.Name, luaPath, functions, enums, fields, consts, Doc(cls));
    }

    private static ApiEnum LoadEnum(INamedTypeSymbol e, string ns)
    {
        var members = e.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => new ApiEnumMember(f.Name, LuaNaming.Const(f.Name),
                Convert.ToInt32(f.ConstantValue), Doc(f)))
            .ToList();
        return new ApiEnum(e.Name, ns, LuaNaming.RefTypePath(e), members, Doc(e),
            HasAttr(e, "LubLuaStringAttribute"));
    }

    private static ApiType LoadType(INamedTypeSymbol t)
    {
        var kind = TypeKindOf(t) switch
        {
            LubTypeKind.Handle => "handle",
            LubTypeKind.View => "view",
            LubTypeKind.Keyed => "keyed",
            _ => "record",
        };
        var fields = t.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && f.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadField).ToList();
        var methods = t.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic
                && m.DeclaredAccessibility == Accessibility.Public)
            .Select(LoadFunction).ToList();
        var baseName = t.BaseType != null && t.BaseType.SpecialType != SpecialType.System_Object
            ? t.BaseType.Name : null;
        return new ApiType(t.Name, kind, baseName, fields, methods, Doc(t));
    }

    private static ApiFunction LoadFunction(IMethodSymbol m)
    {
        var ps = m.Parameters.Select(p => new ApiParam(p.Name, LuaNaming.Member(p.Name),
            Resolve(p.Type, p.NullableAnnotation), p.HasExplicitDefaultValue,
            p.RefKind == RefKind.Out, ArrayLen(p))).ToList();
        return new ApiFunction(m.Name, LuaNaming.Member(m.Name),
            Resolve(m.ReturnType, m.ReturnNullableAnnotation), ps, Doc(m),
            HasAttr(m, "LubNoFailAttribute"), HasAttr(m, "LubMaybeAttribute"),
            HasAttr(m, "LubNoCAttribute"));
    }

    private static ApiField LoadField(IFieldSymbol f) =>
        new(f.Name, LuaNaming.Member(f.Name), Resolve(f.Type, f.NullableAnnotation),
            f.NullableAnnotation == NullableAnnotation.Annotated
                || f.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
            Doc(f), ArrayLen(f), HasAttr(f, "LubBitsAttribute"));

    private static bool HasAttr(ISymbol s, string name) =>
        s.GetAttributes().Any(a => a.AttributeClass?.Name == name);

    private static int? ArrayLen(ISymbol s)
    {
        var a = s.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "LubArrayAttribute");
        return a == null ? null : Convert.ToInt32(a.ConstructorArguments[0].Value);
    }

    private static LubTypeKind TypeKindOf(INamedTypeSymbol t)
    {
        if (HasAttr(t, "LubHandleAttribute")) return LubTypeKind.Handle;
        if (HasAttr(t, "LubViewAttribute")) return LubTypeKind.View;
        if (HasAttr(t, "LubKeyedAttribute")) return LubTypeKind.Keyed;
        return LubTypeKind.Record;
    }

    public static TypeRef Resolve(ITypeSymbol t, NullableAnnotation annotation)
    {
        var nullable = annotation == NullableAnnotation.Annotated;
        switch (t.SpecialType)
        {
            case SpecialType.System_Void: return TypeRef.VoidType;
            case SpecialType.System_Int32: return new TypeRef(LubTypeKind.Int, "int", nullable, null, null, null);
            case SpecialType.System_Double: return new TypeRef(LubTypeKind.Double, "double", nullable, null, null, null);
            case SpecialType.System_Boolean: return new TypeRef(LubTypeKind.Bool, "bool", nullable, null, null, null);
            case SpecialType.System_String: return new TypeRef(LubTypeKind.String, "string", nullable, null, null, null);
        }
        if (t is IArrayTypeSymbol arr)
            return new TypeRef(LubTypeKind.Array, "array", nullable,
                Resolve(arr.ElementType, arr.ElementNullableAnnotation), null, null);
        if (t is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return Resolve(named.TypeArguments[0], NullableAnnotation.NotAnnotated).AsNullable();
            var def = named.OriginalDefinition.ToDisplayString();
            if (def == "System.Collections.Generic.List<T>")
                return new TypeRef(LubTypeKind.List, "List", nullable,
                    Resolve(named.TypeArguments[0], named.TypeArgumentNullableAnnotations[0]), null, null);
            if (def.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal))
                return new TypeRef(LubTypeKind.Dict, "Dictionary", nullable, null, null, null);
            if (named.TypeKind == TypeKind.Delegate)
            {
                var invoke = named.DelegateInvokeMethod!;
                var ps = invoke.Parameters.Select(p => Resolve(p.Type, p.NullableAnnotation)).ToList();
                return new TypeRef(LubTypeKind.Func, "Func", nullable, null, ps,
                    Resolve(invoke.ReturnType, invoke.ReturnNullableAnnotation));
            }
            if (named.TypeKind == TypeKind.Enum)
            {
                var ns = named.ContainingType?.Name ?? "";
                return new TypeRef(LubTypeKind.Enum, ns + "." + named.Name, nullable, null, null, null);
            }
            if (named.TypeKind == TypeKind.Class)
                return new TypeRef(TypeKindOf(named), named.Name, nullable, null, null, null);
        }
        throw new InvalidOperationException($"unsupported stub type: {t.ToDisplayString()}");
    }

    private static bool IsWide(char c) => c > 0x2E7F && !char.IsSurrogate(c);

    private static readonly Regex SummaryRegex = new(
        @"<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string Doc(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml() ?? "";
        var m = SummaryRegex.Match(xml);
        if (!m.Success) return "";
        var body = TagRegex.Replace(m.Groups[1].Value, "");
        body = body.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        // 行の継ぎ目は空白 1 つに畳む。CJK 同士の継ぎ目には空白を入れない。
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0 && !(IsWide(sb[^1]) && IsWide(line[0]))) sb.Append(' ');
            sb.Append(line);
        }
        return Regex.Replace(sb.ToString(), @"[ \t]+", " ").Trim();
    }
}

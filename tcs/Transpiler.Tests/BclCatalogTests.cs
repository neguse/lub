using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests;

/// <summary>
/// セルフホストの compiler が持つ BCL の名前の表 (SelfHost/Compiler/BclCatalog.cs) を、参照アセンブリから作り直して比べる。
/// 表は「.NET に有るがサブセットに無い API」を TCS1002 と報告するのに使う (無い名前は CSxxxx)。
/// TCS_UPDATE_BCL=1 で表を書き直す。
/// </summary>
public class BclCatalogTests
{
    // 型の名前を集める namespace (user のコードが using しそうなもの)
    private static readonly string[] Namespaces =
    [
        "System", "System.Collections", "System.Collections.Generic", "System.Collections.ObjectModel",
        "System.Text", "System.IO", "System.Linq", "System.Globalization", "System.Numerics",
        "System.Threading", "System.Threading.Tasks", "System.Diagnostics", "System.Buffers",
        "System.Linq.Expressions", "System.Net.Sockets",
    ];

    // 型の名前だけを集める参照アセンブリ (compiler の参照 TcsLibrary.References に無いもの)
    private static readonly string[] CatalogOnlyAssemblies =
    [
        "System.Threading.Thread.dll", "System.Linq.Expressions.dll", "System.Net.Sockets.dll", "System.Net.Primitives.dll",
    ];

    // member の名前を集める型 (サブセットが member を絞って通す型と、拡張メソッドの Enumerable)
    private static readonly string[] MemberTypes =
    [
        "System.String", "System.Char", "System.Int32", "System.Single", "System.Math", "System.Console",
        "System.Array", "System.Enum", "System.Environment", "System.Text.Rune", "System.Random",
        "System.Collections.Generic.List`1", "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.HashSet`1", "System.Collections.Generic.Dictionary`2",
        "System.Linq.Enumerable",
        "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IList`1", "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IDictionary`2",
        "System.IComparable`1",
    ];

    [Fact]
    public void Catalog_IsUpToDate()
    {
        var root = Path.GetDirectoryName(TestHelper.FindProjectFile("tcs.slnx"))!;
        var path = Path.Combine(root, "SelfHost", "Compiler", "BclCatalog.cs");
        var generated = Generate();
        if (Environment.GetEnvironmentVariable("TCS_UPDATE_BCL") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }
        Assert.True(File.Exists(path) && File.ReadAllText(path) == generated,
            "SelfHost/Compiler/BclCatalog.cs is stale: run TCS_UPDATE_BCL=1 dotnet test --filter BclCatalogTests");
    }

    private static readonly SymbolDisplayFormat Display = SymbolDisplayFormat.CSharpErrorMessageFormat;

    private static string Generate()
    {
        var extra = CatalogOnlyAssemblies.Select(name => (MetadataReference)MetadataReference.CreateFromStream(
            typeof(BclCatalogTests).Assembly.GetManifestResourceStream("catalog/" + name)
            ?? throw new InvalidOperationException("missing resource: catalog/" + name)));
        var compilation = CSharpCompilation.Create("BclCatalog", [], TcsLibrary.References.Concat(extra));
        var types = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var ns in Namespaces)
        {
            var nsSymbol = FindNamespace(compilation.GlobalNamespace, ns);
            if (nsSymbol == null) continue;
            foreach (var type in nsSymbol.GetTypeMembers())
            {
                if (type.DeclaredAccessibility != Accessibility.Public) continue;
                var key = ns + "." + type.MetadataName;
                types[key] = type.OriginalDefinition.ToDisplayString(Display);
            }
        }
        var members = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in MemberTypes)
        {
            var type = compilation.GetTypeByMetadataName(key)
                ?? throw new InvalidOperationException("missing BCL type: " + key);
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var chain = new List<INamedTypeSymbol>();
            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType) chain.Add(t);
            if (type.TypeKind == TypeKind.Interface) chain.AddRange(type.AllInterfaces);
            foreach (var t in chain)
            {
                foreach (var m in t.GetMembers())
                {
                    if (m.DeclaredAccessibility != Accessibility.Public || m.IsImplicitlyDeclared) continue;
                    if (m is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                    if (m is not (IMethodSymbol or IPropertySymbol or IFieldSymbol)) continue;
                    if (m is IPropertySymbol { IsIndexer: true }) continue;
                    if (!names.ContainsKey(m.Name))
                        names[m.Name] = t.OriginalDefinition.ToDisplayString(Display);
                }
            }
            // 名前ごとに宣言した型の表示 (継承した member は基底の型の名前で報告する)
            var display = type.OriginalDefinition.ToDisplayString(Display);
            members[key] = string.Join(" ", names.Select(kv => kv.Value == display ? kv.Key : kv.Key + "@" + kv.Value));
        }

        var sb = new StringBuilder();
        sb.Append("""
            using System.Collections.Generic;

            namespace TcsSelf;

            // 生成物 (Transpiler.Tests/BclCatalogTests.cs、TCS_UPDATE_BCL=1)。手で編集しない。
            // .NET の参照アセンブリにある BCL の型と、サブセットが member を絞って通す型の member の名前。
            // 「.NET に有るがサブセットに無い API」を TCS1002 と報告するのに使う
            public static class BclCatalog
            {
                private static Dictionary<string, string>? _types;
                private static Dictionary<string, Dictionary<string, string>>? _members;

                // metadata 名 (`System.IO.File`、`System.Collections.Generic.LinkedList`1`) の型の表示の名前 (無ければ null)
                public static string? TypeDisplay(string metadataName)
                {
                    if (_types == null) _types = Types();
                    string? display;
                    if (_types.TryGetValue(metadataName, out display)) return display;
                    return null;
                }

                // member の一覧を持つ型か
                public static bool HasMembers(string metadataName)
                {
                    if (_members == null) _members = Members();
                    return _members.ContainsKey(metadataName);
                }

                // 型 (metadata 名) の member を宣言した型の表示の名前 (無ければ null)
                public static string? MemberOwner(string metadataName, string member)
                {
                    if (_members == null) _members = Members();
                    Dictionary<string, string>? names;
                    if (!_members.TryGetValue(metadataName, out names)) return null;
                    string? owner;
                    if (names.TryGetValue(member, out owner)) return owner;
                    return null;
                }

                // 名前を空白で区切った一覧。継承した member は 名前@宣言した型の表示
                private static void AddMembers(Dictionary<string, Dictionary<string, string>> d, string metadataName, string list)
                {
                    string own = TypeDisplay(metadataName) ?? metadataName;
                    var names = new Dictionary<string, string>();
                    foreach (var item in list.Split(' '))
                    {
                        if (item == "") continue;
                        var parts = item.Split('@');
                        names[parts[0]] = parts.Length > 1 ? parts[1] : own;
                    }
                    d[metadataName] = names;
                }

                // metadata 名 (namespace.Name`arity) と表示の名前 (C# のエラーの書式)
                private static Dictionary<string, string> Types()
                {
                    var d = new Dictionary<string, string>();

            """);
        foreach (var (key, display) in types)
            sb.Append("        d[\"").Append(key).Append("\"] = \"").Append(display).Append("\";\n");
        sb.Append("        return d;\n    }\n\n");
        sb.Append("    private static Dictionary<string, Dictionary<string, string>> Members()\n    {\n");
        sb.Append("        var d = new Dictionary<string, Dictionary<string, string>>();\n");
        foreach (var (key, names) in members)
            sb.Append("        AddMembers(d, \"").Append(key).Append("\", \"").Append(names).Append("\");\n");
        sb.Append("        return d;\n    }\n}\n");
        return sb.ToString();
    }

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol global, string name)
    {
        var current = global;
        foreach (var part in name.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current == null) return null;
        }
        return current;
    }
}

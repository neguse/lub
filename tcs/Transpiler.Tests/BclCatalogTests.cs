using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests;

/// <summary>
/// Rebuilds the BCL name table that the self-hosted compiler has (Compiler/BclCatalog.cs) from the reference assemblies and compares them.
/// The table is used to report "APIs that exist in .NET but not in the subset" as TCS1002 (missing names are CSxxxx).
/// TCS_UPDATE_BCL=1 rewrites the table.
/// </summary>
public class BclCatalogTests
{
    // Namespaces to collect type names from (ones that user code is likely to import with using)
    private static readonly string[] Namespaces =
    [
        "System", "System.Collections", "System.Collections.Generic", "System.Collections.ObjectModel",
        "System.Text", "System.IO", "System.Linq", "System.Globalization", "System.Numerics",
        "System.Threading", "System.Threading.Tasks", "System.Diagnostics", "System.Buffers",
        "System.Linq.Expressions", "System.Net.Sockets",
    ];

    // Reference assemblies from which only type names are collected (ones not in the compiler's references TcsLibrary.References)
    private static readonly string[] CatalogOnlyAssemblies =
    [
        "System.Threading.Thread.dll", "System.Linq.Expressions.dll", "System.Net.Sockets.dll", "System.Net.Primitives.dll",
    ];

    // Types to collect member names from (types the subset passes with a restricted member set, and Enumerable for extension methods)
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
        var path = Path.Combine(root, "Compiler", "BclCatalog.cs");
        var generated = Generate();
        if (Environment.GetEnvironmentVariable("TCS_UPDATE_BCL") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }
        Assert.True(File.Exists(path) && File.ReadAllText(path) == generated,
            "Compiler/BclCatalog.cs is stale: run TCS_UPDATE_BCL=1 dotnet test --filter BclCatalogTests");
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
            // Display of the declaring type per name (inherited members are reported with the base type's name)
            var display = type.OriginalDefinition.ToDisplayString(Display);
            members[key] = string.Join(" ", names.Select(kv => kv.Value == display ? kv.Key : kv.Key + "@" + kv.Value));
        }

        var sb = new StringBuilder();
        sb.Append("""
            using System.Collections.Generic;

            namespace TcsSelf;

            // Generated file (Transpiler.Tests/BclCatalogTests.cs, TCS_UPDATE_BCL=1). Do not edit by hand.
            // BCL types in the .NET reference assemblies, and member names of the types the subset passes with a restricted member set.
            // Used to report "APIs that exist in .NET but not in the subset" as TCS1002
            public static class BclCatalog
            {
                private static Dictionary<string, string>? _types;
                private static Dictionary<string, Dictionary<string, string>>? _members;

                // Display name of the type with metadata name (`System.IO.File`, `System.Collections.Generic.LinkedList`1`) (null if none)
                public static string? TypeDisplay(string metadataName)
                {
                    if (_types == null) _types = Types();
                    string? display;
                    if (_types.TryGetValue(metadataName, out display)) return display;
                    return null;
                }

                // Whether the type has a member list
                public static bool HasMembers(string metadataName)
                {
                    if (_members == null) _members = Members();
                    return _members.ContainsKey(metadataName);
                }

                // Display name of the type that declared a member of the type (metadata name) (null if none)
                public static string? MemberOwner(string metadataName, string member)
                {
                    if (_members == null) _members = Members();
                    Dictionary<string, string>? names;
                    if (!_members.TryGetValue(metadataName, out names)) return null;
                    string? owner;
                    if (names.TryGetValue(member, out owner)) return owner;
                    return null;
                }

                // Space-separated list of names. An inherited member is name@display name of the declaring type
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

                // Metadata name (namespace.Name`arity) and display name (C# error format)
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

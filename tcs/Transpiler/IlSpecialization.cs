using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

internal static class IlSpecialization
{
    public static (SyntaxTree[] Trees, string? Error) Expand(SyntaxTree[] trees, SyntaxTree[] references)
    {
        var names = new Dictionary<string, string>();
        for (var round = 0; round < 128; round++)
        {
            var compilation = CSharpCompilation.Create("IlSpecialization", trees.Concat(references),
                Transpiler.References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var instances = new List<(INamedTypeSymbol Type, ClassDeclarationSyntax Source)>();
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>())
                {
                    if (model.GetSymbolInfo(node).Symbol is not INamedTypeSymbol type
                        || ContainsParameter(type) || names.ContainsKey(Key(type))) continue;
                    if (type.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()
                        ?.GetSyntax() is not ClassDeclarationSyntax source) continue;
                    if (source.Parent is not CompilationUnitSyntax)
                        return (trees, "C specialization requires top-level generic classes");
                    if (source.Modifiers.Any(SyntaxKind.PartialKeyword))
                        return (trees, "C specialization does not support partial generic classes");
                    if (names.Count >= 256)
                        return (trees, "C generic specialization exceeds 256 closed classes");
                    var name = $"TcsClosed{names.Count}_{source.Identifier.ValueText}";
                    if (compilation.GetTypeByMetadataName(name) != null)
                        return (trees, $"C specialization name conflicts with {name}");
                    names.Add(Key(type), name);
                    instances.Add((type, source));
                }
            }
            if (instances.Count == 0)
            {
                return (trees.Select(tree =>
                {
                    var root = new Rewriter(compilation.GetSemanticModel(tree), names, [])
                        .Visit(tree.GetRoot())!;
                    var templates = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                        .Where(c => c.TypeParameterList != null);
                    return tree.WithRootAndOptions(root.RemoveNodes(templates,
                        SyntaxRemoveOptions.KeepNoTrivia)!, tree.Options);
                }).ToArray(), null);
            }
            var additions = new Dictionary<SyntaxTree, List<MemberDeclarationSyntax>>();
            foreach (var (type, source) in instances)
            {
                var substitution = type.OriginalDefinition.TypeParameters
                    .Select((p, i) => (Key: p.Name, Type: type.TypeArguments[i]))
                    .ToDictionary(p => p.Key, p => p.Type);
                var model = compilation.GetSemanticModel(source.SyntaxTree);
                var copy = (ClassDeclarationSyntax)new Rewriter(model, names, substitution)
                    .Visit(source)!;
                var name = names[Key(type)];
                copy = copy.WithIdentifier(SyntaxFactory.Identifier(name))
                    .WithTypeParameterList(null).WithConstraintClauses(default);
                copy = copy.WithMembers(SyntaxFactory.List(copy.Members.Select(m =>
                    m is ConstructorDeclarationSyntax ctor
                        ? (MemberDeclarationSyntax)ctor.WithIdentifier(SyntaxFactory.Identifier(name))
                        : m)));
                if (!additions.TryGetValue(source.SyntaxTree, out var members))
                    additions[source.SyntaxTree] = members = [];
                members.Add(copy);
            }
            trees = trees.Select(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                var root = (CompilationUnitSyntax)new Rewriter(model, names, [])
                    .Visit(tree.GetRoot())!;
                if (additions.TryGetValue(tree, out var members))
                    root = root.AddMembers([.. members]);
                return tree.WithRootAndOptions(root, tree.Options);
            }).ToArray();
        }
        return (trees, "C generic specialization exceeds 128 expansion rounds");
    }

    private static string Key(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool ContainsParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsParameter),
        _ => false,
    };

    private sealed class Rewriter(SemanticModel model, Dictionary<string, string> names,
        Dictionary<string, ITypeSymbol> substitution) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            if (model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type
                && names.TryGetValue(Key(type), out var name))
                return SyntaxFactory.IdentifierName(name).WithTriviaFrom(node);
            return base.VisitGenericName(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol parameter
                && parameter.TypeParameterKind == TypeParameterKind.Type
                && node.Parent is not TypeParameterConstraintClauseSyntax
                && substitution.TryGetValue(parameter.Name, out var type))
                return SyntaxFactory.ParseTypeName(Key(type)).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }
    }
}

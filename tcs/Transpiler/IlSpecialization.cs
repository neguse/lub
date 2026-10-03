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
            var methods = new List<(IMethodSymbol Method, MethodDeclarationSyntax Source)>();
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
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol { IsGenericMethod: true } method
                        || method.TypeArguments.Any(ContainsParameter)
                        || ContainsParameter(method.ContainingType)
                        || names.ContainsKey(Key(method))) continue;
                    if (method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()
                        ?.GetSyntax() is not MethodDeclarationSyntax source) continue;
                    // generic class の template 内の method は、その class の閉じた copy で数える
                    if (source.Parent is not ClassDeclarationSyntax { TypeParameterList: null }) continue;
                    if (names.Count >= 256)
                        return (trees, "C generic specialization exceeds 256 closed classes");
                    names.Add(Key(method), $"TcsClosed{names.Count}_{source.Identifier.ValueText}");
                    methods.Add((method, source));
                }
            }
            if (instances.Count == 0 && methods.Count == 0)
            {
                return (trees.Select(tree =>
                {
                    var root = new Rewriter(compilation.GetSemanticModel(tree), names, [])
                        .Visit(tree.GetRoot())!;
                    var templates = root.DescendantNodes().Where(n =>
                        n is ClassDeclarationSyntax { TypeParameterList: not null }
                            or MethodDeclarationSyntax { TypeParameterList: not null });
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
            var closedMethods = new Dictionary<ClassDeclarationSyntax, List<MemberDeclarationSyntax>>();
            foreach (var (method, source) in methods)
            {
                var substitution = method.OriginalDefinition.TypeParameters
                    .Select((p, i) => (Key: p.Name, Type: method.TypeArguments[i]))
                    .ToDictionary(p => p.Key, p => p.Type);
                var model = compilation.GetSemanticModel(source.SyntaxTree);
                var copy = (MethodDeclarationSyntax)new Rewriter(model, names, substitution)
                    .Visit(source)!;
                copy = copy.WithIdentifier(SyntaxFactory.Identifier(names[Key(method)]))
                    .WithTypeParameterList(null).WithConstraintClauses(default);
                var owner = (ClassDeclarationSyntax)source.Parent!;
                if (!closedMethods.TryGetValue(owner, out var members))
                    closedMethods[owner] = members = [];
                members.Add(copy);
            }
            trees = trees.Select(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                var root = (CompilationUnitSyntax)new Rewriter(model, names, [], closedMethods)
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

    private static string Key(IMethodSymbol method) =>
        $"{Key(method.ContainingType)}::{method.Name}/{method.Parameters.Length}" +
        $"<{string.Join(",", method.TypeArguments.Select(Key))}>";

    private static bool ContainsParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsParameter),
        _ => false,
    };

    private sealed class Rewriter(SemanticModel model, Dictionary<string, string> names,
        Dictionary<string, ITypeSymbol> substitution,
        Dictionary<ClassDeclarationSyntax, List<MemberDeclarationSyntax>>? closedMethods = null)
        : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            return closedMethods != null && closedMethods.TryGetValue(node, out var members)
                ? visited.AddMembers([.. members]) : visited;
        }

        // 閉じた generic method の呼び出しは単相化した method 名へ (型引数は落とす)
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol { IsGenericMethod: true } method
                || !names.TryGetValue(Key(method), out var name))
                return visited;
            var identifier = SyntaxFactory.IdentifierName(name);
            return visited.Expression switch
            {
                MemberAccessExpressionSyntax access => visited.WithExpression(
                    access.WithName(identifier.WithTriviaFrom(access.Name))),
                SimpleNameSyntax simple => visited.WithExpression(identifier.WithTriviaFrom(simple)),
                _ => visited,
            };
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            if (model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type
                && names.TryGetValue(Key(type), out var name))
                return SyntaxFactory.IdentifierName(name).WithTriviaFrom(node);
            return base.VisitGenericName(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol
                { TypeParameterKind: TypeParameterKind.Type or TypeParameterKind.Method } parameter
                && node.Parent is not TypeParameterConstraintClauseSyntax
                && substitution.TryGetValue(parameter.Name, out var type))
                return SyntaxFactory.ParseTypeName(Key(type)).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }
    }
}

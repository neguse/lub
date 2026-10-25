using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

internal static class IlSpecialization
{
    public static (SyntaxTree[] Trees, string? Error) Expand(SyntaxTree[] trees, SyntaxTree[] references)
    {
        var names = new Dictionary<string, string>();
        // 閉じた interface の generic method (interface の key、method の名前、型引数の key、閉じた名前)。
        // 実装する型ごとに同じ名前の閉じた method を作る (後の round でできる型の copy も)
        var interfaceMethods = new List<(string Interface, string Method, string[] TypeArgs, string Name)>();
        for (var round = 0; round < 128; round++)
        {
            var compilation = CSharpCompilation.Create("IlSpecialization", trees.Concat(references),
                Transpiler.References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var instances = new List<(INamedTypeSymbol Type, TypeDeclarationSyntax Source)>();
            var methods = new List<(MethodDeclarationSyntax Source, Dictionary<string, string> Substitution,
                string Name)>();
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>())
                {
                    if (model.GetSymbolInfo(node).Symbol is not INamedTypeSymbol type
                        || ContainsParameter(type) || names.ContainsKey(Key(type))) continue;
                    // class / record / struct / interface の閉じた copy を作る
                    if (type.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()
                        ?.GetSyntax() is not TypeDeclarationSyntax source) continue;
                    if (source.Parent is not CompilationUnitSyntax)
                        return (trees, "C specialization requires top-level generic types");
                    if (source.Modifiers.Any(SyntaxKind.PartialKeyword))
                        return (trees, "C specialization does not support partial generic types");
                    if (names.Count >= 256)
                        return (trees, "C generic specialization exceeds 256 closed classes");
                    var name = $"TcsClosed{names.Count}_{source.Identifier.ValueText}";
                    if (compilation.GetTypeByMetadataName(name) != null)
                        return (trees, $"C specialization name conflicts with {name}");
                    names.Add(Key(type), name);
                    instances.Add((type, source));
                }
                // 呼び出しと method group (`G.Id<int>`、型引数を推論させた `G.Id`)
                foreach (var node in tree.GetRoot().DescendantNodes().Where(n =>
                    n is InvocationExpressionSyntax || IsMethodGroupName(n)))
                {
                    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol { IsGenericMethod: true } called)
                        continue;
                    // interface の method を実装する method は interface の method で数える
                    var method = InterfaceMethod(called) ?? called;
                    if (method.TypeArguments.Any(ContainsParameter)
                        || ContainsParameter(method.ContainingType)
                        || names.ContainsKey(Key(method))) continue;
                    if (method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()
                        ?.GetSyntax() is not MethodDeclarationSyntax source) continue;
                    // generic な型の template 内の method は、その型の閉じた copy で数える
                    if (source.Parent is not TypeDeclarationSyntax { TypeParameterList: null }) continue;
                    if (names.Count >= 256)
                        return (trees, "C generic specialization exceeds 256 closed classes");
                    var name = $"TcsClosed{names.Count}_{source.Identifier.ValueText}";
                    names.Add(Key(method), name);
                    var typeArgs = method.TypeArguments.Select(Key).ToArray();
                    methods.Add((source, Substitution(method.OriginalDefinition, typeArgs), name));
                    if (method.ContainingType.TypeKind == TypeKind.Interface)
                        interfaceMethods.Add((Key(method.ContainingType), LuaNaming.MethodName(method),
                            typeArgs, name));
                }
            }
            // 閉じた interface の generic method を、実装する型に同じ名前で作る
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var decl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (decl.TypeParameterList != null || decl is InterfaceDeclarationSyntax
                        || model.GetDeclaredSymbol(decl) is not { } type) continue;
                    foreach (var (iface, methodName, typeArgs, name) in interfaceMethods)
                    {
                        var member = type.AllInterfaces.FirstOrDefault(i => Key(i) == iface)
                            ?.GetMembers().OfType<IMethodSymbol>()
                            .FirstOrDefault(m => m.IsGenericMethod && LuaNaming.MethodName(m) == methodName);
                        if (member == null
                            || type.FindImplementationForInterfaceMember(member) is not IMethodSymbol impl
                            || impl.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                                is not MethodDeclarationSyntax { Parent: var owner } implSource
                            || owner != decl) continue;
                        var implKey = $"{Key(type)}::{LuaNaming.MethodName(impl)}<{string.Join(",", typeArgs)}>";
                        if (!names.TryAdd(implKey, name)) continue;
                        methods.Add((implSource, Substitution(impl, typeArgs), name));
                    }
                }
            }
            if (instances.Count == 0 && methods.Count == 0)
            {
                return (trees.Select(tree =>
                {
                    var root = new Rewriter(compilation.GetSemanticModel(tree), names, [])
                        .Visit(tree.GetRoot())!;
                    var templates = root.DescendantNodes().Where(n =>
                        n is TypeDeclarationSyntax { TypeParameterList: not null }
                            or MethodDeclarationSyntax { TypeParameterList: not null });
                    return tree.WithRootAndOptions(root.RemoveNodes(templates,
                        SyntaxRemoveOptions.KeepNoTrivia)!, tree.Options);
                }).ToArray(), null);
            }
            var additions = new Dictionary<SyntaxTree, List<MemberDeclarationSyntax>>();
            foreach (var (type, source) in instances)
            {
                var substitution = type.OriginalDefinition.TypeParameters
                    .Select((p, i) => (Key: p.Name, Type: Key(type.TypeArguments[i])))
                    .ToDictionary(p => p.Key, p => p.Type);
                var model = compilation.GetSemanticModel(source.SyntaxTree);
                var copy = (TypeDeclarationSyntax)new Rewriter(model, names, substitution)
                    .Visit(source)!;
                var name = names[Key(type)];
                copy = copy.WithIdentifier(SyntaxFactory.Identifier(name))
                    .WithTypeParameterList(null).WithConstraintClauses(default);
                copy = copy.WithMembers(SyntaxFactory.List(copy.Members.Zip(source.Members, (m, original) =>
                    m switch
                    {
                        ConstructorDeclarationSyntax ctor =>
                            ctor.WithIdentifier(SyntaxFactory.Identifier(name)),
                        MethodDeclarationSyntax method when OverloadName(
                                model.GetDeclaredSymbol((MethodDeclarationSyntax)original)) is { } il =>
                            method.WithIdentifier(SyntaxFactory.Identifier(il)),
                        _ => m,
                    })));
                if (!additions.TryGetValue(source.SyntaxTree, out var members))
                    additions[source.SyntaxTree] = members = [];
                members.Add(copy);
            }
            var closedMethods = new Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>>();
            foreach (var (source, substitution, name) in methods)
            {
                var model = compilation.GetSemanticModel(source.SyntaxTree);
                var copy = (MethodDeclarationSyntax)new Rewriter(model, names, substitution)
                    .Visit(source)!;
                copy = copy.WithIdentifier(SyntaxFactory.Identifier(name))
                    .WithTypeParameterList(null).WithConstraintClauses(default);
                var owner = (TypeDeclarationSyntax)source.Parent!;
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
        $"{Key(method.ContainingType)}::{LuaNaming.MethodName(method)}" +
        $"<{string.Join(",", method.TypeArguments.Select(Key))}>";

    // method の型引数の名前から型の key へ
    private static Dictionary<string, string> Substitution(IMethodSymbol method, string[] typeArgs) =>
        method.TypeParameters.Select((p, i) => (p.Name, typeArgs[i]))
            .ToDictionary(p => p.Item1, p => p.Item2);

    // class の method が実装する interface の method (型引数は呼び出しのもの)
    private static IMethodSymbol? InterfaceMethod(IMethodSymbol method)
    {
        var definition = method.OriginalDefinition;
        if (definition.IsStatic || definition.ContainingType is not { TypeKind: not TypeKind.Interface } type)
            return null;
        foreach (var iface in type.AllInterfaces)
            foreach (var candidate in iface.GetMembers(definition.Name).OfType<IMethodSymbol>())
                if (candidate.IsGenericMethod && SymbolEqualityComparer.Default.Equals(
                        type.FindImplementationForInterfaceMember(candidate)?.OriginalDefinition, definition))
                    return candidate.Construct([.. method.TypeArguments]);
        return null;
    }

    // generic な型の overload は、閉じた copy で parameter の型が重なりうる (`M(T)` と `M(string)` の
    // T = string)。閉じた copy では宣言と呼び出しの名前を IL 名 (LuaNaming.MethodName) にする
    private static string? OverloadName(IMethodSymbol? method)
    {
        if (method?.OriginalDefinition is not { MethodKind: MethodKind.Ordinary } m
            || !m.ContainingType.IsGenericType || m.DeclaringSyntaxReferences.Length == 0
            || m.ContainingType.GetMembers(m.Name).OfType<IMethodSymbol>()
                .Count(x => x.MethodKind == MethodKind.Ordinary) < 2)
            return null;
        return LuaNaming.MethodName(m);
    }

    // 呼び出しの callee ではない method の名前 (method group)
    private static bool IsMethodGroupName(SyntaxNode node) =>
        node is SimpleNameSyntax name
        && name.Parent is not InvocationExpressionSyntax
        && !(name.Parent is MemberAccessExpressionSyntax access && access.Name == name
            && access.Parent is InvocationExpressionSyntax call && call.Expression == access);

    private static bool ContainsParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsParameter),
        _ => false,
    };

    private sealed class Rewriter(SemanticModel model, Dictionary<string, string> names,
        Dictionary<string, string> substitution,
        Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>>? closedMethods = null)
        : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) =>
            WithClosedMethods(node, (TypeDeclarationSyntax)base.VisitClassDeclaration(node)!);

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) =>
            WithClosedMethods(node, (TypeDeclarationSyntax)base.VisitStructDeclaration(node)!);

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) =>
            WithClosedMethods(node, (TypeDeclarationSyntax)base.VisitRecordDeclaration(node)!);

        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
            WithClosedMethods(node, (TypeDeclarationSyntax)base.VisitInterfaceDeclaration(node)!);

        private TypeDeclarationSyntax WithClosedMethods(TypeDeclarationSyntax node,
            TypeDeclarationSyntax visited) =>
            closedMethods != null && closedMethods.TryGetValue(node, out var members)
                ? visited.AddMembers([.. members]) : visited;

        // 閉じた generic method の呼び出しは単相化した method 名へ (型引数は落とす)
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method)
                return visited;
            var name = method.IsGenericMethod && names.TryGetValue(Key(method), out var closed)
                ? closed : OverloadName(method);
            if (name == null) return visited;
            var identifier = SyntaxFactory.IdentifierName(name);
            return visited.Expression switch
            {
                MemberAccessExpressionSyntax access => visited.WithExpression(
                    access.WithName(identifier.WithTriviaFrom(access.Name))),
                MemberBindingExpressionSyntax binding => visited.WithExpression(
                    binding.WithName(identifier.WithTriviaFrom(binding.Name))),
                SimpleNameSyntax simple => visited.WithExpression(identifier.WithTriviaFrom(simple)),
                _ => visited,
            };
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            if (model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type
                && names.TryGetValue(Key(type), out var name))
                return SyntaxFactory.IdentifierName(name).WithTriviaFrom(node);
            // 閉じた generic method の method group (`G.Id<int>`)
            if (IsMethodGroupName(node) && ClosedMethodName(node) is { } method)
                return SyntaxFactory.IdentifierName(method).WithTriviaFrom(node);
            return base.VisitGenericName(node);
        }

        private string? ClosedMethodName(SimpleNameSyntax node) =>
            model.GetSymbolInfo(node).Symbol is not IMethodSymbol method ? null
            : method.IsGenericMethod && names.TryGetValue(Key(method), out var name) ? name
            : OverloadName(method);

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            // 型引数を推論させた generic method の method group (`Func<int, int> f = G.Id;`)
            if (IsMethodGroupName(node) && ClosedMethodName(node) is { } method)
                return SyntaxFactory.IdentifierName(method).WithTriviaFrom(node);
            if (model.GetSymbolInfo(node).Symbol is ITypeParameterSymbol
                { TypeParameterKind: TypeParameterKind.Type or TypeParameterKind.Method } parameter
                && node.Parent is not TypeParameterConstraintClauseSyntax
                && substitution.TryGetValue(parameter.Name, out var type))
                return SyntaxFactory.ParseTypeName(type).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }
    }
}

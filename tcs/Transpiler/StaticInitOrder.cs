using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// 型の static initializer の実行順 (doc/support-matrix.md §2.3)。
//
// C# は型の static field を最初に使う前にその型の initializer を走らせる
// (§15.5.6.2)。tcs は全型を定義してから型単位に eager に初期化するので、
// initializer (とそこから呼ぶ project 内 method の推移閉包) が static field に
// 触れる他の型を依存とし、依存を先に初期化する。順は「型を宣言順に触った
// ときの lazy 初期化」と同じ DFS post-order: 依存の循環は、先に触った型が
// 初期化中 (= pre-zero の default) のまま相手の initializer に読まれる。
public static class StaticInitOrder
{
    /// <summary>roots (宣言順) から deps を辿る DFS post-order。初期化中の型
    /// へ戻る辺 (循環) は辿らない。</summary>
    public static List<T> Order<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> deps)
        where T : notnull
    {
        var order = new List<T>();
        var seen = new HashSet<T>();
        void Visit(T t)
        {
            if (!seen.Add(t)) return;
            foreach (var d in deps(t)) Visit(d);
            order.Add(t);
        }
        foreach (var r in roots) Visit(r);
        return order;
    }

    /// <summary>trees で宣言した class ごとの依存 (Lua / IL の型名、発見順)。
    /// static initializer を持たない型は載せない。</summary>
    public static Dictionary<string, List<string>> DependenciesByName(
        Compilation compilation, IReadOnlyCollection<SyntaxTree> trees,
        Func<INamedTypeSymbol, string> typeName) =>
        DependenciesByName(new StaticInitDependencies(compilation, trees), trees, typeName);

    public static Dictionary<string, List<string>> DependenciesByName(
        StaticInitDependencies analysis, IReadOnlyCollection<SyntaxTree> trees,
        Func<INamedTypeSymbol, string> typeName)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var model = analysis.Model(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } type
                    || !analysis.HasStaticInitializers(type))
                    continue;
                result[typeName(type)] = [.. analysis.Of(type).Select(typeName)];
            }
        }
        return result;
    }
}

// initializer から static field に触れる project 型の解析。method ごとの
// 直接の効果 (触る型 / 呼ぶ method) を memo し、型ごとに到達閉包を取る。
// 解決できない呼び出し (delegate 値の呼び出し、host API) は辿らない。
public sealed class StaticInitDependencies(Compilation compilation,
    IReadOnlyCollection<SyntaxTree> projectTrees)
{
    private sealed record Effects(List<INamedTypeSymbol> Types, List<IMethodSymbol> Callees);

    private readonly HashSet<SyntaxTree> _trees = [.. projectTrees];
    private readonly Dictionary<IMethodSymbol, Effects> _methods =
        new(SymbolEqualityComparer.Default);
    private Dictionary<IMethodSymbol, List<IMethodSymbol>>? _overrides;
    // 同じ tree の model を使い回し、bind 結果の cache を効かせる
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

    internal SemanticModel Model(SyntaxTree tree) =>
        _models.TryGetValue(tree, out var m) ? m
            : _models[tree] = compilation.GetSemanticModel(tree);

    /// <summary>これまでの解析で本文を辿った method。本文がこれ以外の method
    /// だけを変える編集では依存は変わらない。</summary>
    public IEnumerable<IMethodSymbol> VisitedMethods => _methods.Keys;

    public static string MethodKey(IMethodSymbol method) =>
        method.OriginalDefinition.ToDisplayString();

    /// <summary>method の本文が直接触る型・呼ぶ method (変化の比較用)。</summary>
    public string EffectsKey(IMethodSymbol method)
    {
        var e = MethodEffects(method);
        return string.Join(",", e.Types.Select(t => t.ToDisplayString())) + ";"
            + string.Join(",", e.Callees.Select(MethodKey));
    }

    public bool HasStaticInitializers(INamedTypeSymbol type) =>
        StaticInitializers(type).Any();

    /// <summary>type の static initializer が初期化を要求する他の project 型
    /// (発見順)。</summary>
    public List<INamedTypeSymbol> Of(INamedTypeSymbol type)
    {
        var types = new List<INamedTypeSymbol>();
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            { type };
        var seenMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var work = new Stack<IMethodSymbol>();
        void Add(Effects e)
        {
            foreach (var t in e.Types)
                if (seenTypes.Add(t)) types.Add(t);
            // 発見順に辿るため逆順に積む
            for (var i = e.Callees.Count - 1; i >= 0; i--)
                if (seenMethods.Add(e.Callees[i].OriginalDefinition))
                    work.Push(e.Callees[i]);
        }
        foreach (var init in StaticInitializers(type))
        {
            Add(Collect(init));
            while (work.Count > 0)
                Add(MethodEffects(work.Pop()));
        }
        return types;
    }

    private IEnumerable<IOperation> StaticInitializers(INamedTypeSymbol type)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (!_trees.Contains(syntaxRef.SyntaxTree)
                || syntaxRef.GetSyntax() is not TypeDeclarationSyntax decl)
                continue;
            var model = Model(syntaxRef.SyntaxTree);
            foreach (var member in decl.Members)
            {
                var isStatic = member.Modifiers.Any(m =>
                    m.ValueText is "static" or "const");
                if (!isStatic) continue;
                IEnumerable<EqualsValueClauseSyntax?> inits = member switch
                {
                    FieldDeclarationSyntax f =>
                        f.Declaration.Variables.Select(v => v.Initializer),
                    PropertyDeclarationSyntax p => [p.Initializer],
                    _ => [],
                };
                foreach (var init in inits)
                {
                    if (init == null || model.GetConstantValue(init.Value).HasValue)
                        continue;
                    if (model.GetOperation(init) is { } op) yield return op;
                }
            }
        }
    }

    private IEnumerable<IOperation> InstanceInitializers(INamedTypeSymbol type)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (!_trees.Contains(syntaxRef.SyntaxTree)
                || syntaxRef.GetSyntax() is not TypeDeclarationSyntax decl)
                continue;
            var model = Model(syntaxRef.SyntaxTree);
            foreach (var member in decl.Members)
            {
                if (member.Modifiers.Any(m => m.ValueText is "static" or "const"))
                    continue;
                IEnumerable<EqualsValueClauseSyntax?> inits = member switch
                {
                    FieldDeclarationSyntax f =>
                        f.Declaration.Variables.Select(v => v.Initializer),
                    PropertyDeclarationSyntax p => [p.Initializer],
                    _ => [],
                };
                foreach (var init in inits)
                    if (init != null && model.GetOperation(init) is { } op)
                        yield return op;
            }
        }
    }

    private Effects MethodEffects(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        if (_methods.TryGetValue(method, out var cached)) return cached;
        var effects = new Effects([], []);
        _methods[method] = effects;

        var hasExplicitInitializer = false;
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            if (!_trees.Contains(syntaxRef.SyntaxTree)) continue;
            var node = syntaxRef.GetSyntax();
            if (node is ConstructorDeclarationSyntax { Initializer: not null })
                hasExplicitInitializer = true;
            var model = Model(syntaxRef.SyntaxTree);
            if (model.GetOperation(node) is { } op) Merge(effects, Collect(op));
        }
        if (method.MethodKind == MethodKind.Constructor && !method.IsStatic)
        {
            // instance 生成は field initializer と (暗黙の) base constructor を走らせる
            foreach (var op in InstanceInitializers(method.ContainingType))
                Merge(effects, Collect(op));
            if (!hasExplicitInitializer && method.ContainingType.BaseType is { } b)
                effects.Callees.AddRange(b.InstanceConstructors);
        }
        // virtual / interface の呼び出しは project 内の実装すべてに届き得る
        if ((method.IsVirtual || method.IsAbstract || method.IsOverride
                || method.ContainingType.TypeKind == TypeKind.Interface)
            && Overrides().TryGetValue(method, out var impls))
            effects.Callees.AddRange(impls);
        return effects;
    }

    private static void Merge(Effects into, Effects from)
    {
        into.Types.AddRange(from.Types);
        into.Callees.AddRange(from.Callees);
    }

    // lambda の本文と method group の先は、delegate を作るだけでは実行されない
    // ので辿らない (辿ると偽の依存・循環で初期化順が狂う)
    private static IEnumerable<IOperation> ExecutedOperations(IOperation root)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var op = stack.Pop();
            yield return op;
            if (op is IAnonymousFunctionOperation) continue;
            foreach (var child in op.ChildOperations.Reverse())
                stack.Push(child);
        }
    }

    private Effects Collect(IOperation root)
    {
        var effects = new Effects([], []);
        foreach (var op in ExecutedOperations(root))
        {
            switch (op)
            {
                case IFieldReferenceOperation { Field: { IsStatic: true, IsConst: false } f }:
                    AddType(effects, f.ContainingType);
                    break;
                case IPropertyReferenceOperation { Property: var p }:
                    if (p.IsStatic && IsAutoProperty(p)) AddType(effects, p.ContainingType);
                    if (p.GetMethod != null) effects.Callees.Add(p.GetMethod);
                    if (p.SetMethod != null) effects.Callees.Add(p.SetMethod);
                    break;
                case IInvocationOperation inv:
                    effects.Callees.Add(inv.TargetMethod);
                    break;
                case IObjectCreationOperation { Constructor: { } ctor }:
                    effects.Callees.Add(ctor);
                    break;
                case IBinaryOperation { OperatorMethod: { } m }:
                    effects.Callees.Add(m);
                    break;
                case IUnaryOperation { OperatorMethod: { } m }:
                    effects.Callees.Add(m);
                    break;
                case ICompoundAssignmentOperation { OperatorMethod: { } m }:
                    effects.Callees.Add(m);
                    break;
                case IIncrementOrDecrementOperation { OperatorMethod: { } m }:
                    effects.Callees.Add(m);
                    break;
            }
        }
        return effects;
    }

    private void AddType(Effects effects, INamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        if (type.DeclaringSyntaxReferences.Any(r => _trees.Contains(r.SyntaxTree)))
            effects.Types.Add(type);
    }

    private bool IsAutoProperty(IPropertySymbol p) =>
        p.DeclaringSyntaxReferences.Any(r => _trees.Contains(r.SyntaxTree)
            && r.GetSyntax() is PropertyDeclarationSyntax
            {
                AccessorList: { } accessors,
            } && accessors.Accessors.All(a => a.Body == null && a.ExpressionBody == null));

    // virtual / abstract / interface member → project 内の override / 実装
    private Dictionary<IMethodSymbol, List<IMethodSymbol>> Overrides()
    {
        if (_overrides != null) return _overrides;
        _overrides = new(SymbolEqualityComparer.Default);
        foreach (var tree in _trees)
        {
            var model = Model(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes()
                .OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } type) continue;
                foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
                {
                    for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
                        AddOverride(o, m);
                }
                foreach (var iface in type.AllInterfaces)
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (type.FindImplementationForInterfaceMember(member)
                        is IMethodSymbol impl)
                        AddOverride(member, impl);
                }
            }
        }
        return _overrides;
    }

    private void AddOverride(IMethodSymbol target, IMethodSymbol impl)
    {
        target = target.OriginalDefinition;
        if (!_overrides!.TryGetValue(target, out var list))
            _overrides[target] = list = [];
        list.Add(impl);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TinyCs;

// module の型の static initializer の依存 (EmittedTypeInfo.InitDependencies)。
// snapshot の init_order (fresh VM の初期化順) の元になる。
public sealed partial class IncrementalCompilationSession
{
    // 前回の依存解析で本文を辿った method → その直接の効果
    // (StaticInitDependencies.MethodKey → EffectsKey)
    private Dictionary<string, string> _initEffects = [];

    // static initializer の依存 (StaticInitOrder) は他 module の method 本文の
    // 先にも及ぶので、emit した module に限らず全 artifact の型で取り直す
    private void RefreshInitDependencies()
    {
        var trees = _moduleOrder.Select(p => _trees[p]).ToList();
        var analysis = new StaticInitDependencies(_compilation!, trees);
        var deps = StaticInitOrder.DependenciesByName(analysis, trees,
            new LuaEmitter().TypeName);
        foreach (var t in _lastGood.Values.SelectMany(a => a.Types))
            t.InitDependencies = deps.GetValueOrDefault(t.Name) ?? [];
        _initEffects = [];
        foreach (var m in analysis.VisitedMethods.ToList())
            _initEffects.TryAdd(StaticInitDependencies.MethodKey(m), analysis.EffectsKey(m));
    }

    // fast path の body edit。依存が変わるのは、static initializer から辿る
    // method の本文が触る型・呼ぶ method を変えたときだけなので、そのときだけ
    // 全体を取り直す。それ以外は前の artifact の依存を型名で引き継ぐ (module を
    // 丸ごと emit し直した artifact は依存を持たない)
    private void UpdateInitDependenciesAfterBodyEdit(SessionModuleArtifact? prev,
        SessionModuleArtifact next, SyntaxTree tree, TextSpan bodySpan)
    {
        if (prev == null || EditChangesInitEffects(tree, bodySpan))
        {
            RefreshInitDependencies();
            return;
        }
        var old = prev.Types.ToDictionary(t => t.Name, t => t.InitDependencies);
        foreach (var t in next.Types)
            t.InitDependencies = old.GetValueOrDefault(t.Name) ?? [];
    }

    private bool EditChangesInitEffects(SyntaxTree tree, TextSpan bodySpan)
    {
        var node = tree.GetCompilationUnitRoot().FindNode(new TextSpan(bodySpan.Start, 0));
        var member = node.AncestorsAndSelf().FirstOrDefault(n =>
            n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax);
        if (member == null
            || _compilation!.GetSemanticModel(tree).GetDeclaredSymbol(member)
                is not IMethodSymbol method)
            return true;
        if (!_initEffects.TryGetValue(StaticInitDependencies.MethodKey(method), out var before))
            return false;
        var analysis = new StaticInitDependencies(_compilation!,
            [.. _moduleOrder.Select(p => _trees[p])]);
        return analysis.EffectsKey(method) != before;
    }
}

using Microsoft.CodeAnalysis;

namespace TinyCs;

// 同じ tree の model を使い回し、bind 結果の cache を効かせる。
// GetSemanticModel は呼ぶたびに新しい model を返し、本文の bind は model ごとにしか残らない
public sealed class SemanticModelCache(Compilation compilation)
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

    public SemanticModel this[SyntaxTree tree] =>
        _models.TryGetValue(tree, out var m) ? m
            : _models[tree] = compilation.GetSemanticModel(tree);
}

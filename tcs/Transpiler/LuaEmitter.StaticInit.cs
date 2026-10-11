using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 型の static initializer は型の定義と同じ位置では実行せず、chunk 内の全型を
// 定義してから型単位にまとめて出す。順は StaticInitOrder (initializer が読む
// 他の型を先に初期化する)。宣言順・ファイル順には依存しない。
public partial class LuaEmitter
{
    private readonly List<(SemanticModel Model, string Name, EmittedTypeInfo Info,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> Inits)>
        _pendingStaticInits = [];

    /// <summary>型 (Lua 名) → static initializer が初期化を要求する他の型
    /// (StaticInitOrder.DependenciesByName)。null なら宣言順。</summary>
    public IReadOnlyDictionary<string, List<string>>? StaticInitDependencies { get; set; }

    // static の pre-zero は型の定義より前 (module mode では descriptor の構築時)
    // に評価されるので、struct の zero 値は `S.new()` を呼ばず table literal で書く
    private string StaticDefaultLua(ITypeSymbol? type) =>
        IsUserStruct(type)
            ? "{" + string.Join(", ", ValueMembers(type).Select(m =>
                $"{m.Name} = {StaticDefaultLua(m.Type)}")) + "}"
            : GetDefaultValueForType(type);

    private static bool IsStaticField(FieldDeclarationSyntax field) =>
        field.Modifiers.Any(SyntaxKind.StaticKeyword)
        || field.Modifiers.Any(SyntaxKind.ConstKeyword);

    // C# は static field を default 値で事前初期化してから initializer を
    // 宣言順に実行する (循環参照 `a = b + 1; b = a + 1` が nil にならない)。
    // pre-zero は declare 側の意味論なので DeclRanges に載せ、hot apply の
    // define チャンクに含めない (live 値を上書きしないため)。
    private void EmitStaticPreZero(string name, EmittedTypeInfo info,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> staticFieldInits)
    {
        var preZeroStart = _sb.Length;
        foreach (var (fieldName, _, type) in staticFieldInits)
        {
            var defaultValue = StaticDefaultLua(type);
            if (defaultValue != "nil")
                AppendLine($"{name}.{fieldName} = {defaultValue}");
        }
        if (_sb.Length == preZeroStart) return;
        info.DeclRanges.Add((preZeroStart, _sb.Length - preZeroStart));
        AppendLine();
    }

    private void DeferStaticFieldInitializers(SemanticModel model, string name,
        EmittedTypeInfo info,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> staticFieldInits)
    {
        if (staticFieldInits.Count > 0)
            _pendingStaticInits.Add((model, name, info, staticFieldInits));
    }

    private void FlushStaticInitializers()
    {
        if (_pendingStaticInits.Count == 0) return;
        var pending = _pendingStaticInits.ToDictionary(p => p.Name);
        var deps = StaticInitDependencies;
        var order = StaticInitOrder.Order(_pendingStaticInits.Select(p => p.Name),
            n => deps?.GetValueOrDefault(n) ?? []);
        _pendingStaticInits.Clear();
        foreach (var name in order)
        {
            if (!pending.TryGetValue(name, out var p)) continue;
            p.Info.InitDependencies = deps?.GetValueOrDefault(name) ?? [];
            if (p.Inits.FirstOrDefault(i => i.Init != null).Init is { } first)
                SetSource(first);
            EmitStaticFieldInitializers(p.Model, p.Name, p.Info, p.Inits);
        }
    }
}

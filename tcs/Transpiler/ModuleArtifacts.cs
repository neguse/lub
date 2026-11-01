namespace TinyCs;

// module artifact と bridge snapshot の本体は SelfHost/Tool/Modules.cs (TinyC#)。
// ここは .NET の呼び出し側 (WasmCompiler とテスト) のための薄い入口

public static class ModuleArtifactText
{
    // define の chunk: module の RawLua から宣言の行と static の初期化の行を除いたもの
    public static string BuildDefineLua(string rawLua, IEnumerable<TcsSelf.ModuleType> types) =>
        TcsSelf.ModuleText.DefineLua(rawLua, [.. types]);

    // 型 1 個の static の初期化の chunk
    public static string BuildTypeInitializerLua(string rawLua, TcsSelf.ModuleType type) =>
        TcsSelf.ModuleText.TypeInitializerLua(rawLua, type);
}

public static class ModuleLinker
{
    public static string LinkSnapshot(
        IReadOnlyList<SessionModuleArtifact> artifacts,
        int revision,
        string? entryClass,
        string tinySystemLua,
        string registryLua,
        bool emitAck = false)
    {
        var lua = TcsSelf.ModuleLinker.LinkSnapshot([.. artifacts.Select(a => a.Port)], revision,
            entryClass ?? "", tinySystemLua, registryLua, emitAck);
        return lua ?? throw new InvalidOperationException(TcsSelf.ModuleLinker.Error);
    }
}

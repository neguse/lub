namespace TinyCs.Tcs2c;

// C runtime の string 節: TinySystem.String (runtime/tinysystem.lua) と
// Lua 標準関数 (string.upper / lower / format、tonumber) の C 実装。
// 意味論は Lua backend に揃える (plain find、byte 列)。
internal sealed partial class CEmitter
{
    private static readonly string RuntimeStrings = CoreRuntime("tcs_strings.c");
}

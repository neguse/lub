namespace TinyCs.Tcs2c;

// C runtime の library 節: string.format、Math (Lua 意味論)、List sort
// (.NET と同じ introsort と OrderBy の安定 merge sort、比較は生成側の lifted 関数)、Dict.Keys / Values、
// Console.Write、os.getenv。
internal sealed partial class CEmitter
{
    private static readonly string RuntimeLib = CoreRuntime("tcs_lib.c");
}

namespace TinyCs.Tcs2c;

// C runtime の collection 節: 固定長配列 (inline 要素)、growable List
// (RAW buffer)、chained hash Dictionary (bucket 倍化)。要素 layout は
// 生成側が確保時に渡し、GC がそれで要素内 pointer を trace する。
internal sealed partial class CEmitter
{
    private static readonly string RuntimeCollections = CoreRuntime("tcs_collections.c");
}

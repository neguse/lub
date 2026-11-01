namespace TinyCs.Tcs2c;

// C runtime の GC 節: フレーム同期の世代別 GC (T252)。
//
// - nursery: bump 確保の chunk 列。フレーム中 (entry の呼び出し中) は一切
//   GC しない。足りなければ chunk を足すだけ。
// - フレーム境界 (tcs_gc_frame。host が entry から戻った後に呼ぶ = C# の
//   スタックが空): static field / dirty な旧 object / host の hold slot から
//   到達する若い object だけを旧世代へ copy (Cheney、forwarding は header の
//   next)、残りの nursery は pointer のリセットで一括解放する。root は
//   全部精密で、C stack の走査は無い。
// - 旧世代: malloc 個別 + 連結 list。昇格 bytes が閾値を超えたら同じ境界で
//   mark-sweep (root は static + hold。この時点で若い object は無い)。
// - ライトバリア: 旧 object の参照型 slot への store で owner を dirty 登録
//   (tcs_wb)。生成側は field / 要素 / cell / struct 連鎖の store に挿し、
//   runtime は List / Dict の内部 store に挿す。static は毎境界で全走査
//   するのでバリア不要。
// 実行形 (main) は Main 全体が 1 フレームで GC は走らない (lib 出荷形が GC
// の対象)。
internal sealed partial class CEmitter
{
    private static readonly string RuntimeGc = CoreRuntime("tcs_gc.c");
}

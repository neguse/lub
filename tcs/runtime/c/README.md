# tcs2c の C runtime

tcs2c が生成する C の前に、この順に埋め込む: `prelude.h`、`../math`、`../fmt`、`../utf8`、`core.c`、`gc.c`、`collections.c`、`strings.c`、`lib.c`。
ファイルの中身はそのまま生成 C に入るので、説明はここに書く。

## core.c

heap object の共通形 (GC の header と layout)、文字列、配列、List、Queue、HashSet、Nullable、closure、Dictionary の表現と、それらの基本の操作。

## gc.c

C runtime の GC 節: フレーム同期の世代別 GC (T252)。

- nursery: bump 確保の chunk 列。フレーム中 (entry の呼び出し中) は一切
GC しない。足りなければ chunk を足すだけ。
- フレーム境界 (tcs_gc_frame。host が entry から戻った後に呼ぶ = C# の
スタックが空): static field / dirty な旧 object / host の hold slot から
到達する若い object だけを旧世代へ copy (Cheney、forwarding は header の
next)、残りの nursery は pointer のリセットで一括解放する。root は
全部精密で、C stack の走査は無い。
- 旧世代: malloc 個別 + 連結 list。昇格 bytes が閾値を超えたら同じ境界で
mark-sweep (root は static + hold。この時点で若い object は無い)。
- ライトバリア: 旧 object の参照型 slot への store で owner を dirty 登録
(tcs_wb)。生成側は field / 要素 / cell / struct 連鎖の store に挿し、
runtime は List / Dict の内部 store に挿す。static は毎境界で全走査
するのでバリア不要。
実行形 (main) は Main 全体が 1 フレームで GC は走らない (lib 出荷形が GC
の対象)。

## collections.c

C runtime の collection 節: 固定長配列 (inline 要素)、growable List
(RAW buffer)、chained hash Dictionary (bucket 倍化)。要素 layout は
生成側が確保時に渡し、GC がそれで要素内 pointer を trace する。

## strings.c

C runtime の string 節: TinySystem.String (runtime/tinysystem.lua) と
Lua 標準関数 (string.upper / lower / format、tonumber) の C 実装。
意味論は Lua backend に揃える (plain find、byte 列)。

## lib.c

C runtime の library 節: string.format、Math (Lua 意味論)、List sort
(.NET と同じ introsort と OrderBy の安定 merge sort、比較は生成側の lifted 関数)、Dict.Keys / Values、
Console.Write、os.getenv。

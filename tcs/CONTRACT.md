# spike 実装契約 (T212 PC 測定)

`docs/spike-ceiling.md` の PC 部分の実装仕様。全変種で bit 一致する digest を
出すため、演算列・定数・乱数を厳密にここで固定する。

## 共通

- f32 演算のみ。libm 禁止。C は `-O2 -ffp-contract=off -fwrapv`、float 演算は
  すべて `float` 型 (excess precision 排除: `-fexcess-precision=standard`)
- Lua 実行は LUA_32BITS ビルド (`../tcs/deps/lua/lua32`。CMake 変数
  `SPIKE_LUA32` で上書き可)。aot-hash / aot-slot の C 実装は同じ
  LUA_32BITS 構成の liblua を embed する (`../tcs/deps/lua` のソースから
  spike の CMake で LUA_32BITS 付きビルド)
- 乱数: LCG。`state = (state * 1103515245 + 12345) & 0x3FFFFFFF`、
  戻り値はその state。C は uint32_t で計算して & 後に int32 へ (Lua の
  整数 wrap + `&` と bit 一致)。初期 seed = 12345
- `frand(lo, hi)`: `lo + (lcg() % 1000) * (1/1000.0f) * (hi - lo)`
  (演算順そのまま。% は非負なので floor/trunc 差なし)
- dt = 1.0f/50.0f (定数)

## digest (FNV-1a 32bit)

- `h = 2166136261; h = (h ~ byte) * 16777619` を u32 wrap で
- f32 値は IEEE754 bit 列 4 byte (little endian) を byte 順に投入。
  Lua 側は `string.pack("<f", v)`
- 各 kernel の「digest 対象」列を全変種で同順に投入

## kernel 1: sprite_update

- N ∈ {256, 1024, 4096}、FRAMES = 1000
- sprite: x, y, vx, vy (f32), frame (int)
- 初期化 (i = 0..N-1 順): x=frand(0,400), y=frand(0,240),
  vx=frand(-60,60), vy=frand(-60,60), frame=lcg()%8
- 毎フレーム各 i 順:
  `x = x + vx*dt; y = y + vy*dt;`
  `if x < 0.0f then x = 0.0f; vx = -vx end; if x > 400.0f then x = 400.0f; vx = -vx end`
  y も同様に 0/240。`frame = (frame + 1) % 8`
- digest 対象: 全 i 順に x, y, vx, vy

## kernel 2: spawn_churn

- N = 1024 定員のリング、FRAMES = 1000、毎フレーム SPAWN = 32 体を
  生成し最古 32 体を破棄
- entity: x, y, vx, vy (f32)。生成時 x=frand(0,400), y=frand(0,240),
  vx=frand(-30,30), vy=frand(-30,30)
- 毎フレーム: 生存全体に sprite_update と同じ移動+反射 (frame 無し)
- naive 変種: 生成のたび新 allocation (Lua: 新 table / native: malloc+free)。
  pool 変種: 事前確保スロットの再利用
- digest 対象: 最終生存体を生成順に x, y, vx, vy

## kernel 3: particles

- N = 4096、FRAMES = 1000
- particle: px, py, vx, vy (f32)。初期化: px=frand(100,300),
  py=frand(50,200), vx=frand(-40,40), vy=frand(-40,40)
- 毎フレーム各 i 順:
  `vy = vy + 98.0f*dt`
  `px = px + vx*dt; py = py + vy*dt`
  境界反射 (sprite と同形、x:0..400, y:0..240、減衰 `v = -v*0.9f`)
  中心 (200,120) との距離判定: `dx=px-200; dy=py-120; d2=dx*dx+dy*dy;`
  `if d2 < 400.0f then px = px + dx*0.1f; py = py + dy*0.1f end`
- digest 対象: 全 i 順に px, py, vx, vy

## 変種 (docs/spike-ceiling.md の表)

- interp: 上記を素の Lua (sequence table / naive は table 生成)。
  sprite/particle は record 的 table `{x=..,y=..}` (hash access) で書く
- aot-hash: C から Lua C API で同じ table 構造を操作
  (lua_getfield/lua_setfield 相当。文字列 key は事前 intern した
  lua_pushstring + lua_rawget でよい)
- aot-slot: C から Lua table の配列部 slot (lua_rawgeti/lua_rawseti、
  1..4 番 slot = x,y,vx,vy) を操作
- native: 素の C struct 配列
- jit-off: luajit があれば `-joff` で interp と同じ Lua を実行 (無ければ skip)

## 測定・出力

- 実行時間: kernel×変種ごとに 7 回実行し median、ms/frame で出力
- 出力形式 (1 行 1 結果): `kernel,variant,N,ms_per_frame,digest`
- 全変種の digest 一致を harness が検証し、不一致は exit 1
- 成果物: `spike/` 以下に CMakeLists.txt + C ソース + lua ソース +
  `run.sh` (ビルド→実行→表形式サマリ)。結果は `spike/results-pc.md` へ
  (実測値表 + aot-slot/native 比 + 合否解釈のどちらに落ちたか)

## tcs 側実測 digest (T215 harness、lua32 実行)

TinyC# → Lua (tcs transpiler) 経由の digest。spike 変種はこれと一致すべき:

- sprite_update (N=256): `e8814b32` — spike 全変種と一致確認済み
- spawn_churn: `9274159d` — 同上 (解釈統一: 開始時に n 体充填、毎フレーム
  spawn 32 で最古を上書き → 生存全体を最古→最新順に更新)
- particles (N=4096): `8bf97e09` — 同上

3 kernel すべてで native C / aot-hash / aot-slot / interp / jit-off /
TinyC#→Lua (tcs) の 6 実装が bit 一致 (2026-07-18 PC 実測)。

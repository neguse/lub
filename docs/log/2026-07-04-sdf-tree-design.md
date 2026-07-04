# SDF ツリー設計 — data 契約化と C 評価

> 記録: 2026-07-04 時点の実装前設計。実装後の現状は `lubx/Sdf.hx` / `src/sdf.c` /
> `samples/19_sdf/` を見る。

## 目的

`samples/19_sdf` で実証した「SDF を書いてメッシュ化する」フローの SDF 表現を、
Haxe 関数(実行コード)から **素の data(ツリー)** に置き換える。

動機は 2 つ:

1. **評価の C 移行** — Lua 評価(56³ で数百 ms)を C 評価(64³ で ~10ms 目標)に。
   パラメータをいじりながらのリアルタイム remesh が成立する速度域に入れる。
2. **エディタ分離** — ツリーが data なら、将来のエディタ(スライダー/ギズモ)は
   data を直接編集して保存すればよく、コードへの書き戻し問題が発生しない。

## アーキテクチャ

```
[authoring]                  [契約層]                [runtime C]
Haxe builder (lubx.Sdf) ──→  SDF ツリー(素の table)──→ sdf_mesh: 評価 → surface nets → mesh
エディタ(将来)     ──→          〃
手書き dist()      ──────────────────────────→ grid 直埋め → surface_nets(既存経路を維持)
```

- ツリーは **関数参照を含まない素の table**。直列化(.lua data / JSON)可能で、
  Haxe からもエディタからも同じものを作れる。
- ツリーで書けない変態 SDF(noise、domain repetition 等)は従来どおり Haxe 関数で
  grid を埋めて `surface_nets(grid, ...)` に渡す。逃げ道は塞がない。
- 将来の GPU 化(ツリー → WGSL codegen)もこの契約の上に足す。

## 契約: ツリー schema (version 1)

```lua
{
  version = 1,
  root = {
    op = "smin", k = 0.22,
    a = { op = "move", x = 0, y = -0.42, z = 0,
          c = { op = "sphere", r = 0.72 } },
    b = { op = "move", x = 0, y = 0.48, z = 0,
          c = { op = "sphere", r = 0.46 } },
  },
}
```

| 分類 | op | フィールド | 備考 |
| --- | --- | --- | --- |
| prim | `sphere` | `r` | |
| prim | `box` | `hx hy hz` | half extents |
| prim | `capsule` | `ax ay az bx by bz r` | 線分 a-b + 半径 |
| prim | `torus` | `rmajor rminor` | XZ 平面 |
| xform | `move` | `x y z c` | `c` = 子 |
| xform | `rotate` | `qx qy qz qw c` | quat 格納。builder は axis-angle 受け |
| xform | `scale` | `s c` | uniform のみ(非一様は距離が歪むため除外) |
| combine | `union` | `a b` | |
| combine | `smin` | `k a b` | polynomial smooth min |
| combine | `subtract` | `a b` | a から b をくり抜く |
| combine | `ssub` | `k a b` | smooth subtraction |
| combine | `intersect` | `a b` | |
| misc | `mirror_x` | `c` | X 対称(\|x\| 折り畳み) |

- 任意ノードに `name`(string)を置ける。v1 では無視。将来の bones タグ・
  エディタのラベル・パラメータ UI のグルーピングに使う予約フィールド。
- 未知の op / version 不一致は `sdf_mesh` が luaL_error にする(黙って無視しない)。

## C ランタイム: `sdf_mesh`

```
sdf_mesh(tree, n) -> mesh
```

- `n` = 最長軸の cell 数。bounds はツリーから自動計算(下記)、cell は立方を保ち
  他軸の cell 数は extent 比で決める。
- 返す mesh は `load_gltf` / `surface_nets` と同じ規約
  (`positions/normals/indices/vert_count/index_count`)+ 評価に使った
  `bounds_min/bounds_max/cell`(エディタやデバッグ表示用)。
- 内部: ツリーを 1 回 flat な C 構造体配列に落としてから全 grid 点を評価
  (Lua API を評価ループに入れない)。評価は再帰(xform は p を変換して下降、
  combine は子の距離を合成、scale は距離を s 倍)。深さ上限 64。
- grid → mesh は既存 `surface_nets` の実装を関数として共有する。

### bounds 自動計算

各ノードが保守的 AABB を返す。厳密性は不要(外れないことだけ保証。grid は
AABB + 1 cell のマージンで切る):

| op | AABB |
| --- | --- |
| prim | 各形状の素の AABB |
| `move` / `scale` | 子 AABB を平行移動 / s 倍 |
| `rotate` | 子 AABB の 8 頂点を回して包み直す |
| `union` | a ∪ b |
| `smin` | (a ∪ b) を k/4 だけ外に膨らませる(blend の張り出し上限) |
| `subtract` / `ssub` | a(ssub は k/4 膨らませ) |
| `intersect` | a ∩ b |
| `mirror_x` | x を ±max(\|lo\|, \|hi\|) に対称化 |

### 性能目標

64³ = 26 万点 × ノード 30 個規模で **16ms 以内**(シングルスレッド)。
これを超える需要が出たら SIMD / スレッド / GPU compute の順に検討する(v1 対象外)。

## Haxe builder: `lubx.Sdf`

メソッドチェーンで、構築順 = 読み順にする:

```haxe
var body = Sdf.sphere(0.72).move(0, -0.42, 0);
var head = Sdf.sphere(0.46).move(0, 0.48, 0);
var arm = Sdf.capsule(new Vec3(0.56, -0.32, 0), new Vec3(1.04, 0.24, 0), 0.13);
var eye = Sdf.sphere(0.11).move(0.17, 0.56, -0.40);
var d = body.smin(head, 0.22).smin(arm.mirrorX(), 0.10).ssub(eye.mirrorX(), 0.06);
var mesh = Sdf.mesh(d, 64); // -> lub.Mesh.MeshData
```

- `SdfNode` は素の table を包む abstract。メソッドは新ノードを返すだけ
  (イミュータブル。共有部分ツリーの再利用可)。
- `Sdf.mesh(node, n)` が `{version = 1, root = node}` に包んで C の `sdf_mesh` を呼ぶ。
- C extern (`@:native("sdf_mesh")`) は `lub.Mesh` に置き、prelude shim に追加。
  lubx はその上の builder 層(core / lubx 境界は SpriteBatch と同じ整理)。

## 型付け方針

型は **builder の API 面にだけ**置く。ツリー本体は型付けしない。

- `SdfNode` のメソッドシグネチャで誤用はコンパイル時に弾ける。builder を通す限り
  不正なツリーは作れないので、ツリー内表現の型付けで防げる事故はほぼ残らない。
- Haxe enum ADT は Lua 出力が素の table にならず(constructor index 持ち)、
  wire format との二重表現 + 変換層を常時払うことになるためやらない。
  typedef + optional だらけの折衷は見た目だけ型で実質無検査なので最も中途半端。
- 代わりに **C の flatten を唯一のバリデータ**にして、うるさく死ぬ
  (未知 op・欠落フィールド・型不一致は即エラー)。hot reload 前提なら
  「保存 → 即エラー」で runtime 検証でもフィードバックは十分速い。
- 将来 Haxe 側でツリー処理が要る場合(WGSL codegen 等)は「table → ADT パーサ」を
  後付けする。wire 契約は不変のまま型付きビューだけ得られる。
- エディタ着手時は TS 側に独自の型定義を書く(wire 仕様は本 doc の表が source)。

## v1 スコープ

やる:

- `src/sdf.c`(flatten + 評価 + AABB + surface nets 直結)+ smoke test
  (球で数値検証、既存 surfacenets_smoke と同じ形式)
- `lub.Mesh.sdfMesh` extern + prelude shim
- `lubx/Sdf.hx` builder
- `samples/19_sdf` をツリー版に移行(見た目は現状の golden を維持)

やらない(将来、この設計の上に足す):

- **エディタ**: 段階案は (1) playground にツリー由来の自動スライダー(数値
  フィールドを列挙するだけで UI が作れる)→ (2) transform ノードのギズモ →
  (3) `.lua` data ファイルとして保存・ロード(エディタ authoring の source of truth)。
- **bones**: `name` 付き xform ノードをジョイントとみなし、メッシュ化後に
  頂点ごとの「パーツ距離」から skin weight を焼く(softmax、smin の k と整合)。
  ツリーが data なのでパーツ分解が機械的にできる。
- **material**: ノードに material id を許し、評価時に「最寄りパーツの id」を
  頂点属性として出す。
- **GPU**: ツリー → WGSL codegen → compute で grid 生成。契約は不変。
- **interval arithmetic 枝刈り**: 高解像度 CPU 評価が要るようになったら。

## テスト

- `tests/c/sdf_smoke.c` — 球ツリー: 頂点位置・法線・winding(既存 smoke と同基準)。
  ネスト(move+rotate+smin)の AABB が実際の表面を含むこと。未知 op がエラーになること。
- golden: 移行後の `19_sdf` が現状とほぼ同じ絵になること(bounds 自動化で grid が
  変わるため完全一致は求めず、golden は移行時に更新する)。
- web verify A5 は既存登録のまま。

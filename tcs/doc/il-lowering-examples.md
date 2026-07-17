# IL lowering 演習 — 抽象度の決定 (T210)

> Status: 設計成果物 (2026-07-18)。IL の抽象度（何を IL の概念として残し、
> 何を IL 構築時に lower するか）を、意地悪な構文の worked example で決める。
> 意味論の規範は `doc/il-spec.md`。ここは決定の根拠と backend 出力の見取り図。

記法: 本文の IL はテキスト擬似表記（説明用・非規範）。正規形は in-memory
モデル（il-spec §1）。演算子の `+i` / `*f` は「IL 演算ノードは型解決済みで
単型」であることを表す。

## E1: switch 式とパターンマッチング

```csharp
static float Area(object shape) => shape switch
{
    Circle { Radius: > 10 } => -1f,
    Circle c => 3.14f * c.Radius * c.Radius,
    Rect r => r.W * r.H,
    _ => 0f,
};
```

IL — パターンという概念は IL に存在しない。type test + cast + 比較 + if 連鎖
へ構築時に lower し、式位置の制御フローは temp 変数 + 文に statement 化する:

```text
func Program.Area(shape: ref object) -> f32 {
  let t0: ref object = shape          // 対象は 1 回だけ評価
  var result: f32
  if is(t0, Circle) && (t0 as Circle).Radius >f 10.0f {
    result = -1.0f
  } else if is(t0, Circle) {
    let c: ref Circle = t0 as Circle
    result = (3.14f *f c.Radius) *f c.Radius
  } else if is(t0, Rect) {
    let r: ref Rect = t0 as Rect
    result = r.W *f r.H
  } else {
    result = 0.0f
  }
  return result
}
```

IL→Lua（statement 化。現行の IIFE 方式は廃止）:

```lua
function Program.Area(shape)
  local t0 = shape
  local result
  if TCS.is(t0, Circle) and t0.Radius > 10.0 then
    result = -1.0
  elseif TCS.is(t0, Circle) then
    local c = t0
    result = (3.14 * c.Radius) * c.Radius
  ...
  return result
end
```

IL→C（object model は spike 合否解釈に従う。native 表現の場合の概形）:

```c
static float Program_Area(TcsObj *shape) {
  TcsObj *t0 = shape;
  float result;
  if (tcs_is(t0, &Circle_type) && ((Circle *)t0)->Radius > 10.0f) {
    result = -1.0f;
  } else if (tcs_is(t0, &Circle_type)) { ...
```

決定:

- **パターンは IL に無い**。type test（`is`）・cast・比較・if 連鎖へ lower する
- **式位置の制御フロー（switch 式、`?.`、`??`、`?:`）は IL に無い**。
  temp + 文へ statement 化する。現行 Lua 出力の IIFE
  （`(function() ... end)()`）は評価のたびに closure を割り当てており、
  statement 化はこれを除去する（性能 floor にも効く）
- **IL 演算ノードは型解決済みで単型**（i32 加算 / f32 加算 / 文字列連結は
  別ノード）。overload 解決・暗黙変換の挿入は Roslyn 層で完了している
- 対象式は 1 回評価、arm は宣言順に判定（C# の順序保存）

実測で見つけた現行バグ: 型 test が `getmetatable(x) == T` の完全一致のため、
派生インスタンスへの `is Base` が C# `true` / 現行出力 `false`（→ T222）。
IL の `is` は「T またはその派生」を規範とし、Lua backend は metatable chain を
辿る helper を義務とする。

## E2: ループ変数を捕捉する closure

```csharp
var fs = new List<Action>();
for (int i = 0; i < 3; i++) { fs.Add(() => Console.WriteLine(i)); }
foreach (var f in fs) f();   // C# は 3,3,3（for 変数はループ全体で 1 個）
```

実測: 実 .NET は `3 3 3`、現行 Lua 出力は numeric for へ変換するため
`0 1 2`（Lua の numeric for は反復ごとに新しい変数）。意味論バグ（→ T221）。

IL — closure は**値ではなく変数を捕捉**する。変数は identity を持つ実体で、
寿命は scope 構造が定める。ループは IL に来る前に while + block へ脱糖済み:

```text
{
  var i: i32 = 0                       // for 制御変数: ループ全体で 1 個
  while i <i 3 {
    call List.Add(fs, closure(fn_0, captures: [&i]))
    i = i +i 1
  }
}
```

IL→Lua（local が 1 個なので全 closure が同一 upvalue を共有し、C# と一致）:

```lua
do
  local i = 0
  while i < 3 do
    List.Add(fs, function() return print(i) end)
    i = i + 1
  end
end
```

IL→C: closure = { 関数ポインタ, 環境 }。捕捉されて脱出する変数は
heap cell へ box し、共有はセル共有で表現する。

決定:

- **capture は変数単位**（by variable）。「for 変数はループ全体で 1 個、
  foreach の反復変数は反復ごと」は脱糖後の scope 構造として IL に現れる
- **ループ構文は IL に無い**。while + block のみ（il-spec §8）
- Lua の numeric for は「制御変数が捕捉されていない」場合に限り backend が
  使ってよい最適化（観測等価の範囲）

## E3: struct 配列の更新（particles 型 kernel、M5 先行検討）

```csharp
struct Particle { public float X; public float VX; }
var ps = new Particle[n];
for (int i = 0; i < n; i++) { ps[i].X += ps[i].VX * dt; }
var p = ps[0];   // copy
p.X = 99f;       // ps[0].X は変わらない
```

IL — **place（格納場所: 変数 / フィールド path / 要素 path）を一級で持つ**。
place への部分書き込みは copy を伴わず、値文脈での読み出しが copy 地点:

```text
store ps[i].X, load(ps[i].X) +f (load(ps[i].VX) *f dt)   // in-place、copy 無し
var p: Particle = copy(load(ps[0]))                       // 値文脈 → copy
store p.X, 99.0f                                          // p のみ変更
```

IL→C: native struct / 連続配列へ直写（`ps[i].X += ps[i].VX * dt;`）。
IL→Lua: 表現候補は table of tables（copy は clone helper）か userdata 連続
バッファ。選択は `../luo/docs/spike-ceiling.md` の particles kernel 実測で
決める（T212 / T219）。IL 意味論は表現非依存に「要素は独立した place、
copy 地点で値が分離する」とだけ定める。

決定:

- IL は place / load / store で値型を表現し、**copy 地点を仕様で列挙**する
  （il-spec §10）。連続メモリ配置は backend 表現の自由であり IL の意味論
  ではない

## E4: 文字列補間

```csharp
Console.WriteLine($"x={x}, area={Area(c)}");
```

IL — 補間という概念は IL に無い。n-ary concat と数値→文字列 intrinsic へ
lower する:

```text
call Console.WriteLine(concat("x=", i32_to_str(x), ", area=", f32_to_str(call Program.Area(c))))
```

決定:

- 補間は concat + to_str intrinsic へ lower
- **数値→文字列は intrinsic として規範定義**する（i32 = 10 進、f32 =
  shortest round-trip 10 進）。Lua の `tostring`（`%.14g` 系）は f32 で
  規範と一致しないため、Lua backend は fmt helper を義務とする

## 決定まとめ（il-spec へ反映）

| # | 決定 | 由来 |
|---|---|---|
| 1 | パターン・switch 式・`?.` `??` `?:`・補間・ループ構文・property は IL に無い（構築時 lower） | E1 E2 E4 |
| 2 | 式位置の制御フローは temp + 文へ statement 化、IIFE 廃止 | E1 |
| 3 | IL 演算ノードは型解決済み・単型 | E1 |
| 4 | `is` は継承込み。null は常に偽 | E1（バグ T222） |
| 5 | capture は変数単位。変数は identity を持ち scope が寿命を定める | E2（バグ T221） |
| 6 | 制御フローは block / if / while / break / continue / return のみ | E2 |
| 7 | 値型は place / load / store + copy 地点列挙。メモリ配置は backend 自由 | E3 |
| 8 | 数値→文字列を含む runtime 表面は intrinsic として規範定義 | E4 |

現行実装についての副次的発見: emitter は bound tree（IOperation）ではなく
syntax tree + SemanticModel を走査している（il-design.md の記述を実態へ修正
済み）。IL builder も syntax + SemanticModel 走査を維持してよい — IL 化の
価値は IL 側にあり、builder の入力表現は二次的で、現行走査コードは資産。

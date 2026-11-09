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

IL→C（概形）:

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
- **式位置の制御フロー（switch 式、`?.`、`??`、`?:`）は IL に無い**。temp + 文へ statement 化する。前に文を置けない位置では式の位置の逐次実行 (IlIife) か条件式 (IlTernary) が残る。tcs VM は IlIife を結果の register を持つ block に、C は GNU statement expression に展開する (closure は作らない)
- **IL 演算ノードは型解決済みで単型**。数の演算は operand の型で決まり (i32 と f32 の加算は同じ `AddNum` で、backend が operand の型で i32 か f32 の演算を選ぶ)、文字列連結は別ノード (`Concat`)。overload 解決・暗黙変換の挿入は compiler の束縛の層で完了している
- 対象式は 1 回評価、arm は宣言順に判定（C# の順序保存）
- discard arm の無い非網羅 switch 式は、else 節を `switch-no-match` fault へ
  lower する（C# の SwitchExpressionException 相当。temp 未代入の穴を残さない
  — gpt-5.6 反証レビューの指摘）

IL の `is` は「T またはその派生、T を実装する class」を規範とする (il-spec §9、T222)。

## E2: ループ変数を捕捉する closure

```csharp
var fs = new List<Action>();
for (int i = 0; i < 3; i++) { fs.Add(() => Console.WriteLine(i)); }
foreach (var f in fs) f();   // C# は 3,3,3（for 変数はループ全体で 1 個）
```

tcs VM と C も `3 3 3` を出す (T221)。

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

IL→C: closure = { 関数ポインタ, 環境 }。捕捉されて脱出する変数は heap cell へ box し、共有はセル共有で表現する。tcs VM も捕捉する変数を cell に置き、closure は関数と捕捉の列を持つ。

決定:

- **capture は変数単位**（by variable）。「for 変数はループ全体で 1 個、
  foreach の反復変数は反復ごと」は脱糖後の scope 構造として IL に現れる
- **ループ構文は IL に無い**。while + block のみ（il-spec §8）
- 数値 for (IlNumericFor) は、制御変数が捕捉されず範囲が変わらないと builder が示せたときだけ IL に現れる。backend はこれを素の数値 for にしてよい（観測等価の範囲）

## E3: struct 配列の更新（particles 型 kernel）

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
IL→tcs VM: 配列の要素は C と同じ inline の連続配置で (object-model §5)、`ps[i].X` は要素の field の位置への load / store になる。copy は葉の register への load。
IL 意味論は表現非依存に「要素は独立した place、copy 地点で値が分離する」とだけ定める。

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
- **数値→文字列は intrinsic として規範定義**する（i32 = 10 進、f32 = shortest round-trip 10 進）。tcs VM と C は runtime/fmt の同じ実装で文字列にする (il-spec §13)

## 決定まとめ（il-spec へ反映）

| # | 決定 | 由来 |
|---|---|---|
| 1 | パターン・switch 式・`?.` `??` `?:`・補間・ループ構文・property は IL に無い（構築時 lower） | E1 E2 E4 |
| 2 | 式位置の制御フローは temp + 文へ statement 化。残る IlIife は backend が block に展開する (closure にしない) | E1 |
| 3 | IL 演算ノードは型解決済み・単型 | E1 |
| 4 | `is` は継承込み。null は常に偽 | E1（バグ T222） |
| 5 | capture は変数単位。変数は identity を持ち scope が寿命を定める | E2（バグ T221） |
| 6 | 制御フローは block / if / while / break / continue / return のみ | E2 |
| 7 | 値型は place / load / store + copy 地点列挙。メモリ配置は backend 自由 | E3 |
| 8 | 数値→文字列を含む runtime 表面は intrinsic として規範定義 | E4 |
| 9 | 非網羅 switch 式の no-match は fault。f32 リテラルは IL が bit 値で保持（二重丸め禁止） | 反証レビュー |

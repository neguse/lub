# TinyC# IL 仕様 v0 (T211)

> Status: draft v0 (2026-07-18)。**意味論の正本**。位置づけと経緯は
> `doc/il-design.md`、抽象度の決定根拠は `doc/il-lowering-examples.md`。
> 本文 §1-§15 は backend に依存せず規範を定める。各 backend の義務は付録。

## 1. スコープと適合

- IL は Roslyn で検査・診断済みの TinyC# プログラムの表現。IL は自前の
  型検査を持たず、型不整合な IL の挙動は未定義
- TinyC# プログラムの実行意味 = 本仕様が IL に与える意味。backend は
  **観測等価**な実行を提供する義務を負う。観測とは: Console 出力の列、
  エントリポイントの完了または fault (§12)、interop 境界 (§15) を通過する値
- backend は観測等価を保つ任意の変形を行ってよい。ただし §6 の f32 制約は
  観測等価の定義に含まれる（禁止事項は変形で導入できない）
- v0 の正規形は in-memory モデル（Transpiler 内の .NET オブジェクト）。
  テキスト表記は診断用で非規範。シリアライズ形式は M2 (T217) で決定

## 2. プログラム構造

module / class / record class / enum / interface / method / field。

- property・indexer・event・ループ構文・パターン・式位置の制御フロー・
  文字列補間は IL に存在しない（構築時 lowering 済み。examples 決定 1-2）
- enum は i32 定数の集合。実行時表現は i32
- interface は型検査の痕跡のみで実行時表現を持たない。interface を対象と
  する type test / cast はサブセット外（診断 → T223。実行時に interface を
  発見する表現が無いため。実行時表現の付与は需要駆動で再検討）
- エントリポイントは static Main 相当の単一 method

## 3. 型

`i32` / `f32` / `bool` / `string` / `ref C`（class / record class）/
`V`（struct / record struct、M5 で有効化）/ `T[]` / `List<T>` /
`Dictionary<K,V>` / 関数型（closure）/ enum (= i32)。

- 型引数は消去済み。IL ノードはすべて単型（examples 決定 3）
- null は ref・string・List・Dict・関数型の値。i32 / f32 / bool / V は
  非 null。Nullable<T> の位置づけは v0 未決（付録 C）
- double / long はサブセット外（M4 で診断化。il-design §4）

## 4. 評価モデル

- 式の operand は**厳密に左→右**で評価する。副作用は逐次で、backend が
  観測可能な並べ替えをしてはならない
- 演算ノードは型解決済み・単型。overload 解決・暗黙変換の挿入・定数畳み込みの
  可否判断は Roslyn 層で完了している
- 短絡 `and` / `or` は bool 専用ノード（右 operand は必要時のみ評価）。
  条件式 `?:` は IL に無い（temp + if へ statement 化済み）

## 5. i32 意味論

- 32bit 2 の補数。add / sub / mul は wrap（C# unchecked と同一）
- div / rem: 0 方向切り捨て、rem の符号は被除数。除数 0 は fault。
  `INT_MIN / -1` および `INT_MIN % -1` は fault（C# の OverflowException 相当）
- 比較・ビット演算・シフトはサポート演算子集合（support-matrix §4.2 が正）
  について C# unchecked と同一の意味
- i32 ↔ f32 変換は明示ノード。f32 → i32 は 0 方向丸め。変換元が NaN または
  i32 で表現できない値のときは fault（C# は unchecked では値未規定のため、
  決定的な定義を IL が与えても C# サブセットと矛盾しない。digest workload と
  fuzz はこの入力を生成しない）

## 6. f32 意味論

- IEEE 754 binary32。各演算ノードは single rounding（演算ごとに f32 へ丸める）
- **再結合・縮約（fma 化）・精度の昇格/降格は観測等価とみなさない**。
  backend は式の演算列を IL のまま保存する
- NaN / ±Inf / −0 は IEEE 準拠。比較は IEEE（NaN との順序比較・`==` は
  false、`!=` は true。−0 == +0）
- **fp モード**: 上記 strict が既定。出荷ビルド単位の明示 opt-in で
  **relaxed-fp** を選択でき、縮約（fma 化）・再結合・中間精度の昇格を許可
  する。relaxed でも IEEE special values の意味論（NaN / Inf / −0 の比較・
  伝播）は維持する — finite-math 系（NaN 不存在仮定）は許可しない。
  relaxed 下では f32 の丸め列が実装定義になるため、f32 値に依存する観測
  （出力値、値依存の fault 位置）の backend 間一致は保証されず、digest
  一致ゲートは strict モードでのみ適用される
- リテラルは 10 進表記から最近接丸めで f32 へ写す。この丸めは IL 構築時に
  **一度だけ**起こり、IL はリテラルを f32 bit 値として保持する（10 進
  テキストを backend へ渡さない。double 経由の二重丸めは 1 bit の差を生む）

## 7. 変数・スコープ・capture

- 変数は名前ではなく identity を持つ実体。scope（block 構造）が寿命を定める
- closure は**変数を捕捉**する（値ではない。examples 決定 5）。同じ変数を
  捕捉する closure 同士・外側 scope は、その変数の読み書きを共有する
- ループは IL に来る前に while + block へ脱糖済み。「`for` の制御変数は
  ループ全体で 1 個」「`foreach` の反復変数は反復ごと」という C# の規定は、
  脱糖結果の scope 構造として表現される（現行バグ T221 はこの規範で修正）

## 8. 制御フロー

block / if / while / break / continue / return のみ（examples 決定 6）。
goto は無い。例外機構は無い — try / throw はサブセット外（TCS1001 維持）で、
実行時の異常は fault (§12) として扱う。

## 9. 参照型

- class インスタンスは identity を持つ heap 値。フィールドは place (§10)
- new: 全フィールドを default 値（i32=0, f32=0.0, bool=false,
  ref/string/List/Dict/関数型=null）で初期化した後、constructor 本体を実行
- type test（`is T`、T は class に限る — §2）: 実行時型が T またはその派生
  なら真。null は常に偽（現行バグ T222 はこの規範で修正）
- cast: Roslyn が安全と検査済みの位置では無検査。明示 cast の失敗は fault
- 仮想呼び出し: 単一継承、override は実行時型で解決
- class の参照比較（operator 定義が無い `==`）は identity 比較

## 10. place と値型（M5 v1 で「データ struct」= field のみを有効化。
member 付き struct / record struct は引き続きサブセット外）

place = 格納場所。変数、フィールド path、配列/List 要素 path の 3 種。

- place への store は in-place 更新であり copy を伴わない
  （`s.F = v`、`a[i].X = v`）
- 値型 V の **copy 地点**（この列挙が規範。examples 決定 7）:
  1. 代入（`var p = q` / place への V 値の store）
  2. 引数渡し（値渡し）
  3. return
  4. 値文脈での place 読み出し（`var p = a[i]`、`var q = s.Inner`）
- struct 配列の要素は互いに独立した place。連続メモリ配置は backend 表現の
  自由であり IL の意味論ではない
- 値型の `==` は operator 定義がある場合のみ（既定の構造等価は v0 に無い）

## 11. 配列・List・Dictionary・string

- `T[]`: 固定長、**0-based**。範囲外 index は fault。`new T[n]` は
  default 値で初期化
- List / Dict: IL は runtime 型として扱い、メンバー呼び出しは intrinsic
  call (§13)。意味論の規範は TinySystem dotnet facade（T209 で System 委譲
  実装済み）+ `known-differences.json`。null 保存制約は TCS1003 の診断が守る
- string: **不変の octet 列**。Length・index・IndexOf 等はバイト単位。
  source literal は UTF-8 符号化で octet 列へ写す。孤立 surrogate を含む
  literal はこの写像が存在しないためサブセット外（診断）。octet 列は
  valid UTF-8 を不変条件としない（バイト単位の Substring 等が非 UTF-8 列を
  生成しうる）。C# frontend（UTF-16 code unit）との差は known-differences が
  正本（support-matrix §27）。
  判断: 出荷ターゲットの表現（octet 列）を規範に置き、開発時 .NET 実行を
  「非 ASCII で長さ系が異なる既知差」とする。逆にすると release backend が
  UTF-16 文字列を背負う
- string の `==` は内容比較。参照等価は観測に含まれない（intern の自由）

## 12. fault

fault = 決定的に検出される実行時異常。発生した fault はプログラムを即時
停止し、回復手段は無い。

- 種別: 除数 0 (§5)、i32 除算 overflow (§5)、f32→i32 変換不能 (§5)、
  null 参照への member アクセス、配列/List/string の範囲外 index、
  明示 cast の失敗 (§9)、switch 式の no-match（discard の無い非網羅
  switch 式で一致 arm が無い場合。C# の SwitchExpressionException 相当 —
  例外がサブセット外のため fault として定義）、
  runtime intrinsic が定義する error
- 同一入力・同一プログラムなら、どの backend・どの lowering モードでも
  同一の観測列の後に fault する（fault の位置も観測の一部）
- fault 時の診断情報（メッセージ、source 位置）は品質事項であり適合条件では
  ない
- 実装は上記に加えて実装定義の資源 fault（allocation 失敗等）を持ってよい。
  資源 fault の発生位置は backend 間一致の対象外

## 13. intrinsic

- TinySystem runtime 表面（Math / String / List / Dict / Random / Console）
  の member 集合が intrinsic 集合。member 一覧は support-matrix Part II が正
- 意味論の規範 = TinySystem dotnet facade + known-differences.json。
  IL 仕様はこれを重複記述しない（conformance sweep / differential が
  実行可能な正本として既に担保している）
- 数値→文字列（補間・Console 出力・ToString）:
  i32 は 10 進最短表記、f32 は shortest round-trip 10 進表記が規範
- 数学関数（Sin / Cos / Sqrt 等）の規範は「同一プラットフォーム上で全
  backend が同一値」。プラットフォーム間のビット一致は保証しない。
  digest workload は数学関数を使わない（spike の libm 排除方針と同一）
- Random は backend 間一致の対象外（v0）。合意 PRNG の導入は付録 C

## 14. migration metadata（v0 はスキーマのみ。実装は T220）

class ごとに: layout version hash / field 列（名前、IL 型、initializer）/
rename 注釈（`[RenamedFrom]` 相当）/ ユーザーフック（`OnReload` 相当）。
プロトコルの語彙と適用規則は il-design §6（CLOS 型 eager migration）。

## 15. interop 境界

- 境界面 = class の public メンバー（method・field・lowering 済み accessor）
- 外部コードとの相互作用は境界面の呼び出し・読み書きのみが規約内。
  インスタンス表現への直接操作は規約外（backend は表現を自由に選べる）
- release 実行では境界面のうち到達可能な export のみが保証対象（M3 で精密化）

## 付録 A: Lua backend の義務（dev-lowering）

- 0-based → 1-based の index 変換
- idiv / irem 補正（実装済み）+ 除算 overflow / 変換 fault の検出 (§5, §12)
- capture を保存するループ生成 — numeric for は制御変数が捕捉されていない
  場合のみ使用可（T221）
- 式位置制御フローの statement 化（IIFE 廃止。examples 決定 2）
- type test の継承対応 helper（metatable chain 走査。T222）
- f32 shortest round-trip の fmt helper（`tostring` の `%.14g` 系は不適合）
- f32 リテラルは bit 値を正確に表す表記（16 進浮動小数リテラル等）で出力
  （10 進経由の二重丸め禁止 — §6）
- 値型の copy helper（M5。表現は spike 実測後に決定）
- `LUA_32BITS` ビルドでの実行（M4）

## 付録 B: C backend の義務（release-lowering、`../luo`）

- signed wrap の保証（`-fwrapv` 相当）
- strict（既定）: `-ffp-contract=off`、fast-math 系最適化の禁止、
  excess precision の排除（`FLT_EVAL_METHOD=0` 相当）(§6)
- relaxed-fp（opt-in）: `-ffp-contract=fast` と再結合を許可。
  `-ffinite-math-only` / `-ffast-math` 一括指定は不可（NaN/Inf 意味論を
  壊すため）(§6)
- bounds / null / 除算 check の生成。省略は観測等価を証明できる場合のみ
- object model（class 表現・struct 配列表現）は spike 合否解釈に従う
- fault の trap 実装（§12 の決定性を満たすこと）

## 付録 C: 未決事項

- Nullable<T> の位置づけ
- relaxed-fp (§6) の粒度を module 単位まで細分するか（mixed-mode ABI と
  絡むため v0 は出荷ビルド単位のみ）
- 合意 PRNG（Random の backend 間一致）
- シリアライズ形式（M2 / T217）
- Lua 側 struct 配列表現・release 側 class 表現（spike T212 待ち）
- mixed-mode の module 境界 ABI（il-design §5）

# AOT性能上界spike

release backendのobject model選択(Lua table/GCを保つか、IL-nativeな
struct/連続配列にするか)に必要な性能データを、compilerを書く前に
手書きCで実測する。

## 背景: 方向性の決定リスト

tcs/luo/lubをまたぐ再方向づけの決定(2026-07のdesign session)。
architecture.mdの全面改稿までの間、正本はここに置く。IL仕様の設計文書は
tcs側に置く予定。

1. 正本は自前の小さなIL仕様。意味論の核はC#形
   (i32/f32、値型/参照型の区別、0-based配列、C#の除算規則)。
   tcsはRoslyn frontend→IL、LuaとCは対称な2つのbackend
2. luoは「IL→C release backend」へ。region guard / VM patch /
   bundle動的loadの現行機構はlegacy
3. loweringは2モード: dev-lowering(layout間接参照つき、データ移行可能)と
   release-lowering(layout凍結)。dev-loweringは当面IL→Lua backendで実装し、
   stock Lua runtimeを流用する(限界が来た部分からIL直実装へ置換可)
4. 時間分割: dev = loose実行(データ型変更追従reload)、release = 全面AOT・
   静的link。dev buildはmodule粒度の降格/昇格mixed-mode。in-process
   codegenはどのターゲットでも行わない(JITではない)
5. データ移行はCLOS型プロトコル(eager migration、layout diff、rename注釈、
   ユーザーフック)をILのmigration metadata仕様として定義
6. 段階導入: (i) tcs内部を bound tree→IL→Lua出力 に再編(挙動不変)、
   (ii) ILの隣にIL→C backendを追加、(iii) 必要になった部分だけ
   dev-loweringをIL直実装へ
7. 性能floor = Playdate級。KPIは下記。本spikeの結果は
   release-loweringのobject model設計の入力になる

## KPI floor

- HW: Playdate (STM32F746 Cortex-M7 180MHz単コア、RAM 16MB、
  FPv5-SP = f32のみ。doubleはソフトエミュのため使用禁止)
- workload: 2D sprite更新、400x240、50Hz
- スクリプト予算: 10ms/frame
- 描画はspikeの対象外(stub)。lubの2D API設計とは独立

数値モデルは全ターゲット `LUA_32BITS`(int32 + f32)に統一する。
PC測定も同構成でbuildし、digest一致を全変種・全環境で要求する。

## 測定変種

| 変種 | 実体 | PC | 実機 |
|---|---|---|---|
| interp | PUC Lua 5.5 (LUA_32BITS) | o | o |
| jit-off | LuaJIT 2.1 `-E -joff` 参考値 | o | 不可(Cortex-M非対応) |
| aot-hash | 理想(a)手書きC、named-field(table hash access) | o | o |
| aot-slot | 理想(a)手書きC、array-slot layout | o | o |
| native | 理想(b)手書きC、素のstruct | o | o |

「理想(a)手書きC」= whole-program compilerが生成するはずのコードを人手で
書いたもの。Lua tableを実在させ、Lua C API/内部アクセスで操作する。
実機ではpdex.binに自前Lua 5.5を埋め込む(この成立確認自体もspikeの成果物。
Panic同梱Luaは5.4ベース改造のため使わない)。

## workload kernels

1. `sprite_update`: N sprites {x, y, vx, vy, frame, ...} のfield-heavy数値
   更新+壁バウンド。固定work、N ∈ {256, 1024, 4096}、1000 frames、
   固定seed
2. `spawn_churn`: 生成/破棄を含む変種でallocation/GC軸を露出させる。
   naive版(毎frame new)とpool版の両方
3. `particles`: Vec2値演算中心のstruct-heavy kernel(積分+反射+距離判定)。
   値型・連続配列の有無で差が最大化する場所を実測し、
   「Lua的表現の対価」を分離して見る

## 測定軸

- 実行時間: 7-run median、フレームあたりms
- メモリ: bytes/entity(変種間比較)、GC pause p95(interp / aot-*)
- 正当性: f32量子化値のFNV digestが全変種・全環境で一致

f32決定性のため `-ffp-contract=off`、libm関数はkernelから排除する。

## 合否解釈

- aot-slotがnativeの1.3x以内、かつ実機でN=1024が予算10ms内
  → release-loweringでもLua table object modelを維持してよい
    (backend間でheap表現を共有でき、実装が最も軽い)
- aot-*のみ予算落ち、nativeは通る → release-loweringはIL-native表現
  (struct/連続配列)を採る。値型はいずれにせよIL-native
- nativeも落ちる → workload/KPIの再交渉

## 先行タスク

- Playdate SDK + arm-none-eabi toolchain導入(Linux機に未導入を確認済み)
- ベンチ実行は事前の所要見積もり+明示timeoutで行い、出力はファイルへ保存

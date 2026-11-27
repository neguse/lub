# perf — 性能実測ハーネス

tcs の実行系 (tcs VM の dev / C の release) の性能と数値一致を実測するハーネス。

## 構成

- `compare/` — 外向けの比較ベンチ。同じ TinyC# のプログラム (Benchmarks Game 由来の 6 本と
  ゲームの 2 本) を tcs の 3 経路 (VM / C / .NET) で動かし、C、Rust、Go、Java、JS、
  Python の版と並べる。全実装の出力の一致が前提で、`run.py --check` が一致だけを見る (手で走らせる)。
  測り方と公平さの決まりは `docs/tcs/perf-compare.md`
- `selfbench/` — tcs 自身 (compiler) の速さの回帰。tcs のソースを tcs VM の .tcsb と C に変換する時間、命令の数 (cachegrind)、managed heap の最大、出力の大きさを測る。
  tcs を動かす実行モードは 3 つある。`c` (C の tcs) は compiler の処理、`tcs c` の出力、C の runtime の回帰を拾う。`vm` (tcs VM の上の tcs。`tcs_on_vm.c` が tcs 自身の .tcsb を動かす) は VM の interpreter と `tcs vm` の出す bytecode の回帰を拾う。`dotnet` は時間だけを測る。
  CI の selfbench job は PR の base と、`c` と `vm` で `tcs vm` を比べ、命令の数と heap の最大が 3% を超えて増えたら失敗する (時間は測らない)。

## KPI floor (il-design の性能前提)

- HW: Playdate (STM32F746 Cortex-M7 180MHz 単コア、RAM 16MB、FPU は f32 のみ)
- workload: 2D sprite 更新、400x240、50Hz、スクリプト予算 10ms/frame
- 数値モデルは全ターゲット i32 + f32。digest 一致を全変種・
  全環境で要求 (f32 決定性のため -ffp-contract=off、libm は kernel から排除)

# perf — 性能実測ハーネス

tcs の実行系 (tcs VM の dev / C の release) の性能と数値一致を実測するハーネス。

## 構成

- `compare/` — 外向けの比較ベンチ。同じ TinyC# のプログラム (Benchmarks Game 由来の 6 本と
  ゲームの 2 本) を tcs の 3 経路 (VM / C / .NET) で動かし、C、Rust、Go、Java、JS、
  Python の版と並べる。全実装の出力の一致が前提で、`run.py --check` が一致だけを見る (手で走らせる)。
  測り方と公平さの決まりは `compare/METHODOLOGY.md`
- `selfbench/` — tcs 自身 (compiler) の速さの回帰。tcs のソースを C の tcs と .NET の tcs で
  tcs VM の .tcsb と C に変換する時間、命令の数 (cachegrind)、managed heap の最大、出力の大きさを測る。
  CI の selfbench job は PR の base と C の tcs の `tcs vm` を比べ、命令の数と heap の最大が 3% を超えて増えたら失敗する (時間は測らない)。

## KPI floor (il-design の性能前提)

- HW: Playdate (STM32F746 Cortex-M7 180MHz 単コア、RAM 16MB、FPU は f32 のみ)
- workload: 2D sprite 更新、400x240、50Hz、スクリプト予算 10ms/frame
- 数値モデルは全ターゲット i32 + f32。digest 一致を全変種・
  全環境で要求 (f32 決定性のため -ffp-contract=off、libm は kernel から排除)

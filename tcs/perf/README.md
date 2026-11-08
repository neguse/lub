# perf — 性能実測ハーネス

tcs の実行系 (VM dev / C release) の性能と数値一致を実測する恒久ハーネス。
旧称「AOT 性能上界 spike」— 2026-07-18 の PC 実測で合否は決着済みで、
以後は性能回帰の測定基盤・実機測定の受け皿として維持する。

## 構成

- `CONTRACT.md` — kernel 仕様 (LCG / 演算列 / FNV digest)。tcs の C と VM の経路
  (`Transpiler.Tests/DigestKernels/`) と native の変種が bit 一致すべき正本
- `core.c` — OS 非依存の kernel 共通部 (LCG / frand / digest)。
  ホストとPlaydate の両ビルドに入る
- `native.c` — native struct の変種
- `run.sh` — ビルド → 7-run median 計測 → digest の一致の検証 → 表出力 (stdout)
- `bench-2backend.sh` — 実パイプライン比較: 同一 TinyC# kernel を
  vm (tcs→IL→tcs2vm→tcsvm。`tcsvm/`、doc/vm-design.md) と release (tcs→IL→tcs2c→C) で実行。
  digest 相互一致が fail ゲート、ms/frame はレポート (CI の bench-2backend job で毎 push 実行、
  結果は step summary と workflow artifact `bench-2backend-results` の CSV)
- `compare/` — 外向けの比較ベンチ。同じ TinyC# のプログラム (Benchmarks Game 由来の 6 本と
  ゲームの 2 本) を tcs の 3 経路 (VM / C / .NET) で動かし、C、Rust、Go、Java、JS、
  Python の版と並べる。全実装の出力の一致が前提で、CI (compare-check job) は
  一致だけを見る。測り方と公平さの決まりは `compare/METHODOLOGY.md`、結果は `compare/results/`
- `selfbench/` — tcs 自身 (compiler) の速さの回帰。tcs のソースを C の tcs と .NET の tcs で
  tcs VM の .tcsb と C に変換する時間、命令の数 (cachegrind)、managed heap の最大、出力の大きさを測る。
  PR では base と交互に測り、命令の数と heap の最大が 3% を超えて増えたら失敗する (selfbench job)
- `results-pc.md` — PC 実測記録 (2026-07-18。当時の Lua の変種と並べた表)
- `playdate/` — Playdate 実機/シミュレータ用ハーネス。native の変種を
  update 1 回 1 job で実行し、CSV を console log と
  `Data/dev.neguse.tcs.perf/results.csv` (シミュレータでは
  `$PLAYDATE_SDK_PATH/Disk/Data/` 配下) の両方へ出す

## KPI floor (il-design の性能前提)

- HW: Playdate (STM32F746 Cortex-M7 180MHz 単コア、RAM 16MB、FPU は f32 のみ)
- workload: 2D sprite 更新、400x240、50Hz、スクリプト予算 10ms/frame
- 数値モデルは全ターゲット i32 + f32。digest 一致を全変種・
  全環境で要求 (f32 決定性のため -ffp-contract=off、libm は kernel から排除)

## 決着済みの合否解釈 (2026-07-18 PC 実測)

aot-slot/native ≈ 17x、interp/native ≈ 19-36x — aot-slot は interp と
大差なく、boxed TValue 表現自体がボトルネック。
→ **release-lowering は IL-native 表現 (struct / 連続配列) を採る** (確定。
tcs2c はこの方針で実装済み)。数値表は `results-pc.md`。

## Playdate ビルド

前提: Playdate SDK (`PLAYDATE_SDK_PATH`) + arm-none-eabi toolchain。

```bash
cd perf/playdate
# 実機向け (.pdx は tcs_perf_DEVICE.pdx へ)
cmake -B build-device \
  -DCMAKE_TOOLCHAIN_FILE=$PLAYDATE_SDK_PATH/C_API/buildsupport/arm.cmake \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build-device
# シミュレータ向け (.pdx は tcs_perf.pdx へ)
cmake -B build-sim -DCMAKE_BUILD_TYPE=Release
cmake --build build-sim
# 実機へ転送: pdutil install tcs_perf_DEVICE.pdx
```

## 残り

- 実機 (Playdate) 測定: `tcs_perf_DEVICE.pdx` を実機で実行し、
  N=1024 が予算 10ms 内かを確認して結果を results-playdate.md に記録する
  (2026-07-18: デバイスビルドと、シミュレータでの全 job 完走 +
  全 digest の results-pc.md 一致まで確認済み。実機実行のみ未)

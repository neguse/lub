# perf — 性能実測ハーネス

2 backend (Lua dev / C release) の性能と数値一致を実測する恒久ハーネス。
旧称「AOT 性能上界 spike」— 2026-07-18 の PC 実測で合否は決着済みで、
以後は性能回帰の測定基盤・実機測定の受け皿として維持する。

## 構成

- `CONTRACT.md` — kernel 仕様 (LCG / 演算列 / FNV digest)。tcs transpiler
  経由 (`Transpiler.Tests/DigestKernels/`) と全変種が bit 一致すべき正本
- `core.c` — OS 非依存の kernel 共通部 (LCG / frand / digest)。
  ホストとPlaydate の両ビルドに入る
- `native.c` / `aot.c` / `interp/` — 変種実装 (native struct / Lua C API
  hash・slot / 素の Lua)
- `run.sh` — ビルド → 7-run median 計測 → 全変種 digest 一致検証 → 表出力
- `bench-2backend.sh` — 実パイプライン比較: 同一 TinyC# kernel を
  dev (tcs→Lua→lua32) と release (tcs→IL→tcs2c→C) で実行。digest 相互一致が
  fail ゲート、ms/frame はレポート (CI の bench-2backend job で毎 push 実行、
  結果は step summary と workflow artifact `bench-2backend-results` の CSV)。
  tcs2c 未対応 kernel (struct 系) は dev のみ
- `results-pc.md` — PC 実測記録 (2026-07-18)
- `playdate/` — Playdate 実機/シミュレータ用ハーネス。全変種
  (native / aot-hash / aot-slot / interp = 埋め込み lua32 + bench.lua) を
  update 1 回 1 job で実行し、CSV を console log と
  `Data/dev.neguse.tcs.perf/results.csv` (シミュレータでは
  `$PLAYDATE_SDK_PATH/Disk/Data/` 配下) の両方へ出す

## KPI floor (il-design の性能前提)

- HW: Playdate (STM32F746 Cortex-M7 180MHz 単コア、RAM 16MB、FPU は f32 のみ)
- workload: 2D sprite 更新、400x240、50Hz、スクリプト予算 10ms/frame
- 数値モデルは全ターゲット LUA_32BITS (i32 + f32)。digest 一致を全変種・
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
  (2026-07-18: デバイスビルドと、シミュレータでの全 24 job 完走 +
  全 digest の results-pc.md 一致まで確認済み。実機実行のみ未)
- 実機の update watchdog: aot/interp 系は 1 job (1000 frame) が
  数十秒かかる見込みで、update 長時間ブロックで落ちる場合は
  フレーム分割 (複数 update に跨る実行) が必要になる

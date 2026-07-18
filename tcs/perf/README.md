# perf — 性能実測ハーネス

2 backend (Lua dev / C release) の性能と数値一致を実測する恒久ハーネス。
旧称「AOT 性能上界 spike」— 2026-07-18 の PC 実測で合否は決着済みで、
以後は性能回帰の測定基盤・実機測定の受け皿として維持する。

## 構成

- `CONTRACT.md` — kernel 仕様 (LCG / 演算列 / FNV digest)。tcs transpiler
  経由 (`Transpiler.Tests/DigestKernels/`) と全変種が bit 一致すべき正本
- `native.c` / `aot.c` / `interp/` — 変種実装 (native struct / Lua C API
  hash・slot / 素の Lua)
- `run.sh` — ビルド → 7-run median 計測 → 全変種 digest 一致検証 → 表出力
- `results-pc.md` — PC 実測記録 (2026-07-18)

## KPI floor (il-design の性能前提)

- HW: Playdate (STM32F746 Cortex-M7 180MHz 単コア、RAM 16MB、FPU は f32 のみ)
- workload: 2D sprite 更新、400x240、50Hz、スクリプト予算 10ms/frame
- 数値モデルは全ターゲット LUA_32BITS (i32 + f32)。digest 一致を全変種・
  全環境で要求 (f32 決定性のため -ffp-contract=off、libm は kernel から排除)

## 決着済みの合否解釈 (2026-07-18 PC 実測)

aot-slot/native ≈ 17x、interp/native ≈ 19-36x — aot-slot は interp と
大差なく、boxed TValue 表現自体がボトルネック。
→ **release-lowering は IL-native 表現 (struct / 連続配列) を採る** (確定。
luoc はこの方針で実装済み)。数値表は `results-pc.md`。

## 残り

- 実機 (Playdate) 測定: SDK + arm-none-eabi toolchain 導入後に
  `run.sh` の変種を実機で再実測し、N=1024 が予算 10ms 内かを確認する

# AOT 性能上界 spike: PC 結果

- 測定: kernel/variant ごとに 7 回実行した median（ms/frame）
- 正当性: 各 workload について全変種・全 7 run の FNV-1a digest 一致を確認
- Lua: `/home/neguse/ghq/github.com/neguse/tcs/deps/lua/lua32`（LUA_32BITS）
- LuaJIT: jit-off included: LuaJIT 2.1.1783773675 -- Copyright (C) 2005-2026 Mike Pall. https://luajit.org/

```csv
kernel,variant,N,ms_per_frame,digest
sprite_update,native,256,0.001099250,e8814b32
sprite_update,aot-hash,256,0.034398539,e8814b32
sprite_update,aot-slot,256,0.018846323,e8814b32
sprite_update,interp,256,0.020994999,e8814b32
sprite_update,jit-off,256,0.076506000,e8814b32
sprite_update,native,1024,0.004356346,d4a095ee
sprite_update,aot-hash,1024,0.136689341,d4a095ee
sprite_update,aot-slot,1024,0.073085043,d4a095ee
sprite_update,interp,1024,0.081533998,d4a095ee
sprite_update,jit-off,1024,0.306979000,d4a095ee
sprite_update,native,4096,0.017611641,0040a0c1
sprite_update,aot-hash,4096,0.547758906,0040a0c1
sprite_update,aot-slot,4096,0.294068781,0040a0c1
sprite_update,interp,4096,0.332136005,0040a0c1
sprite_update,jit-off,4096,1.243820000,0040a0c1
spawn_churn_naive,native,1024,0.002363183,9274159d
spawn_churn_naive,aot-hash,1024,0.117679208,9274159d
spawn_churn_naive,aot-slot,1024,0.061528636,9274159d
spawn_churn_naive,interp,1024,0.093704998,9274159d
spawn_churn_naive,jit-off,1024,0.366359000,9274159d
spawn_churn_pool,native,1024,0.002132407,9274159d
spawn_churn_pool,aot-hash,1024,0.110582950,9274159d
spawn_churn_pool,aot-slot,1024,0.059596525,9274159d
spawn_churn_pool,interp,1024,0.088292003,9274159d
spawn_churn_pool,jit-off,1024,0.366078000,9274159d
particles,native,4096,0.012153455,8bf97e09
particles,aot-hash,4096,0.422544275,8bf97e09
particles,aot-slot,4096,0.238825275,8bf97e09
particles,interp,4096,0.439085990,8bf97e09
particles,jit-off,4096,3.185832000,8bf97e09
```

## native 比

| kernel | N | aot-slot/native | aot-hash/native | interp/native |
|---|---:|---:|---:|---:|
| sprite_update | 256 | 17.14x | 31.29x | 19.10x |
| sprite_update | 1024 | 16.78x | 31.38x | 18.72x |
| sprite_update | 4096 | 16.70x | 31.10x | 18.86x |
| spawn_churn_naive | 1024 | 26.04x | 49.80x | 39.65x |
| spawn_churn_pool | 1024 | 27.95x | 51.86x | 41.40x |
| particles | 4096 | 19.65x | 34.77x | 36.13x |

## 合否解釈

PC 比率では sprite_update N=1024 の aot-slot/native が 16.78x で 1.3x 条件外のため、「release-lowering は IL-native 表現」側に該当する（10ms 条件の最終判定は実機測定待ち）。

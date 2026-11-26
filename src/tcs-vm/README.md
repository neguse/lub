# tcsvm — IL から直接 bytecode を出す dev 実行系

TinyC# の IL (`IlExport`) を bytecode (`.tcsb`) にし、型付きの register VM で実行する。tcs の dev 実行系 (開発と hot reload) で、release は C (`tcs c`)。
設計は `docs/tcs/vm-design.md`、hot reload の契約は `docs/tcs/hot-reload-design.md`、C backend と共有する型記述子と reload の計画は `docs/tcs/object-model.md`。

| 場所 | 中身 |
|---|---|
| `src/tcs-vm/` | VM (C11)。`tcsvm.h` が API、`tcsvm_ops.h` が命令の一覧、`tcsvm_load.c` が読み込みと検査、`tcsvm_run.c` が interpreter、`tcsvm_rt.c` が string / List / Dictionary / Sort、`tcsvm_lib.c` が runtime の関数 (`CALLR`)、`tcsvm_host.c` が host の関数と host 向けの API、`tcsvm_gc.c` が GC、`tcsvm_reload.c` が hot reload (`tvm_apply`)、`tcsvm_debug.c` が `--trace` の記録と検査の口、`tcsvm_main.c` が CLI |
| `src/tcs-vm/gen-ops.py` | `tcsvm_ops.h` から `src/tcs/VmBackend/Op.cs` を作る |
| `tests/tcs-vm/` | テストの入力。各ファイルの `Main` を VM で走らせ、`expected/` の stdout と比べる (C backend が動かせる入力は C の stdout とも) |
| `tests/tcs-vm/run-tests.sh` | テスト (直下の入力、`tests/tcs/samples/` の 6 本、digest kernel の固定の digest、host / trace / inspect / profile / mixed / modules / watch の試験)。`tests/tcs/run-tests.sh` から呼ばれる |
| `tests/tcs-vm/fuzz-loader.py` | `.tcsb` を変異させて loader と verifier の頑健さを見る |

## 使い方

```bash
cmake -S tests/tcs-vm -B build-tcs-vm -DCMAKE_BUILD_TYPE=Release && cmake --build build-tcs-vm --target tcsvm
scripts/tcs.sh vm your.cs -o out.tcsb
build-tcs-vm/tcsvm/tcsvm out.tcsb
```

`--entry Class` は `Main` を持つ class が複数あるときに選ぶ。性能の比較は `tests/tcs-perf/compare/`。

## hot reload

`tcs vm --reload-from` が旧版と新版から新版の module と reload の計画 (`docs/tcs/object-model.md` §6) を作り、
`tvm_apply` がフレーム境界で当てる。CLI では `--lib` の形で何フレーム目の後に当てるかを指定する。

```bash
scripts/tcs.sh vm --export 'Game.*' v1.cs -o v1.tcsb
scripts/tcs.sh vm --export 'Game.*' --reload-from v1.cs --plan v2.plan v2.cs -o v2.tcsb
build-tcs-vm/tcsvm/tcsvm --lib Game 4 --reload 2 v2.tcsb v2.plan v1.tcsb
```

restart が要る差分 (static の initializer の変更、型の削除、生きている lambda を消した等) は終了コード 3 で、理由を stderr に書く。
`tcs vm --watch` は編集ごとに reload の batch を標準出力に流し、`--reload-batch K BATCH` で当てられる (`docs/tcs/vm-design.md` §13.3)。
テストは `tests/tcs-backends` の `VmReloadTests` と `VmReloadCaseTests`、`tests/tcs-vm/host/watch_test.py`。

## 範囲

VM は C backend と同じ IL を受け、C と .NET との stdout の一致を差分のゲートで確かめる (`docs/tcs/vm-design.md` §16)。
VM が受けない IL は `tcs vm` が `... is not in the VM yet` で止める。

関数は entry、`--export` で選んだ関数、static initializer から到達するものだけを作る (TinySystem.Linq の特殊化を全部は作らない)。
GC は動かさない mark-sweep で、確保の地点 (frame を保存してから) とフレーム境界で回収する (`--gc-stress` は毎回回収して root の漏れを出力の違いで見つける)。
型記述子、layout、heap の walker、reload の計画は C backend と共有する (`docs/tcs/object-model.md`)。

## 読み込みの検査

`.tcsb` は読み込み時に検査する (`docs/tcs/vm-design.md` §9.4)。register の種類は関数の
中で固定なので、命令ごとに operand の種類表と照らす 1 pass で済む。field と配列の
命令は operand の register の型 (class / 配列) から field の種類と offset を確かめ、
field の番号を byte offset に書き換える。

```bash
gcc -std=c11 -O1 -g -fsanitize=address,undefined -fno-sanitize-recover=all \
  src/tcs-vm/*.c src/tcs-runtime/sort/*.c src/tcs-runtime/fmt/*.c src/tcs-runtime/math/*.c src/tcs-runtime/utf8/*.c src/tcs-runtime/model/*.c \
  -o /tmp/tcsvm_asan -lm
python3 -I tests/tcs-vm/fuzz-loader.py /tmp/tcsvm_asan 3000 1 a.tcsb b.tcsb
```

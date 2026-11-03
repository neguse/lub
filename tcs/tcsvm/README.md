# tcsvm — IL から直接 bytecode を出す dev 実行系

TinyC# の IL (`IlExport`) を Lua を経ずに bytecode (`.tcsb`) にし、型付きの
register VM で実行する。設計は `doc/vm-design.md`。dev 実行系として採ることは決定済み
(同書 §18 D-V1)。いまは V1 と V2 の一部 (同書 §17.3、§17.4)。

| 場所 | 中身 |
|---|---|
| `Emitter/` | `tcs2vm` (.NET)。`tcs vm` を動かす入口 (IL → `.tcsb` の本体は TinyC# の `SelfHost/VmBackend/`、CLI は `SelfHost/Tool/VmRun.cs`)。C にした tcs の `tcs vm` も同じ `.tcsb` を出す。`--dump-il` で IL の診断用テキスト |
| `vm/` | VM (C11)。`tcsvm.h` が API、`tcsvm_ops.h` が命令の一覧、`tcsvm_load.c` が読み込みと検査、`tcsvm_run.c` が interpreter、`tcsvm_rt.c` が string / List / Dictionary / Sort、`tcsvm_gc.c` が GC、`tcsvm_reload.c` が hot reload (`tvm_apply`)、`tcsvm_main.c` が CLI |
| `gen-ops.py` | `tcsvm_ops.h` から `SelfHost/VmBackend/Op.cs` を作る |
| `tests/` | 差分テストの入力。各ファイルの `Main` を Lua と VM で走らせて比べる |
| `run-tests.sh` | 差分テスト (`tests/`、`samples/` の 6 本、digest kernel)。`run-tests.sh` (root) から呼ばれる |
| `fuzz-loader.py` | `.tcsb` を変異させて loader と verifier の頑健さを見る |

## 使い方

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build --target tcsvm
dotnet run --project tcsvm/Emitter -- samples-or-your.cs -o out.tcsb
build/tcsvm/tcsvm out.tcsb
```

`--entry Class` は `Main` を持つ class が複数あるときに選ぶ。性能の比較は
`perf/bench-2backend.sh` (dev = lua32、vm、release = tcs2c の 3 列)。

## hot reload

`tcs2vm --reload-from` が旧版と新版から新版の module と reload の計画 (`doc/object-model.md` §6) を作り、
`tvm_apply` がフレーム境界で当てる。CLI では `--lib` の形で何フレーム目の後に当てるかを指定する。

```bash
dotnet run --project tcsvm/Emitter -- --export 'Game.*' v1.cs -o v1.tcsb
dotnet run --project tcsvm/Emitter -- --export 'Game.*' --reload-from v1.cs --plan v2.plan v2.cs -o v2.tcsb
build/tcsvm/tcsvm --lib Game 4 --reload 2 v2.tcsb v2.plan v1.tcsb
```

restart が要る差分 (static の initializer の変更、型の削除、生きている lambda を消した等) は終了コード 3 で、
理由を stderr に書く。テストは `tcs2c.Tests` の `VmReloadTests`。

## いまの範囲

int / float / bool / enum / string、class (field、instance method、constructor と field の
initializer)、struct (local と配列要素と static)、配列、`List<T>`、`Dictionary<K, V>`
(key は int / enum / string)、lambda と method group (捕捉は cell、il-spec §7)、
`List.Sort` (比較の closure を runtime から入れ子で呼ぶ)、TinySystem.Linq、static field と
static method、if / while / do-while / for / foreach / break / continue / `?:` / `??`、
checked 演算、`Console.WriteLine`、fault と C# の行の trace。
範囲外の IL は `tcs2vm` が `... is not in the VM yet` で止める。

関数は entry と static initializer から到達するものだけを作る (TinySystem.Linq の特殊化を
全部は作らない)。GC は動かさない mark-sweep で、確保の地点 (frame を保存してから) とフレーム境界で
回収する (`--gc-stress` は毎回回収して root の漏れを出力の違いで見つける)。型記述子、layout、
heap の walker、reload の計画は tcs2c と共有する (`doc/object-model.md`)。
残りの範囲 (文字列の書式の一部、host 呼び出しなど) は `doc/tasks.md` の VM トラック。

## 読み込みの検査

`.tcsb` は読み込み時に検査する (`doc/vm-design.md` §9.4)。register の種類は関数の
中で固定なので、命令ごとに operand の種類表と照らす 1 pass で済む。field と配列の
命令は operand の register の型 (class / 配列) から field の種類と offset を確かめ、
field の番号を byte offset に書き換える。

```bash
gcc -std=c11 -O1 -g -fsanitize=address,undefined -fno-sanitize-recover=all \
  tcsvm/vm/*.c runtime/sort/*.c runtime/fmt/*.c runtime/math/*.c runtime/utf8/*.c runtime/model/*.c \
  -o /tmp/tcsvm_asan -lm
python3 -I tcsvm/fuzz-loader.py /tmp/tcsvm_asan 3000 1 a.tcsb b.tcsb
```

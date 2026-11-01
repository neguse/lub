# tcsvm — IL から直接 bytecode を出す dev 実行系 (試作)

TinyC# の IL (`IlExport`) を Lua を経ずに bytecode (`.tcsb`) にし、型付きの
register VM で実行する。設計は `doc/vm-design.md`、この版はその V1
(同書 §17、結果は §17.3)。

| 場所 | 中身 |
|---|---|
| `Emitter/` | `tcs2vm` (.NET)。IL → `.tcsb`。`--dump-il` で IL の診断用テキスト |
| `vm/` | VM (C11)。`tcsvm.h` が API、`tcsvm_ops.h` が命令の一覧、`tcsvm_main.c` が CLI |
| `tests/` | 差分テストの入力。各ファイルの `Main` を Lua と VM で走らせて比べる |
| `run-tests.sh` | 差分テスト (`tests/` と digest kernel)。`run-tests.sh` (root) から呼ばれる |
| `fuzz-loader.py` | `.tcsb` を変異させて loader と verifier の頑健さを見る |

## 使い方

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build --target tcsvm
dotnet run --project tcsvm/Emitter -- samples-or-your.cs -o out.tcsb
build/tcsvm/tcsvm out.tcsb
```

`--entry Class` は `Main` を持つ class が複数あるときに選ぶ。性能の比較は
`perf/bench-2backend.sh` (dev = lua32、vm、release = tcs2c の 3 列)。

## V1 の範囲

int / float / bool、class (field と、field の struct を葉に展開したもの)、struct
(local と配列要素と static)、配列、static field と static method、if / while /
do-while / for / break / continue、`Console.WriteLine` (int / float / bool)。
範囲外の IL は `tcs2vm` が `... is not in V1` で止める。

GC は無く、確保は bump だけ (tcs2c の実行形と同じく、Main 全体が 1 フレーム)。
hot reload、string、collection、closure、interface、host 呼び出しは V2 以降
(`doc/vm-design.md` §17)。

## 読み込みの検査

`.tcsb` は読み込み時に検査する (`doc/vm-design.md` §9.4)。register の種類は関数の
中で固定なので、命令ごとに operand の種類表と照らす 1 pass で済む。field と配列の
命令は operand の register の型 (class / 配列) から field の種類と offset を確かめ、
field の番号を byte offset に書き換える。

```bash
gcc -std=c11 -O1 -g -fsanitize=address,undefined -fno-sanitize-recover=all \
  tcsvm/vm/tcsvm.c tcsvm/vm/tcsvm_main.c runtime/fmt/f2s.c -o /tmp/tcsvm_asan -lm
python3 -I tcsvm/fuzz-loader.py /tmp/tcsvm_asan 3000 1 a.tcsb b.tcsb
```

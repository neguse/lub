# 比較ベンチの測り方と決まり

`perf/compare` は、同じ TinyC# のプログラムを tcs の各経路で動かした時間と、同じアルゴリズムを他の言語で書いたものの時間を並べる。
外に見せる数字なので、ここに書いた決まりを守って書き、測る。

## 何を並べるか

tcs の経路 (1 つの `<Entry>.cs` から作る):

| id | 経路 |
|---|---|
| `tcs-vm` | `tcs vm` → `.tcsb` → `build/tcsvm/tcsvm`。開発と hot reload の経路 |
| `tcs-c` | `tcs c` → C → gcc -O2。release の経路 |
| `tcs-dotnet` | 同じソースを .NET (Release、JIT) で動かす。`TinySystem.props` で Math と Dictionary は tcs と同じ実装 |

比べる相手 (`<Entry>.<拡張子>`、手で書く):

| id | 言語と build |
|---|---|
| `c` | C、gcc -O2 -ffp-contract=off -fwrapv。倍率の基準 |
| `rust` | Rust、rustc -C opt-level=3 |
| `go` | Go、go build |
| `java` | Java、javac と java (HotSpot、flag の調整なし) |
| `js` | JavaScript、Node.js |
| `python` | Python 3 (CPython) |

C# の手書きの版は置かない。TinyC# は C# のサブセットで、`tcs-dotnet` がそのまま C# と .NET の行になる。

## プログラム

| name | 見るもの |
|---|---|
| startup | 何もしないプログラム。起動と終了の時間 (倍率のまとめからは除く) |
| fib | 再帰呼び出し |
| fannkuch | 小さな int の配列の操作と分岐 (Benchmarks Game の fannkuch-redux) |
| binarytrees | 小さな object の確保と GC (Benchmarks Game の binary-trees) |
| nbody | float の演算と object の field (Benchmarks Game の n-body) |
| spectralnorm | 二重ループの float の積和と int の除算 (Benchmarks Game の spectral-norm) |
| mandelbrot | 内側のループの float の演算と分岐 (Benchmarks Game の mandelbrot と同じ範囲と反復) |
| sprites | ゲーム: class の sprite 1024 個を List に持ち毎フレーム更新 |
| particles | ゲーム: struct の配列の particle 4096 個に重力と反射 |

Benchmarks Game 由来のものは、その 1 スレッドの版と同じ手順を TinyC# で書き直したもの。
出力は Benchmarks Game の形と違い、検算できる最小の行にしている。

## 書き方の決まり

- アルゴリズムと演算の順序は `.cs` と同じにする。`.cs` が仕様で、他の言語はそこから写す
- 数値は int32 と float32 に揃える。tcs の数値は i32 と f32 だけ (support-matrix の `double` / `long`) なので、他の言語も float32 で計算する。JS は `Math.fround`、Go は `float32`、Java は `float`。整数の溢れは 32 bit で折り返す
- float32 を持たない Python は float64 で計算する。結果は表で ※ を付け、出力を相対 1e-3 の許容差で比べる
- 1 スレッド。SIMD、unsafe、自前の pool や arena、手での展開は使わない。各言語の標準ライブラリで素直に書く
- object は C# と同じ形で確保する。binarytrees の node は 1 つずつ確保し、sprites は sprite ごとの object を列に持つ。C は malloc と free
- サイズは環境変数 `BENCH_N` で渡す。compile 時の定数にしないことで、C などの compiler が計算を前もって畳めないようにする

## 正しさの確かめ方

- 各プログラムは結果を数行の文字列で出す。float の結果は 10^6 倍などして 0 の方向へ切り捨てた整数にする
- `run.py` は、正確な実装 (※ 以外) の出力がすべて byte で一致し、計測の各回でも同じであることを確かめてから時間を記録する。1 つでも違えば exit 1 で、その実装の時間は記録しない
- CI は `run.py --check` (小さいサイズで出力の一致だけ見る) を回し、他の言語の版が腐らないようにする。時間は CI では測らない

## 時間の測り方

2 つの数字を出す。

- 1 回あたり: 起動と JIT の立ち上がりを除いた計算の速さ。Go の `testing.B` や BenchmarkDotNet が回数を決めて回し、回数で割るのと同じ考え方を、プロセスの外から行う
- 起動から終了まで: 本体を 1 回動かすプロセスの時間。スクリプトとして使ったときの体感に近い

1 回あたりの出し方:

- 各プログラムは本体を `Run(n, print)` に持ち、Main が環境変数 `BENCH_REPS` の回数くり返す (既定 1)。出力は最後の回だけ出すので、回数によらず同じ
- 次の回の入力を前の回の結果に依存させる (`prev == -1 ? n + 1 : n`。結果は -1 にならない)。compiler が同じ計算を外に出したり、使われない回を省いたりできないようにするため。返す値は計算した値の全部を含める (例えば binarytrees はすべての check の和)
- run.py は k を倍々に増やし、T(k 回) − T(1 回) が 1 秒ほどになる k を選ぶ (`--target`)
- T(1 回) と T(k 回) を交互に 5 組測り (1 回が 10 秒を超える実装は 3 組)、1 回あたり = (median T(k) − median T(1)) ÷ (k − 1)。組ごとの値の幅も記録する
- 差を取るので、プロセスの起動、runtime の初期化、JIT の立ち上がりの分が消える。JIT の言語は温まった後の速さになる

共通:

- 1 回の実行は `launcher.c` が fork と exec で起動し、wall の時間 (CLOCK_MONOTONIC) と peak RSS (wait4 の ru_maxrss) を取る。Python から直接 fork すると exec の前の Python のメモリが peak RSS に入るため
- `--check` は BENCH_REPS=0 で何も出さないこと (くり返しを実装していること) と、BENCH_REPS=2 で出力が変わらないことも確かめる
- 倍率は各プログラムで C を 1 としたもの。まとめは startup を除く全プログラムの倍率の幾何平均 (全プログラムが揃った実装だけ)
- C との差が 10% 未満の倍率は揺れの範囲として同着とみなす。クラウドの VM では 1 回が 0.1 秒の C でも median と min が 30% 違うことがあった

## 公開する数字を取るとき

- 静かなマシンで、他の重い処理を止めて測る。`--pin <CPU>` で子プロセスを 1 つの CPU に固定できる
- 可能なら CPU の周波数を固定する (Linux なら governor を performance にする)
- 結果の JSON には日時、tcs の commit、CPU、OS、各言語の版が入る。`report.py` の表と HTML にもそのまま出る
- 測ったら `results/` に JSON と、`report.py` で作った md と html を置く

## 使い方

```bash
python3 perf/compare/run.py --check            # 出力の一致だけ (数十秒)
python3 perf/compare/run.py --pin 2            # 本番 (1 時間ほど。Python が大半)
python3 perf/compare/run.py -p fib,nbody -i c,tcs-c,tcs-vm --runs 3
python3 perf/compare/report.py perf/compare/results/<file>.json
```

必要なもの: gcc、cmake、.NET SDK (`global.json` の版)。他の言語は見つからなければ飛ばす。

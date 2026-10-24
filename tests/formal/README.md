# tests/formal

hot reload(tcs `--watch --reload-chunks` と lub の host)と `lub --serve` の契約を、実装から吸い出した主張のまま書いて機械検査する。
検査が見るのは「書いたモデル」であって実装そのものではないので、実装の契約を変えたら spec も直す。
経緯と反例の台帳は `docs/log/2026-10-25-hot-reload-formal-check.md`。

| ファイル | 対象 | 道具 |
| --- | --- | --- |
| `TcsNative.tla` | tcs と native の lub(`src/main.c`、`src/app.c`)。guard の assert が起きないこと(safety)と VM が最新 build に追いつくこと(liveness) | TLA+ / TLC |
| `TcsServe.tla` | tcs と serve のページ(`src/serve.c`、`src/embedded_serve_page.h`)。接続・取得・起動・F5・再接続の全順序 | TLA+ / TLC |
| `serve_conns.py` | `serve_tick` の接続の片づけ。健全な接続を切らないこと | 全列挙 |
| `serve_bounds.py` | `serve.c` のバッファ上限の算術 | z3 |
| `serve_conns_repro.py` | 接続の片づけの実機再現(手で回す。CI では回さない) | socket |

`run.sh` が一式を回す。
モデルごとに期待する判定(成立 / 不変条件の違反 / 時相の違反)を持ち、違えば失敗する。
反例が出るべき設定(plan の失敗、chunk の失敗、読み込みの失敗、mtime の記録を失敗の後に読む)で反例が出ることも同時に見るので、常に緑で何も検証していない状態にはならない。

```bash
# CI と同じ (java と z3-solver が要る)
tests/formal/run.sh
# java が PATH に無いとき、z3 を venv に入れたとき
JAVA="mise exec java@25 -- java" PYTHON=.venv/bin/python tests/formal/run.sh
```

TLC の jar は版と checksum を固定して `.cache/` に取ってくる。

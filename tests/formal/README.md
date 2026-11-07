# tests/formal

`lub --serve`(`src/serve.c`)の契約を、実装から吸い出した主張のまま書いて機械検査する。
検査が見るのは「書いたモデル」であって実装そのものではないので、実装の契約を変えたら検査も直す。
経緯と反例の台帳は `docs/log/2026-10-25-hot-reload-formal-check.md`。

| ファイル | 対象 | 道具 |
| --- | --- | --- |
| `serve_conns.py` | `serve_tick` の接続の片づけ。健全な接続を切らないこと | 全列挙 |
| `serve_bounds.py` | `serve.c` のバッファ上限の算術 | z3 |
| `serve_conns_repro.py` | 接続の片づけの実機再現(手で回す。CI では回さない) | socket |

`run.sh` が `serve_conns.py` と `serve_bounds.py` を回す。
どちらも、直す前の実装や壊した変種で反例が出ることも同時に見るので、常に緑で何も検証していない状態にはならない。

```bash
# CI と同じ (z3-solver が要る)
tests/formal/run.sh
# z3 を venv に入れたとき
PYTHON=.venv/bin/python tests/formal/run.sh
```

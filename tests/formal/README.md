# tests/formal

`lub --serve`(`src/serve.c`)のバッファの上限の算術を、実装から吸い出した式のまま z3 で全入力について確かめる(`serve_bounds.py`)。
検査が見るのは書いた式であって実装そのものではないので、`serve.c` の上限の式を変えたら検査も直す。
`{{ENTRY}}` の数だけは `src/embedded_serve_page.h` から数える。
壊した変種で反例が出ることも同時に見るので、常に緑で何も検証していない状態にはならない。
経緯と反例の台帳は `docs/log/2026-10-25-hot-reload-formal-check.md`。

```bash
# CI と同じ (z3-solver が要る)
tests/formal/run.sh
# z3 を venv に入れたとき
PYTHON=.venv/bin/python tests/formal/run.sh
```

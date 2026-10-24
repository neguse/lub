#!/usr/bin/env python3
"""src/serve.c のバッファ上限の算術を z3 で全入力について確かめる (契約としてロックする側)。

- json_escape: 1 byte は最長 6 byte (\\u00XX) に膨らむ。
- append_json_string: 予約 len + n*6 + 4 (',' + '"' + 6n + '"' + NUL)。
- serve_tick の reload/restart: cap = body_len*6 + 64 に "event: restart\\ndata: \"" + 6*body + "\"\\n\\n" + NUL。
- handle_index: cap = page_len + entry_len*4 + 64 に、{{ENTRY}} (9 byte) を k 回 entry に置き換えた page + NUL。
  k は実際のテンプレートで数える。
"""
import re, sys, pathlib
from z3 import Int, Solver, And, Or, Not, Implies, unsat, sat, ForAll

def prove(name, vars_, premise, claim):
    s = Solver()
    s.add(premise, Not(claim))
    r = s.check()
    if r == unsat:
        print(f"PROVED   {name}")
        return True
    print(f"REFUTED  {name}: witness {s.model()}")
    return False

ok = True
n, length = Int("n"), Int("len")
# append_json_string: 書く量 <= 予約量
need = 1 + 1 + 6 * n + 1 + 1          # ',' '"' escaped '"' NUL
ok &= prove("append_json_string reserve", [n], n >= 0, length + need <= length + n * 6 + 4)

body = Int("body_len")
hdr = len('event: restart\ndata: "')  # reload より長い方
ok &= prove("serve_tick sse frame cap", [body], body >= 0, hdr + 6 * body + 3 + 1 <= body * 6 + 64)

repo = pathlib.Path(__file__).resolve().parents[2]
src = (repo / "src/embedded_serve_page.h").read_text(encoding="utf-8")
k = src.count("{{ENTRY}}")
print(f"template has {k} occurrence(s) of {{{{ENTRY}}}}")
page_len, entry_len = Int("page_len"), Int("entry_len")
expanded = page_len - 9 * k + entry_len * k + 1   # + NUL
ok &= prove(f"handle_index cap (k={k})", [page_len, entry_len],
            And(page_len >= 9 * k, entry_len >= 0), expanded <= page_len + entry_len * 4 + 64)
# broken variant: k = 5 なら成り立たない (検査が効いていることの確認)
assert not prove("handle_index cap (k=5, broken variant)", [page_len, entry_len],
                 And(page_len >= 45, entry_len >= 0), page_len - 45 + entry_len * 5 + 1 <= page_len + entry_len * 4 + 64)
sys.exit(0 if ok else 1)

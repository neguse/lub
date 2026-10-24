#!/usr/bin/env python3
"""src/serve.c serve_tick の「非 SSE 接続を読む for」の全列挙 (有限で決定的なのでツール無しの総当たり)。

実装: tick の頭で pfds を conns の並びのまま作って poll し、conns[i] の revents を pfds[i+1] で見る。
conn_close は末尾の接続を slot i に詰める (swap-remove)。recv が <= 0 なら close して i-- する。
accept はその tick の poll の後なので、新しい接続は pfds に無い。

主張 (吸い出し): 接続を閉じるのは「request を返した後」か「相手が閉じた (recv == 0)」ときだけ。
検査: 接続ごとの socket の状態 (data あり / 無し / 相手が閉じた) と tick の途中で accept された接続を全部列挙し、
主張に反する close (= 健全な接続を黙って切る) が出るかを見る。
"""
import itertools, sys

IDLE, READY, EOF = "idle", "ready", "eof"   # socket: 何も届いていない / request が揃っている / 相手が閉じた

def tick(socks, accepted, fixed):
    # conn = (id, socket 状態, poll したときの pfds の index か None)
    conns = [(f"c{i}", s, i + 1) for i, s in enumerate(socks)]
    pfds = [None] + [s in (READY, EOF) for s in socks]   # POLLIN は data か EOF で立つ
    npfds = len(pfds)
    conns += [(f"a{i}", s, None) for i, s in enumerate(accepted)]
    closed = []
    i = 0
    while i < len(conns):
        pfd_idx = i + 1
        if pfd_idx >= npfds:
            break
        if not fixed:
            pollin = pfds[pfd_idx]                     # 現状: slot の位置で revents を引く
        else:
            own = conns[i][2]                          # 修正案: その接続自身の revents を引く (無ければ今回は読まない)
            pollin = own is not None and pfds[own]
        if not pollin:
            i += 1
            continue
        cid, s, _ = conns[i]
        if s == READY:
            closed.append((cid, "served"))
            conns[i] = conns[-1]; conns.pop()          # handle_request → conn_close (swap-remove)。i はそのまま進む
            i += 1
        else:
            # recv: EOF なら 0、data の無い非 blocking socket なら -1 (EAGAIN)。どちらも n <= 0 で close
            closed.append((cid, "eof" if s == EOF else "EAGAIN"))
            conns[i] = conns[-1]; conns.pop()          # i-- して同じ slot をもう一度見る
    return closed, conns

def check(fixed, max_conns=3, max_accept=1):
    bad = []
    for n in range(0, max_conns + 1):
        for socks in itertools.product((IDLE, READY, EOF), repeat=n):
            for k in range(0, max_accept + 1):
                for accepted in itertools.product((IDLE, READY), repeat=k):
                    closed, _ = tick(list(socks), list(accepted), fixed)
                    for cid, reason in closed:
                        if reason not in ("served", "eof"):
                            bad.append((socks, accepted, cid, reason))
    return bad

if __name__ == "__main__":
    bad = check(fixed=False)
    print(f"current code: {len(bad)} scenario(s) close a healthy connection")
    for socks, accepted, cid, reason in bad[:5]:
        print(f"  conns={list(socks)} accepted={list(accepted)} -> closes {cid} via recv {reason}")
    bad_fixed = check(fixed=True)
    print(f"fixed variant (revents by connection identity): {len(bad_fixed)} scenario(s)")
    # self-check: 現状は反例あり、修正案は無し
    sys.exit(0 if bad and not bad_fixed else 1)

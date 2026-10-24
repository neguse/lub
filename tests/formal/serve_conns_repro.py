#!/usr/bin/env python3
"""手で回す実機再現 (CI では回さない): `lub --serve <csproj> --port 18090` を立てておき、、A を接続 → B を接続 (request 未送信) → A を閉じる → B で request。
現状の serve_tick では、A の EOF を処理した tick で B が A の slot に詰められ、A の revents で recv されて
EAGAIN → close される。B の request は応答無しで切れるはず。対照: A を閉じなければ B は応答を得る。"""
import socket, sys, time
port = int(sys.argv[1]) if len(sys.argv) > 1 else 18090

def attempt(close_a):
    a = socket.create_connection(("127.0.0.1", port))
    b = socket.create_connection(("127.0.0.1", port))
    time.sleep(0.1)          # 両方 accept される (tick は 10ms poll)
    if close_a:
        a.close()
    time.sleep(0.1)          # A の EOF を処理する tick が回る
    try:
        b.sendall(b"GET / HTTP/1.1\r\nHost: x\r\n\r\n")
        b.settimeout(2)
        data = b.recv(64)
    except (ConnectionResetError, BrokenPipeError, socket.timeout) as e:
        data = b""
        err = type(e).__name__
    else:
        err = None
    finally:
        b.close()
        if not close_a:
            a.close()
    return data, err

for close_a in (False, True):
    data, err = attempt(close_a)
    status = data.split(b"\r\n")[0].decode(errors="replace") if data else f"(no response, {err})"
    print(f"close A first = {close_a!s:5} -> B got: {status}")

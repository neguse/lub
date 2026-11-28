"""Reads the tcs vm --watch stream (docs/tcs/vm-design.md §13.3) from the host's point of view: starts it, waits for the first output, rewrites the input
to v2, applies the received vm_batch with tcsvm --reload-batch, and checks that the output matches the expectation.
Next, changes a static initializer and checks that restart_required arrives.

usage: watch_test.py TCS TCSVM WORKDIR
"""
import base64
import os
import shutil
import subprocess
import sys
import time

tcs, tcsvm, work = sys.argv[1:4]
here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(work, "Game.cs")
out = os.path.join(work, "game.tcsb")
shutil.copy(os.path.join(here, "ReloadV1.cs.txt"), src)
v1 = os.path.join(work, "v1.tcsb")

proc = subprocess.Popen([tcs, "vm", "--watch", "--export", "Game.*", src, "-o", out],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def save(text):
    """Replaces the input in one step, as an editor's save does: a watch that reads in the middle of a plain write sees a truncated file"""
    tmp = src + ".tmp"
    with open(tmp, "w") as f:
        f.write(text)
    os.replace(tmp, src)


def read_message():
    header = proc.stdout.readline().decode()
    if not header.startswith("@@tcs_"):
        raise SystemExit(f"FAIL watch: unexpected line {header!r}")
    kind, size = header[6:].split()
    return kind, proc.stdout.read(int(size)).decode()


try:
    deadline = time.time() + 60
    while not os.path.exists(out) and time.time() < deadline:
        time.sleep(0.1)
    # Waits for the first stderr line of watch ("watching") before recording the v1 output
    proc.stderr.readline()
    shutil.copy(out, v1)
    time.sleep(0.2)
    v2 = open(os.path.join(here, "ReloadV2.cs.txt")).read()
    save(v2)
    kind, body = read_message()
    if kind != "vm_batch":
        raise SystemExit(f"FAIL watch: expected vm_batch, got {kind}: {body}")
    batch = os.path.join(work, "v2.batch")
    with open(batch, "wb") as f:
        f.write(base64.b64decode(body.strip()))
    run = subprocess.run([tcsvm, "--lib", "Game", "4", "--reload-batch", "2", batch, v1], capture_output=True, text=True)
    expected = open(os.path.join(here, "reload_expected.txt")).read()
    if run.returncode != 0 or run.stdout != expected:
        raise SystemExit(f"FAIL watch: reload output\n{run.stdout}\n{run.stderr}")
    # An edit that removes a static initializer is a restart
    time.sleep(0.2)
    save(v2.replace(' = "hello v2"', ''))
    kind, body = read_message()
    if kind != "restart_required" or "static initializer removed: Game.banner" not in body:
        raise SystemExit(f"FAIL watch: expected restart_required, got {kind}: {body}")
    print("ok   watch (vm_batch, restart_required)")
finally:
    proc.kill()

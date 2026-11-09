"""tcsvm の読み込みの頑健さ (doc/vm-design.md §16): .tcsb を変異させても、loader と
verifier はエラーを返すか、検査を通った命令列を安全に実行するだけで、crash しないこと。
使い方: python3 -I tcsvm/fuzz-loader.py TCSVM(sanitizer 付き) N SEED file.tcsb ...
無限ループは時間切れとして数える (検査は停止性を保証しない)。"""
import os
import random
import subprocess
import sys
import tempfile

vm, n, seed, inputs = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4:]
rng = random.Random(seed)
seeds = [open(p, 'rb').read() for p in inputs]
stats = {'load_error': 0, 'fault': 0, 'ok': 0, 'timeout': 0}
crashes = []
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, 'm.tcsb')
    for i in range(n):
        data = bytearray(rng.choice(seeds))
        mode = rng.random()
        if mode < 0.1:
            data = data[:rng.randrange(len(data))]
        else:
            for _ in range(rng.choice([1, 1, 2, 4, 16])):
                pos = rng.randrange(len(data))
                data[pos] = rng.randrange(256) if rng.random() < 0.5 else data[pos] ^ (1 << rng.randrange(8))
        with open(path, 'wb') as f:
            f.write(data)
        try:
            r = subprocess.run([vm, path], capture_output=True, timeout=2)
        except subprocess.TimeoutExpired:
            stats['timeout'] += 1
            continue
        err = r.stderr.decode(errors='replace')
        if r.returncode == 0:
            stats['ok'] += 1
        elif r.returncode == 1 and 'fault:' in err:
            stats['fault'] += 1
        elif r.returncode == 1 and 'Sanitizer' not in err and 'runtime error' not in err:
            stats['load_error'] += 1
        else:
            crashes.append((i, r.returncode, err[:400]))
            with open(f'/tmp/vm/crash_{i}.tcsb', 'wb') as f:
                f.write(data)
print(stats, 'crashes:', len(crashes))
for c in crashes[:5]:
    print(c)
sys.exit(1 if crashes else 0)

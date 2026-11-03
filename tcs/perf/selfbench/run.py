#!/usr/bin/env python3
"""tcs 自身 (compiler) の速さの回帰を見るベンチ。

tcs のソース (SelfHost/Compiler、CBackend、VmBackend、Tool。約 4 万行) を入力にして、
C にした tcs (tcs c で自分を C にしたもの) と .NET の tcs で次を測る:
  lua   tcs → Lua (tcs <files> -o out.lua)
  c     tcs → C   (tcs c --lib <files> -o out.c)
指標:
  time_s        wall の時間の median (揺れるので報告だけ)
  instructions  C の tcs の実行した命令の数 (valgrind cachegrind。ほぼ決定的なのでゲートに使う)
  heap_peak     C の tcs の managed heap の段の間の最大 (決定的、ゲートに使う)
  out_bytes     出力の大きさ (決定的。生成するコードの膨らみを見る)

使い方:
  python3 perf/selfbench/run.py                       # 今の tree だけ測る
  python3 perf/selfbench/run.py --base origin/master  # base と交互に測って比べる。決定的な指標が閾値を超えて増えたら exit 1
  python3 perf/selfbench/run.py --no-cachegrind --runs 3
--base のときの入力は base の tree のソース (両方の compiler に同じ入力を与えるため)。
"""

import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
CFLAGS = ["-O2", "-ffp-contract=off", "-fwrapv", "-fexcess-precision=standard"]
GATED = ("instructions", "heap_peak")


def sh(cmd, cwd=None, env=None, quiet=True):
    p = subprocess.run(cmd, cwd=cwd, env=env, stdout=subprocess.PIPE if quiet else None, stderr=subprocess.STDOUT,
                       text=True)
    if p.returncode != 0:
        raise SystemExit("command failed: %s\n%s" % (" ".join(map(str, cmd)), p.stdout or ""))
    return p.stdout


def dotnet():
    return os.environ.get("DOTNET") or shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet")


def sources(tree):
    """build.sh と同じ入力 (TcsHost.cs は --ref)"""
    src = []
    for d in ("Compiler", "CBackend", "VmBackend"):
        src += sorted((tree / "SelfHost" / d).glob("*.cs"))
    src += sorted(f for f in (tree / "SelfHost" / "Tool").glob("*.cs") if f.name != "TcsHost.cs")
    return ["--ref", str(tree / "SelfHost" / "Tool" / "TcsHost.cs")] + [str(f) for f in src]


class Tcs:
    """1 つの tree から作った tcs (.NET と C)"""

    def __init__(self, name, tree, work):
        self.name = name
        self.tree = tree
        self.work = work

    def build(self):
        w = self.work
        w.mkdir(parents=True, exist_ok=True)
        print("[%s] build .NET tcs" % self.name, file=sys.stderr)
        sh([dotnet(), "build", str(self.tree / "Transpiler" / "Transpiler.csproj"), "-c", "Release",
            "-p:NuGetAudit=false", "-v:minimal"])
        self.dll = self.tree / "Transpiler" / "bin" / "Release" / "net10.0" / "Transpiler.dll"
        # tcs と一緒に配るファイル (build.sh と同じ配置)。C の tcs は実行ファイルの dir から読む
        (w / "runtime").mkdir(exist_ok=True)
        for f in ("tinysystem.lua", "module_registry.lua"):
            shutil.copy(self.tree / "runtime" / f, w / "runtime" / f)
        for d in ("core", "math", "fmt", "model", "utf8"):
            (w / "runtime" / d).mkdir(exist_ok=True)
            for f in list((self.tree / "runtime" / d).glob("*.c")) + list((self.tree / "runtime" / d).glob("*.h")):
                shutil.copy(f, w / "runtime" / d / f.name)
        (w / "lib" / "TinySystem").mkdir(parents=True, exist_ok=True)
        for f in ("Linq.cs", "ValueTuple.cs"):
            shutil.copy(self.tree / "TinySystem" / f, w / "lib" / "TinySystem" / f)
        print("[%s] build C tcs" % self.name, file=sys.stderr)
        sh([dotnet(), str(self.dll), "c", "--lib", *sources(self.tree), "-o", str(w / "tcs.c")])
        sh([os.environ.get("CC", "gcc"), *CFLAGS, "-I", str(w), str(self.tree / "SelfHost" / "Native" / "host.c"),
            "-o", str(w / "tcs"), "-lm"])
        self.exe = str(w / "tcs")

    def argv(self, host, task, inputs, out):
        cmd = [self.exe] if host == "native" else [dotnet(), str(self.dll)]
        if task == "c":
            cmd += ["c", "--lib"]
        return cmd + inputs + ["-o", str(out)]


def timed(argv, env=None):
    t0 = time.perf_counter()
    sh(argv, env=env)
    return time.perf_counter() - t0


def heap_peak(argv):
    p = subprocess.run(argv, env=dict(os.environ, TCS_HEAP="1"), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       text=True)
    for line in p.stderr.splitlines():
        if "peak managed heap" in line:
            return int(line.split()[-2])
    return None


def instructions(argv, work):
    out = work / "cachegrind.out"
    p = subprocess.run(["valgrind", "--tool=cachegrind", "--cache-sim=no", "--cachegrind-out-file=" + str(out)] + argv,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if p.returncode != 0:
        raise SystemExit("valgrind failed:\n" + p.stderr[-2000:])
    for line in out.read_text().splitlines():
        if line.startswith("summary:"):
            return int(line.split()[1])
    return None


def median(xs):
    s = sorted(xs)
    m = len(s) // 2
    return s[m] if len(s) % 2 else (s[m - 1] + s[m]) / 2


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", help="比べる git の ref (例: origin/master)")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--threshold", type=float, default=3.0, help="決定的な指標がこの %% を超えて増えたら失敗")
    ap.add_argument("--no-cachegrind", action="store_true")
    ap.add_argument("--tasks", default="lua,c")
    ap.add_argument("--hosts", default="native,dotnet")
    ap.add_argument("-o", "--out", help="結果の JSON")
    args = ap.parse_args()

    work = Path(tempfile.mkdtemp(prefix="tcs-selfbench."))
    trees = [Tcs("head", ROOT, work / "head")]
    if args.base:
        base_tree = work / "base-tree"
        sh(["git", "-C", str(ROOT), "worktree", "add", "--detach", str(base_tree), args.base])
        trees.insert(0, Tcs("base", base_tree, work / "base"))
    try:
        for t in trees:
            t.build()
        inputs = sources(trees[0].tree)
        use_cg = not args.no_cachegrind and shutil.which("valgrind") is not None
        if not args.no_cachegrind and not use_cg:
            print("valgrind が無いので instructions は測らない", file=sys.stderr)

        results = {t.name: {} for t in trees}
        for task in args.tasks.split(","):
            for host in args.hosts.split(","):
                key = "%s/%s" % (host, task)
                print("== " + key, file=sys.stderr)
                times = {t.name: [] for t in trees}
                for t in trees:  # warmup (disk cache と .NET の初回)
                    timed(t.argv(host, task, inputs, t.work / ("warm." + task)))
                for _ in range(args.runs):
                    for t in trees:  # 交互に走らせて、runner の揺れを両方に同じようにかける
                        times[t.name].append(timed(t.argv(host, task, inputs, t.work / ("out." + task))))
                for t in trees:
                    r = {"time_s": median(times[t.name]), "time_min_s": min(times[t.name]),
                         "out_bytes": (t.work / ("out." + task)).stat().st_size}
                    if host == "native":
                        r["heap_peak"] = heap_peak(t.argv(host, task, inputs, t.work / ("heap." + task)))
                        if use_cg:
                            r["instructions"] = instructions(t.argv(host, task, inputs, t.work / ("cg." + task)), t.work)
                    results[t.name][key] = r
                    print("  %-5s %s" % (t.name, " ".join("%s=%s" % kv for kv in r.items())), file=sys.stderr)
    finally:
        if args.base:
            subprocess.run(["git", "-C", str(ROOT), "worktree", "remove", "--force", str(work / "base-tree")])

    failures = []
    lines = []
    if args.base:
        lines += ["## tcs selfbench (base %s と比べる、%d 回の median)" % (args.base, args.runs), "",
                  "| 測るもの | 指標 | base | head | 変化 |", "|---|---|---:|---:|---:|"]
        for key in results["head"]:
            b, h = results["base"][key], results["head"][key]
            for metric in ("time_s", "instructions", "heap_peak", "out_bytes"):
                if metric not in h or b.get(metric) in (None, 0) or h[metric] is None:
                    continue
                pct = (h[metric] - b[metric]) * 100.0 / b[metric]
                mark = ""
                if metric in GATED and pct > args.threshold:
                    failures.append("%s %s: %+.2f%% (閾値 %.1f%%)" % (key, metric, pct, args.threshold))
                    mark = " ✗"
                fmt = "%.3f" if metric == "time_s" else "{:,}"
                fb = fmt % b[metric] if metric == "time_s" else fmt.format(b[metric])
                fh = fmt % h[metric] if metric == "time_s" else fmt.format(h[metric])
                lines.append("| %s | %s | %s | %s | %+.2f%%%s |" % (key, metric, fb, fh, pct, mark))
        lines += ["", "instructions と heap_peak は決定的な指標で、%.1f%% を超えて増えると失敗。time_s は報告だけ。" % args.threshold]
    else:
        lines += ["## tcs selfbench (%d 回の median)" % args.runs, "",
                  "| 測るもの | time_s | instructions | heap_peak | out_bytes |", "|---|---:|---:|---:|---:|"]
        for key, r in results["head"].items():
            lines.append("| %s | %.3f | %s | %s | %s |" % (key, r["time_s"], "{:,}".format(r["instructions"]) if r.get(
                "instructions") else "-", "{:,}".format(r["heap_peak"]) if r.get("heap_peak") else "-",
                "{:,}".format(r["out_bytes"])))
    report = "\n".join(lines) + "\n"
    print(report)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as f:
            f.write(report)
    if args.out:
        Path(args.out).write_text(json.dumps({"base": args.base, "host": platform.node(), "results": results},
                                             indent=1) + "\n")
    shutil.rmtree(work, ignore_errors=True)
    if failures:
        print("FAILED:\n" + "\n".join(" - " + f for f in failures), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()

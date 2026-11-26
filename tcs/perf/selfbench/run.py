#!/usr/bin/env python3
"""Benchmark that watches for speed regressions of tcs itself (the compiler).

Taking the tcs sources (Compiler, CBackend, VmBackend, Tool; about 40k lines) as input,
measures the following with the C tcs (tcs compiled to C with tcs c) and the .NET tcs:
  vm    tcs → tcs VM .tcsb    (tcs vm --export TcsSelf.Cli.* <files> -o out.tcsb)
  c     tcs → C               (tcs c --lib <files> -o out.c)
Metrics:
  time_s        median wall time (fluctuates, so only reported; not measured with --runs 0)
  instructions  number of instructions executed by the C tcs (valgrind cachegrind; nearly deterministic, so used as a gate)
  heap_peak     peak managed heap of the C tcs between stages (deterministic, used as a gate)
  out_bytes     size of the output (deterministic; shows bloat in the generated code)

Usage:
  python3 perf/selfbench/run.py                       # measures only the current tree
  python3 perf/selfbench/run.py --base origin/master  # measures alternately with base and compares. exit 1 if a deterministic metric grows beyond the threshold
  python3 perf/selfbench/run.py --no-cachegrind --runs 3
  python3 perf/selfbench/run.py --base origin/master --tasks vm --hosts native --runs 0  # deterministic metrics only (CI)
--base-results <file> reads/writes the base measurement results: if the file exists, it is used without building or measuring base; otherwise base is measured and the file is written.
The base measurement depends only on the base tree and toolchain, so CI caches the file keyed on them. Times cannot be compared, so this is usable only with --runs 0.
With --base, the input is the source of the base tree (to give both compilers the same input).
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
    """Same input as SelfHost/native.sh (TcsHost.cs is --ref)"""
    src = []
    for d in ("Compiler", "CBackend", "VmBackend"):
        src += sorted((tree / d).glob("*.cs"))
    src += sorted(f for f in (tree / "Tool").glob("*.cs") if f.name != "TcsHost.cs")
    return ["--ref", str(tree / "Tool" / "TcsHost.cs")] + [str(f) for f in src]


class Tcs:
    """tcs built from one tree (.NET and C)"""

    def __init__(self, name, tree, work):
        self.name = name
        self.tree = tree
        self.work = work

    def build(self):
        w = self.work
        w.mkdir(parents=True, exist_ok=True)
        print("[%s] build .NET tcs" % self.name, file=sys.stderr)
        # Fix the output directory in advance so the dll can be found regardless of each tree's TargetFramework
        sh([dotnet(), "build", str(self.tree / "Transpiler" / "Transpiler.csproj"), "-c", "Release",
            "-p:NuGetAudit=false", "-v:minimal", "-o", str(w / "dotnet")])
        self.dll = w / "dotnet" / "Transpiler.dll"
        # Files distributed with tcs (same layout as SelfHost/native.sh). The C tcs reads them from the executable's dir
        (w / "runtime").mkdir(exist_ok=True)
        for d in ("core", "math", "fmt", "model", "utf8"):
            (w / "runtime" / d).mkdir(exist_ok=True)
            for f in list((self.tree / "runtime" / d).glob("*.c")) + list((self.tree / "runtime" / d).glob("*.h")):
                shutil.copy(f, w / "runtime" / d / f.name)
        (w / "lib" / "TinySystem").mkdir(parents=True, exist_ok=True)
        for f in ("Linq.cs", "ValueTuple.cs"):
            shutil.copy(self.tree / "TinySystem" / f, w / "lib" / "TinySystem" / f)
        print("[%s] build C tcs" % self.name, file=sys.stderr)
        sh([dotnet(), str(self.dll), "c", "--lib", *sources(self.tree), "-o", str(w / "tcs.c")])
        sh([os.environ.get("CC", "gcc"), *CFLAGS, "-I", str(w), str(self.tree / "Native" / "host.c"),
            "-o", str(w / "tcs"), "-lm"])
        self.exe = str(w / "tcs")

    def argv(self, host, task, inputs, out):
        cmd = [self.exe] if host == "native" else [dotnet(), str(self.dll)]
        if task == "vm":
            cmd += ["vm", "--export", "TcsSelf.Cli.*"]
        elif task == "c":
            cmd += ["c", "--lib"]
        else:
            raise SystemExit("unknown task: " + task)
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
    ap.add_argument("--runs", type=int, default=5, help="時間を測る回数。0 なら時間を測らない")
    ap.add_argument("--threshold", type=float, default=3.0, help="決定的な指標がこの %% を超えて増えたら失敗")
    ap.add_argument("--no-cachegrind", action="store_true")
    ap.add_argument("--tasks", default="vm,c")
    ap.add_argument("--hosts", default="native,dotnet")
    ap.add_argument("--base-results", help="base の測定の結果の JSON (あれば読み、無ければ書く)")
    ap.add_argument("-o", "--out", help="結果の JSON")
    args = ap.parse_args()
    if args.base_results and (not args.base or args.runs):
        raise SystemExit("--base-results は --base と --runs 0 のときだけ使える")
    base_cached = None
    if args.base_results and Path(args.base_results).exists():
        base_cached = json.loads(Path(args.base_results).read_text())

    work = Path(tempfile.mkdtemp(prefix="tcs-selfbench."))
    trees = [Tcs("head", ROOT, work / "head")]
    if args.base:
        base_tree = work / "base-tree"
        sh(["git", "-C", str(ROOT), "worktree", "add", "--detach", str(base_tree), args.base])
        # When tcs is in a dir under the repo, the base tree also looks at the same dir
        prefix = sh(["git", "-C", str(ROOT), "rev-parse", "--show-prefix"]).strip()
        trees.insert(0, Tcs("base", base_tree / prefix, work / "base"))
    # A cached base is neither built nor measured (the base tree is used as the input source)
    measured = [t for t in trees if not (t.name == "base" and base_cached is not None)]
    try:
        for t in measured:
            t.build()
        inputs = sources(trees[0].tree)
        use_cg = not args.no_cachegrind and shutil.which("valgrind") is not None
        if not args.no_cachegrind and not use_cg:
            print("valgrind が無いので instructions は測らない", file=sys.stderr)

        results = {t.name: {} for t in trees}
        if base_cached is not None:
            results["base"] = base_cached
        for task in args.tasks.split(","):
            for host in args.hosts.split(","):
                key = "%s/%s" % (host, task)
                print("== " + key, file=sys.stderr)
                if base_cached is not None and key not in base_cached:
                    raise SystemExit("--base-results に %s が無い" % key)
                times = {t.name: [] for t in trees}
                if args.runs:
                    for t in trees:  # warmup (disk cache and the first .NET run)
                        timed(t.argv(host, task, inputs, t.work / ("warm." + task)))
                for _ in range(args.runs):
                    for t in trees:  # Run alternately so runner fluctuation affects both equally
                        times[t.name].append(timed(t.argv(host, task, inputs, t.work / ("out." + task))))
                for t in measured:
                    out = t.work / ("out." + task)
                    r = {}
                    if args.runs:
                        r["time_s"] = median(times[t.name])
                        r["time_min_s"] = min(times[t.name])
                    if host == "native":
                        r["heap_peak"] = heap_peak(t.argv(host, task, inputs, out))
                        if use_cg:
                            r["instructions"] = instructions(t.argv(host, task, inputs, out), t.work)
                    elif not args.runs:
                        sh(t.argv(host, task, inputs, out))
                    r["out_bytes"] = out.stat().st_size
                    results[t.name][key] = r
                    print("  %-5s %s" % (t.name, " ".join("%s=%s" % kv for kv in r.items())), file=sys.stderr)
    finally:
        if args.base:
            subprocess.run(["git", "-C", str(ROOT), "worktree", "remove", "--force", str(work / "base-tree")])

    if args.base_results and base_cached is None:
        Path(args.base_results).write_text(json.dumps(results["base"], indent=1) + "\n")

    failures = []
    lines = []
    if args.base:
        lines += ["## tcs selfbench (base %s と比べる%s)" % (args.base, "、時間は %d 回の median" % args.runs if args.runs else ""), "",
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
        lines += ["", "instructions と heap_peak は決定的な指標で、%.1f%% を超えて増えると失敗。%s" % (
            args.threshold, "time_s は報告だけ。" if args.runs else "")]
    else:
        lines += ["## tcs selfbench%s" % (" (時間は %d 回の median)" % args.runs if args.runs else ""), "",
                  "| 測るもの | time_s | instructions | heap_peak | out_bytes |", "|---|---:|---:|---:|---:|"]
        for key, r in results["head"].items():
            lines.append("| %s | %s | %s | %s | %s |" % (key, "%.3f" % r["time_s"] if "time_s" in r else "-", "{:,}".format(r["instructions"]) if r.get(
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

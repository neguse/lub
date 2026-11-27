#!/usr/bin/env python3
"""Benchmark that watches for speed regressions of tcs itself (the compiler).

Taking the tcs sources (src/tcs/sources.sh; about 40k lines) as input, measures these tasks:
  vm    tcs → tcs VM .tcsb    (tcs vm --export Tcs.Cli.* <files> -o out.tcsb)
  c     tcs → C               (tcs c --lib <files> -o out.c)
with tcs running in these execution modes:
  c       the C tcs (tcs compiled to C with tcs c): the compiler, the C that tcs c generates, and the C runtime
  vm      tcs on the tcs VM (the .tcsb of tcs itself, run by tcs_on_vm.c): the interpreter and the bytecode that tcs vm generates
  dotnet  the .NET tcs (time only)
Metrics:
  time_s        median wall time (fluctuates, so only reported; not measured with --runs 0)
  instructions  number of instructions executed in the modes c and vm (valgrind cachegrind; nearly deterministic, so used as a gate)
  heap_peak     peak managed heap between steps (deterministic, used as a gate): for c, old generation plus nursery after each step;
                for vm, the bytes alive after the VM's last collection
  out_bytes     size of the output (deterministic; shows bloat in the generated code)
A base tree from before tcs_on_vm.c has no vm mode: the head is measured alone and not compared.

Usage:
  python3 tests/tcs-perf/selfbench/run.py                       # measures only the current tree
  python3 tests/tcs-perf/selfbench/run.py --base origin/master  # measures alternately with base and compares. exit 1 if a deterministic metric grows beyond the threshold
  python3 tests/tcs-perf/selfbench/run.py --no-cachegrind --runs 3
  python3 tests/tcs-perf/selfbench/run.py --base origin/master --tasks vm --modes c,vm --runs 0  # deterministic metrics only (CI)
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
ROOT = HERE.parent.parent.parent
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


class Layout:
    """Where one repo tree keeps tcs. A base tree may still have it under tcs/ (the .NET entry in Transpiler, the namespace TcsSelf)"""

    def __init__(self, root):
        self.root = root
        old = root / "tcs"
        if (root / "src" / "tcs" / "tcs.csproj").is_file():
            self.project = root / "src" / "tcs" / "tcs.csproj"
            self.dll = "tcs.dll"
            self.body = root / "src" / "tcs"
            self.runtime = root / "src" / "tcs-runtime"
            self.system = root / "src" / "tcs-system"
            self.namespace = "Tcs"
        else:
            self.project = old / "Transpiler" / "Transpiler.csproj"
            self.dll = "Transpiler.dll"
            self.body = old / "SelfHost" if (old / "SelfHost" / "Compiler").is_dir() else root / "src" / "tcs"
            self.runtime = old / "runtime" if (old / "runtime").is_dir() else root / "src" / "tcs-runtime"
            self.system = old / "TinySystem"
            self.namespace = "TcsSelf"

    def sources(self):
        """The arguments that give tcs its own source: what the tree's src/tcs/sources.sh prints"""
        script = self.root / "src" / "tcs" / "sources.sh"
        if script.is_file():
            return [a if a.startswith("--") else str(self.root / a) for a in sh([str(script)]).split()]
        # A tree from before sources.sh (TcsHost.cs is --ref)
        src = []
        for d in ("Compiler", "CBackend", "VmBackend"):
            src += sorted((self.body / d).glob("*.cs"))
        src += sorted(f for f in (self.body / "Tool").glob("*.cs") if f.name != "TcsHost.cs")
        return ["--ref", str(self.body / "Tool" / "TcsHost.cs")] + [str(f) for f in src]


class Tcs:
    """tcs built from one tree (.NET and C)"""

    def __init__(self, name, tree, work):
        self.name = name
        self.layout = Layout(tree)
        self.work = work
        # The command of tcs on the tcs VM (None if not built, or if the tree has no program for it)
        self.vm = None

    def build_vm(self):
        """tcs on the tcs VM: the tree's VM, the .tcsb of the tree's tcs, and the program that runs it. No-op if the tree has no program"""
        root, w = self.layout.root, self.work
        program = root / "tests" / "tcs-perf" / "selfbench" / "tcs_on_vm.c"
        if not program.is_file():
            return
        print("[%s] build tcs on the tcs VM" % self.name, file=sys.stderr)
        sh(["cmake", "-S", str(root / "src" / "tcs-vm"), "-B", str(w / "vm"), "-DCMAKE_BUILD_TYPE=Release"])
        sh(["cmake", "--build", str(w / "vm"), "--target", "tcs-vm", "--parallel"])
        # The module sits next to lib/ and runtime/ (build-native.sh put them in w), where the program reads the shipped files
        sh([self.exe, "vm", "--export", self.layout.namespace + ".Cli.*", *self.layout.sources(), "-o", str(w / "tcs.tcsb")])
        sh([os.environ.get("CC", "gcc"), "-O2", "-ffp-contract=off", "-I", str(root / "src" / "tcs-vm"),
            "-I", str(root / "src" / "tcs" / "Native"), str(program),
            str(w / "vm" / "libtcs-vm.a"), str(w / "vm" / "tcs-runtime" / "libtcs-runtime.a"), "-lm", "-o", str(w / "tcs-on-vm")])
        self.vm = [str(w / "tcs-on-vm"), str(w / "tcs.tcsb")]

    def build(self, vm=False):
        w = self.work
        w.mkdir(parents=True, exist_ok=True)
        root = self.layout.root
        if (root / "src" / "tcs" / "build-native.sh").is_file():
            # The tree builds itself: the .NET tcs, then the C tcs with the files shipped with it
            print("[%s] build .NET tcs" % self.name, file=sys.stderr)
            self.dotnet = sh([str(root / "scripts" / "build-tcs-dotnet.sh")]).split()
            print("[%s] build C tcs" % self.name, file=sys.stderr)
            sh([str(root / "src" / "tcs" / "build-native.sh"), str(w)], env=dict(os.environ, LUB_TCS=" ".join(self.dotnet)))
            self.exe = str(w / "tcs")
            if vm:
                self.build_vm()
            return
        # A tree from before build-native.sh
        print("[%s] build .NET tcs" % self.name, file=sys.stderr)
        # Fix the output directory in advance so the dll can be found regardless of each tree's TargetFramework
        sh([dotnet(), "build", str(self.layout.project), "-c", "Release",
            "-p:NuGetAudit=false", "-v:minimal", "-o", str(w / "dotnet")])
        self.dotnet = [dotnet(), str(w / "dotnet" / self.layout.dll)]
        (w / "runtime").mkdir(exist_ok=True)
        for d in ("core", "math", "fmt", "model", "utf8"):
            (w / "runtime" / d).mkdir(exist_ok=True)
            for f in list((self.layout.runtime / d).glob("*.c")) + list((self.layout.runtime / d).glob("*.h")):
                shutil.copy(f, w / "runtime" / d / f.name)
        (w / "lib" / "TinySystem").mkdir(parents=True, exist_ok=True)
        for f in ("Linq.cs", "ValueTuple.cs"):
            shutil.copy(self.layout.system / f, w / "lib" / "TinySystem" / f)
        print("[%s] build C tcs" % self.name, file=sys.stderr)
        sh([*self.dotnet, "c", "--lib", *self.layout.sources(), "-o", str(w / "tcs.c")])
        sh([os.environ.get("CC", "gcc"), *CFLAGS, "-I", str(w), str(self.layout.body / "Native" / "host.c"),
            "-o", str(w / "tcs"), "-lm"])
        self.exe = str(w / "tcs")

    def has(self, mode):
        return mode != "vm" or self.vm is not None

    def argv(self, mode, task, inputs, out):
        if mode not in ("c", "dotnet", "vm"):
            raise SystemExit("unknown mode: " + mode)
        cmd = [self.exe] if mode == "c" else list(self.vm) if mode == "vm" else list(self.dotnet)
        if task == "vm":
            cmd += ["vm", "--export", inputs.namespace + ".Cli.*"]
        elif task == "c":
            cmd += ["c", "--lib"]
        else:
            raise SystemExit("unknown task: " + task)
        return cmd + inputs.sources() + ["-o", str(out)]


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
    ap.add_argument("--modes", default="c,dotnet", help="tcs を動かす実行モード (c, dotnet, vm)")
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
        trees.insert(0, Tcs("base", base_tree, work / "base"))
    # A cached base is neither built nor measured (the base tree is used as the input source)
    measured = [t for t in trees if not (t.name == "base" and base_cached is not None)]
    try:
        modes = args.modes.split(",")
        for t in measured:
            t.build(vm="vm" in modes)
        inputs = trees[0].layout
        use_cg = not args.no_cachegrind and shutil.which("valgrind") is not None
        if not args.no_cachegrind and not use_cg:
            print("valgrind が無いので instructions は測らない", file=sys.stderr)

        results = {t.name: {} for t in trees}
        if base_cached is not None:
            results["base"] = base_cached
        for task in args.tasks.split(","):
            for mode in modes:
                key = "%s/%s" % (mode, task)
                print("== " + key, file=sys.stderr)
                # The trees that have this mode. A cached base is known by its results
                ran = [t for t in trees if t.has(mode)] if base_cached is None else [t for t in trees if t.name != "base" and t.has(mode)]
                if base_cached is not None and key not in base_cached and mode != "vm":
                    raise SystemExit("--base-results に %s が無い" % key)
                times = {t.name: [] for t in ran}
                if args.runs:
                    for t in ran:  # warmup (disk cache and the first .NET run)
                        timed(t.argv(mode, task, inputs, t.work / ("warm." + task)))
                for _ in range(args.runs):
                    for t in ran:  # Run alternately so runner fluctuation affects both equally
                        times[t.name].append(timed(t.argv(mode, task, inputs, t.work / ("out." + task))))
                for t in ran:
                    if t not in measured:
                        continue
                    out = t.work / ("out." + task)
                    r = {}
                    if args.runs:
                        r["time_s"] = median(times[t.name])
                        r["time_min_s"] = min(times[t.name])
                    if mode != "dotnet":
                        r["heap_peak"] = heap_peak(t.argv(mode, task, inputs, out))
                        if use_cg:
                            r["instructions"] = instructions(t.argv(mode, task, inputs, out), t.work)
                    elif not args.runs:
                        sh(t.argv(mode, task, inputs, out))
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
        alone = []
        for key in results["head"]:
            if key not in results["base"]:
                alone.append(key)
                continue
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
        if alone:
            lines += ["", "base に無い実行モード (比べない):", "", "| 測るもの | instructions | heap_peak | out_bytes |", "|---|---:|---:|---:|"]
            for key in alone:
                r = results["head"][key]
                lines.append("| %s | %s | %s | %s |" % (key, "{:,}".format(r["instructions"]) if r.get("instructions") else "-",
                                                    "{:,}".format(r["heap_peak"]) if r.get("heap_peak") else "-", "{:,}".format(r["out_bytes"])))
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

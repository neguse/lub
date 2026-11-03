#!/usr/bin/env python3
"""tcs の実行経路と他の言語を同じプログラムで比べる (外向けの比較ベンチ)。

使い方:
  python3 perf/compare/run.py --check          # 小さいサイズで全実装の出力の一致だけ見る (CI 用、時間は測らない)
  python3 perf/compare/run.py                  # 本番のサイズで計測し results/ に JSON を書く
  python3 perf/compare/run.py -p fib -i c,tcs-c --runs 3
  python3 perf/compare/report.py results/<file>.json   # 表と HTML を作る

測り方と公平さの決まりは METHODOLOGY.md。
"""

import argparse
import datetime
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
PROGRAMS_DIR = HERE / "programs"

# name: ディレクトリ名、entry: C# の class 名 (各言語のファイル名も同じ)、
# check / full: BENCH_N に渡すサイズ、float: f32 の演算を含むか (f64 しか無い言語は許容差で比べる)
PROGRAMS = [
    {"name": "startup", "entry": "Startup", "check": 0, "full": 0, "float": False,
     "desc": "何もしないプログラム (起動と終了の時間)"},
    {"name": "fib", "entry": "Fib", "check": 20, "full": 38, "float": False,
     "desc": "再帰呼び出し (fib の二重再帰)"},
    {"name": "fannkuch", "entry": "Fannkuch", "check": 7, "full": 10, "float": False,
     "desc": "小さな int 配列の操作と分岐 (fannkuch-redux)"},
    {"name": "binarytrees", "entry": "BinaryTrees", "check": 10, "full": 17, "float": False,
     "desc": "小さな object の確保と GC (binary-trees)"},
    {"name": "nbody", "entry": "NBody", "check": 1000, "full": 2000000, "float": True,
     "desc": "float の演算と object の field (n-body、f32)"},
    {"name": "spectralnorm", "entry": "SpectralNorm", "check": 100, "full": 1500, "float": True,
     "desc": "二重ループの float の積和 (spectral-norm、f32)"},
    {"name": "mandelbrot", "entry": "Mandelbrot", "check": 200, "full": 2000, "float": True,
     "desc": "内側のループの float の演算と分岐 (mandelbrot、f32)"},
    {"name": "sprites", "entry": "Sprites", "check": 100, "full": 30000, "float": True,
     "desc": "ゲーム: class の sprite 1024 個を List で毎フレーム更新"},
    {"name": "particles", "entry": "Particles", "check": 100, "full": 8000, "float": True,
     "desc": "ゲーム: struct の配列の particle 4096 個に重力と反射"},
]

CFLAGS = ["-O2", "-ffp-contract=off", "-fwrapv"]


def run_quiet(cmd, cwd=None, env=None):
    p = subprocess.run(cmd, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if p.returncode != 0:
        raise RuntimeError("command failed: " + " ".join(map(str, cmd)) + "\n" + p.stdout)
    return p.stdout


def first_line(cmd):
    try:
        p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=60)
        # JVM が環境の JAVA_TOOL_OPTIONS を報告する行 (proxy の設定などが入る) は版ではないので飛ばす
        lines = [l for l in p.stdout.strip().splitlines() if l.strip() and not l.startswith("Picked up ")]
        return lines[0] if lines else ""
    except (OSError, subprocess.TimeoutExpired):
        return ""


class Toolchain:
    """外部のコマンドの場所。環境変数で上書きできる"""

    def __init__(self):
        self.cc = os.environ.get("CC", "gcc")
        self.dotnet = os.environ.get("DOTNET") or shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet")
        self.lua32 = os.environ.get("LUA32", str(ROOT / "deps" / "lua" / "lua32"))
        self.tcsvm = os.environ.get("TCSVM", str(ROOT / "build" / "tcsvm" / "tcsvm"))
        self.tcs_dll = ROOT / "Transpiler" / "bin" / "Release" / "net10.0" / "Transpiler.dll"
        self._tcs_ready = False
        self._native_ready = False

    def ensure_native(self):
        """lua32 と tcsvm (root の CMake)"""
        if self._native_ready:
            return
        run_quiet(["cmake", "-B", str(ROOT / "build"), "-DCMAKE_BUILD_TYPE=Release", "-S", str(ROOT)])
        run_quiet(["cmake", "--build", str(ROOT / "build"), "--parallel"])
        self._native_ready = True

    def ensure_tcs(self):
        if self._tcs_ready:
            return
        run_quiet([self.dotnet, "build", str(ROOT / "Transpiler" / "Transpiler.csproj"), "-c", "Release",
                   "-p:NuGetAudit=false", "-v:minimal"])
        self._tcs_ready = True

    def tcs(self, *args):
        self.ensure_tcs()
        return run_quiet([self.dotnet, str(self.tcs_dll), *map(str, args)])

    def has(self, cmd):
        return shutil.which(cmd) is not None or Path(cmd).is_file()


class Impl:
    id = ""
    label = ""
    group = ""  # "tcs" か "ref"
    approx = False  # f32 を持たず f64 で計算する (float のプログラムは許容差で比べる)

    def __init__(self, tc):
        self.tc = tc

    def available(self):
        return True

    def version(self):
        return ""

    def source(self, prog):
        return None

    def build(self, prog, work):
        """実行の argv を返す"""
        raise NotImplementedError


class TcsImpl(Impl):
    group = "tcs"

    def source(self, prog):
        return PROGRAMS_DIR / prog["name"] / (prog["entry"] + ".cs")

    def available(self):
        return self.tc.has(self.tc.dotnet)


class TcsLua(TcsImpl):
    id = "tcs-lua"
    label = "TinyC# → Lua (lua32)"

    def version(self):
        return first_line([self.tc.lua32, "-v"])

    def build(self, prog, work):
        self.tc.ensure_native()
        out = work / (prog["entry"] + ".lua")
        self.tc.tcs(self.source(prog), "-o", out)
        with open(out, "a") as f:
            f.write("\n%s.Main()\n" % prog["entry"])
        return [self.tc.lua32, str(out)]


class TcsVm(TcsImpl):
    id = "tcs-vm"
    label = "TinyC# → tcs VM"

    def version(self):
        return "tcsvm (tcs %s)" % git_commit()

    def build(self, prog, work):
        self.tc.ensure_native()
        out = work / (prog["entry"] + ".tcsb")
        self.tc.tcs("vm", "--entry", prog["entry"], self.source(prog), "-o", out)
        return [self.tc.tcsvm, str(out)]


class TcsC(TcsImpl):
    id = "tcs-c"
    label = "TinyC# → C (gcc -O2)"

    def version(self):
        return first_line([self.tc.cc, "--version"])

    def build(self, prog, work):
        c = work / (prog["entry"] + ".c")
        exe = work / prog["entry"]
        self.tc.tcs("c", "--entry", prog["entry"], self.source(prog), "-o", c)
        run_quiet([self.tc.cc, *CFLAGS, str(c), "-o", str(exe), "-lm"])
        return [str(exe)]


class TcsDotnet(TcsImpl):
    id = "tcs-dotnet"
    label = "TinyC# on .NET (JIT)"

    def version(self):
        return ".NET SDK " + first_line([self.tc.dotnet, "--version"])

    def build(self, prog, work):
        # TinySystem.props で Math と Dictionary を tcs と同じ実装にする (tcs のプログラムを .NET で動かす経路そのもの)
        proj = work / "dotnet"
        proj.mkdir(exist_ok=True)
        entry = prog["entry"]
        (proj / (entry + ".csproj")).write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="{ROOT}/TinySystem/TinySystem.props" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
    <AssemblyName>{entry}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{ROOT}/TinySystem/TinySystem.csproj" />
    <Compile Include="{self.source(prog)}" />
  </ItemGroup>
</Project>
""")
        out = proj / "out"
        run_quiet([self.tc.dotnet, "build", str(proj / (entry + ".csproj")), "-c", "Release", "-o", str(out),
                   "-p:NuGetAudit=false", "-v:minimal"])
        return [self.tc.dotnet, str(out / (entry + ".dll"))]


class RefImpl(Impl):
    group = "ref"
    ext = ""

    def source(self, prog):
        return PROGRAMS_DIR / prog["name"] / (prog["entry"] + self.ext)


class RefC(RefImpl):
    id = "c"
    label = "C (gcc -O2)"
    ext = ".c"

    def available(self):
        return self.tc.has(self.tc.cc)

    def version(self):
        return first_line([self.tc.cc, "--version"])

    def build(self, prog, work):
        exe = work / prog["entry"]
        run_quiet([self.tc.cc, *CFLAGS, str(self.source(prog)), "-o", str(exe), "-lm"])
        return [str(exe)]


class RefRust(RefImpl):
    id = "rust"
    label = "Rust (rustc -O3)"
    ext = ".rs"

    def available(self):
        return self.tc.has("rustc")

    def version(self):
        return first_line(["rustc", "--version"])

    def build(self, prog, work):
        exe = work / prog["entry"]
        run_quiet(["rustc", "-C", "opt-level=3", "-o", str(exe), str(self.source(prog))])
        return [str(exe)]


class RefGo(RefImpl):
    id = "go"
    label = "Go"
    ext = ".go"

    def available(self):
        return self.tc.has("go")

    def version(self):
        return first_line(["go", "version"])

    def build(self, prog, work):
        exe = work / prog["entry"]
        env = dict(os.environ, GO111MODULE="off")
        run_quiet(["go", "build", "-o", str(exe), str(self.source(prog))], env=env)
        return [str(exe)]


class RefJava(RefImpl):
    id = "java"
    label = "Java (HotSpot)"
    ext = ".java"

    def available(self):
        return self.tc.has("javac") and self.tc.has("java")

    def version(self):
        return first_line(["java", "-version"])

    def build(self, prog, work):
        out = work / "classes"
        out.mkdir(exist_ok=True)
        run_quiet(["javac", "-d", str(out), str(self.source(prog))])
        return ["java", "-cp", str(out), prog["entry"]]


class RefJs(RefImpl):
    id = "js"
    label = "JavaScript (Node.js)"
    ext = ".js"

    def available(self):
        return self.tc.has("node")

    def version(self):
        return "Node.js " + first_line(["node", "--version"])

    def build(self, prog, work):
        return ["node", str(self.source(prog))]


class RefPython(RefImpl):
    id = "python"
    label = "Python (CPython)"
    ext = ".py"
    approx = True

    def available(self):
        return self.tc.has("python3")

    def version(self):
        return first_line(["python3", "--version"])

    def build(self, prog, work):
        return ["python3", "-I", str(self.source(prog))]


class RefLua(RefImpl):
    id = "lua"
    label = "Lua 5.5 手書き (lua32)"
    ext = ".lua"

    def version(self):
        return first_line([self.tc.lua32, "-v"])

    def build(self, prog, work):
        self.tc.ensure_native()
        return [self.tc.lua32, str(self.source(prog))]


IMPLS = [RefC, TcsC, RefRust, RefGo, RefJava, TcsDotnet, RefJs, TcsVm, TcsLua, RefLua, RefPython]

NUM = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


def outputs_match(expected, actual, approx):
    if expected == actual:
        return True
    if not approx:
        return False
    # f64 で計算した実装: 数以外の部分は一致、数は相対 1e-3 以内
    if NUM.sub("#", expected) != NUM.sub("#", actual):
        return False
    for a, b in zip(NUM.findall(expected), NUM.findall(actual)):
        x, y = float(a), float(b)
        if abs(x - y) > 1e-3 * max(1.0, abs(x), abs(y)):
            return False
    return True


def run_measured(launcher, argv, n, pin, work):
    """1 回走らせ、wall 秒、stdout、peak RSS (KiB) を返す。計測は launcher (launcher.c) が行う"""
    env = dict(os.environ, BENCH_N=str(n))
    out_path = work / "run.out"
    res_path = work / "run.result"
    with open(out_path, "wb") as fo:
        p = subprocess.run([launcher, str(res_path), str(-1 if pin is None else pin), *argv],
                           env=env, stdout=fo, stderr=subprocess.PIPE)
    if p.returncode != 0:
        raise RuntimeError("launcher failed: " + p.stderr.decode(errors="replace"))
    wall, rss, code = res_path.read_text().split()
    out = out_path.read_text(errors="replace")
    if int(code) != 0:
        raise RuntimeError("exit %s: %s\n%s" % (code, " ".join(argv), p.stderr.decode(errors="replace")[-2000:]))
    return float(wall), out, int(rss)


def median(xs):
    s = sorted(xs)
    m = len(s) // 2
    return s[m] if len(s) % 2 else (s[m - 1] + s[m]) / 2


def git_commit():
    try:
        return run_quiet(["git", "-C", str(ROOT), "rev-parse", "--short", "HEAD"]).strip()
    except RuntimeError:
        return ""


def machine_info():
    cpu = ""
    try:
        for line in open("/proc/cpuinfo"):
            if line.startswith("model name"):
                cpu = line.split(":", 1)[1].strip()
                break
    except OSError:
        cpu = platform.processor()
    return {"cpu": cpu, "cores": os.cpu_count(), "os": platform.platform(), "host": platform.node()}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="小さいサイズで出力の一致だけ確かめる (時間は測らない)")
    ap.add_argument("-p", "--programs", help="カンマ区切りのプログラム名")
    ap.add_argument("-i", "--impls", help="カンマ区切りの実装 id")
    ap.add_argument("--runs", type=int, default=5, help="計測の回数 (1 回の warmup の後)")
    ap.add_argument("--slow", type=float, default=10.0, help="1 回がこの秒数を超える実装は 3 回にする")
    ap.add_argument("--size", action="append", default=[], help="name=N でサイズを上書き")
    ap.add_argument("--pin", type=int, help="子プロセスをこの CPU に固定する")
    ap.add_argument("--work", default=str(HERE / ".work"), help="build の置き場所")
    ap.add_argument("-o", "--out", help="結果の JSON (既定は results/<日付>-<host>.json)")
    args = ap.parse_args()

    tc = Toolchain()
    progs = PROGRAMS
    if args.programs:
        want = args.programs.split(",")
        progs = [p for p in PROGRAMS if p["name"] in want]
    sizes = dict(s.split("=", 1) for s in args.size)
    impls = [cls(tc) for cls in IMPLS]
    if args.impls:
        want = args.impls.split(",")
        impls = [im for im in impls if im.id in want]

    work = Path(args.work)
    work.mkdir(parents=True, exist_ok=True)
    launcher = work / "launcher"
    run_quiet([tc.cc, "-O2", str(HERE / "launcher.c"), "-o", str(launcher)])
    failures = []
    rows = []
    versions = {}
    for im in impls:
        if not im.available():
            print("skip %-10s (見つからない)" % im.id, file=sys.stderr)
            continue
        versions[im.id] = im.version()

    for prog in progs:
        n = int(sizes.get(prog["name"], prog["check"] if args.check else prog["full"]))
        expected = None
        print("== %s (N=%d)" % (prog["name"], n), file=sys.stderr)
        for im in impls:
            if im.id not in versions:
                continue
            src = im.source(prog)
            if src is not None and not src.exists():
                print("  %-10s (ソースが無い)" % im.id, file=sys.stderr)
                continue
            d = work / prog["name"] / im.id
            d.mkdir(parents=True, exist_ok=True)
            try:
                tb0 = time.perf_counter()
                argv = im.build(prog, d)
                build_s = time.perf_counter() - tb0
                t, out, rss = run_measured(launcher, argv, n, args.pin, d)
            except RuntimeError as e:
                failures.append("%s/%s: %s" % (prog["name"], im.id, e))
                print("  %-10s ERROR %s" % (im.id, str(e).splitlines()[0]), file=sys.stderr)
                continue
            approx = im.approx and prog["float"]
            if expected is None and not im.approx:
                expected = (im.id, out)
            if expected is not None and not outputs_match(expected[1], out, approx):
                failures.append("%s/%s: 出力が %s と違う\n--- %s\n%s--- %s\n%s" % (
                    prog["name"], im.id, expected[0], expected[0], expected[1][-500:], im.id, out[-500:]))
                print("  %-10s MISMATCH" % im.id, file=sys.stderr)
                continue
            if args.check:
                print("  %-10s ok (%.2fs)" % (im.id, t), file=sys.stderr)
                continue
            runs = args.runs if t <= args.slow else min(args.runs, 3)
            times, rsss = [], []
            for _ in range(runs):
                t, out2, rss = run_measured(launcher, argv, n, args.pin, d)
                if out2 != out:
                    failures.append("%s/%s: 実行ごとに出力が変わる" % (prog["name"], im.id))
                times.append(t)
                rsss.append(rss)
            row = {"program": prog["name"], "n": n, "impl": im.id, "runs": runs,
                   "median_s": median(times), "min_s": min(times), "max_s": max(times),
                   "peak_rss_kib": max(rsss), "build_s": build_s, "approx": approx}
            rows.append(row)
            print("  %-10s %8.3fs (min %.3f, %d runs, %d MiB)" % (
                im.id, row["median_s"], row["min_s"], runs, row["peak_rss_kib"] // 1024), file=sys.stderr)

    if not args.check:
        result = {
            "date": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
            "tcs_commit": git_commit(),
            "machine": machine_info(),
            "pin": args.pin,
            "programs": [{k: p[k] for k in ("name", "desc", "float")} | {"n": int(sizes.get(p["name"], p["full"]))}
                         for p in progs],
            "impls": [{"id": im.id, "label": im.label, "group": im.group, "approx": im.approx,
                       "version": versions[im.id]} for im in impls if im.id in versions],
            "rows": rows,
        }
        out = Path(args.out) if args.out else HERE / "results" / (
            datetime.date.today().isoformat() + "-" + platform.node() + ".json")
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(result, ensure_ascii=False, indent=1) + "\n")
        print("wrote " + str(out), file=sys.stderr)

    if failures:
        print("\nFAILED:", file=sys.stderr)
        for f in failures:
            print(" - " + f, file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()

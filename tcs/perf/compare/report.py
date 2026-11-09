#!/usr/bin/env python3
"""run.py の結果 (JSON) から markdown の表と HTML のページを作る。

使い方: python3 perf/compare/report.py results/<file>.json [--html out.html] [--md out.md]
既定は JSON と同じ場所に同じ名前の .md と .html を書く。
"""

import argparse
import html
import json
import math
from pathlib import Path

BASE = "c"  # 倍率の基準
PER = "per_iter_s"  # 1 回あたり (BENCH_REPS の差から出す、温まった後の速さ)
E2E = "median_s"  # 起動から終了まで (BENCH_REPS=1 の 1 プロセス)


def val(r, key):
    """古い結果 (1 回あたりを測っていない) は起動から終了までの値で代える"""
    v = r.get(key)
    return v if v is not None else r["median_s"]


def load(path):
    data = json.loads(Path(path).read_text())
    by = {}
    for r in data["rows"]:
        by[(r["program"], r["impl"])] = r
    return data, by


def ratios(data, by, key=PER):
    """program → impl → C 比 (startup は除く)"""
    out = {}
    for p in data["programs"]:
        if p["name"] == "startup":
            continue
        base = by.get((p["name"], BASE))
        if not base:
            continue
        out[p["name"]] = {im["id"]: val(by[(p["name"], im["id"])], key) / val(base, key)
                          for im in data["impls"] if (p["name"], im["id"]) in by}
    return out


def geomean(xs):
    return math.exp(sum(math.log(x) for x in xs) / len(xs)) if xs else None


def summary(data, by, key=PER):
    """impl → 幾何平均の C 比。全プログラムが揃った実装だけ"""
    rs = ratios(data, by, key)
    out = {}
    for im in data["impls"]:
        xs = [rs[p][im["id"]] for p in rs if im["id"] in rs[p]]
        if xs and len(xs) == len(rs):
            out[im["id"]] = geomean(xs)
    return out


def fmt_s(s):
    return "%.3f" % s if s < 10 else "%.1f" % s


def fmt_x(x):
    return "%.2f" % x if x < 10 else "%.1f" % x


def markdown(data, by):
    labels = {im["id"]: im["label"] for im in data["impls"]}
    m = data["machine"]
    lines = ["# tcs 比較ベンチの結果", "",
             "- 日時: %s、tcs の commit: %s" % (data["date"], data["tcs_commit"]),
             "- マシン: %s (%s コア)、%s" % (m["cpu"], m["cores"], m["os"]),
             "- 1 回あたり: 本体を k 回くり返したプロセスと 1 回のプロセスの時間の差を k-1 で割ったもの。起動と JIT の立ち上がりを含まない",
             "- 起動から終了まで: 本体を 1 回動かすプロセスの wall の時間の median",
             "- 倍率は C (gcc -O2) を 1 としたもの。小さいほど速い。差が 10% 未満は揺れの範囲として同着とみなす",
             "- 測り方と公平さの決まりは `perf/compare/METHODOLOGY.md`", ""]
    sp, se = summary(data, by, PER), summary(data, by, E2E)
    lines += ["## まとめ (startup を除くプログラムの C 比の幾何平均)", "",
              "| 実装 | 1 回あたり | 起動から終了まで | 版 |", "|---|---:|---:|---|"]
    for im in sorted(data["impls"], key=lambda im: sp.get(im["id"], 1e9)):
        if im["id"] in sp:
            lines.append("| %s | %s | %s | %s |" % (im["label"], fmt_x(sp[im["id"]]), fmt_x(se[im["id"]]),
                                                 im["version"]))
    lines.append("")

    progs = [p for p in data["programs"] if p["name"] != "startup"]
    rs = ratios(data, by, PER)
    lines += ["## プログラムごとの C 比 (1 回あたり)", "",
              "| 実装 | " + " | ".join(p["name"] for p in progs) + " |", "|---|" + "---:|" * len(progs)]
    for im in data["impls"]:
        cells = [fmt_x(rs[p["name"]][im["id"]]) if im["id"] in rs.get(p["name"], {}) else "-" for p in progs]
        lines.append("| %s | %s |" % (labels[im["id"]], " | ".join(cells)))
    lines.append("")

    for p in data["programs"]:
        base = by.get((p["name"], BASE))
        rows = sorted((by[(p["name"], im["id"])] for im in data["impls"] if (p["name"], im["id"]) in by),
                      key=lambda r: val(r, PER))
        lines += ["## %s (N=%d)" % (p["name"], p["n"]), "", p["desc"], ""]
        if p.get("reps", True):
            lines += ["| 実装 | 1 回あたり (秒) | 範囲 | C 比 | k | 起動から終了 (秒) | C 比 | peak RSS (MiB) |",
                      "|---|---:|---:|---:|---:|---:|---:|---:|"]
            for r in rows:
                rng = "%s〜%s" % (fmt_s(r["per_iter_min_s"]), fmt_s(r["per_iter_max_s"])) if "per_iter_min_s" in r else "-"
                lines.append("| %s%s | %s | %s | %s | %s | %s | %s | %.1f |" % (
                    labels[r["impl"]], " ※" if r["approx"] else "", fmt_s(val(r, PER)), rng,
                    fmt_x(val(r, PER) / val(base, PER)) if base else "-", r.get("k", "-"),
                    fmt_s(r["median_s"]), fmt_x(r["median_s"] / base["median_s"]) if base else "-",
                    r["peak_rss_kib"] / 1024))
        else:
            lines += ["| 実装 | 秒 (median) | min | peak RSS (MiB) |", "|---|---:|---:|---:|"]
            for r in rows:
                lines.append("| %s | %s | %s | %.1f |" % (labels[r["impl"]], fmt_s(r["median_s"]), fmt_s(r["min_s"]),
                                                         r["peak_rss_kib"] / 1024))
        lines.append("")
    if any(r["approx"] for r in data["rows"]):
        lines += ["※ float32 を持たない言語は float64 で計算し、出力を相対 1e-3 の許容差で比べた", ""]
    return "\n".join(lines)


CSS = """
/* layout: 1 列の読み物。まとめのグラフ → プログラムごとの小さなグラフの格子 → 数値の表 */
:root { --bg:#fbfbfd; --fg:#1b1d24; --muted:#5f6472; --grid:#e3e5ec; --card:#f1f2f6;
  --tcs:#2a62c9; --ref:#a2a6b3; --base:#c9731f;
  --font-body:"IBM Plex Sans JP", system-ui, -apple-system, "Hiragino Sans", "Noto Sans JP", sans-serif;
  --font-data:"IBM Plex Mono", ui-monospace, "SFMono-Regular", Menlo, monospace; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#121419; --fg:#e8eaf0; --muted:#9aa0ae; --grid:#2a2e38; --card:#1b1e25; --tcs:#6f9cf5; --ref:#5f6472; --base:#e49a4f; color-scheme: dark; } }
:root[data-theme="dark"] { --bg:#121419; --fg:#e8eaf0; --muted:#9aa0ae; --grid:#2a2e38; --card:#1b1e25; --tcs:#6f9cf5; --ref:#5f6472; --base:#e49a4f; color-scheme: dark; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.65 var(--font-body); }
main { max-width: 980px; margin: 0 auto; padding-block: 32px 64px;
  padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px)); }
h1 { font-size: 28px; margin: 0 0 4px; text-wrap: balance; }
h2 { font-size: 19px; margin: 40px 0 10px; text-wrap: balance; }
p { max-width: 68ch; }
.meta { color: var(--muted); font-size: 13px; font-family: var(--font-data); }
.legend { display:flex; gap:16px; flex-wrap:wrap; font-size:13px; color:var(--muted); margin: 12px 0; }
.legend span::before { content:""; display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:6px; vertical-align:-1px; background:var(--c); }
.grid { display:grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 440px), 1fr)); gap: 16px; }
.card { background: var(--card); border-radius: 8px; padding: 14px 14px 8px; min-width: 0; }
.card h3 { margin: 0; font-size: 15px; }
.card .desc { color: var(--muted); font-size: 12.5px; margin: 2px 0 8px; }
svg { width: 100%; height: auto; display: block; }
svg text { fill: var(--fg); font-size: 12px; font-family: var(--font-body); }
svg text.num { font-family: var(--font-data); font-variant-numeric: tabular-nums; }
svg .muted { fill: var(--muted); }
svg .gridline { stroke: var(--grid); }
.bar-tcs { fill: var(--tcs); } .bar-ref { fill: var(--ref); } .bar-base { fill: var(--base); }
.tablewrap { overflow-x: auto; }
table { border-collapse: collapse; font-size: 13px; width: 100%; }
th, td { padding: 4px 8px; border-bottom: 1px solid var(--grid); text-align: right; white-space: nowrap; }
td { font-family: var(--font-data); font-variant-numeric: tabular-nums; }
th.l, td.l { text-align: left; font-family: var(--font-body); }
th { color: var(--muted); font-weight: 500; }
ul.note { color: var(--fg); font-size: 14px; max-width: 72ch; padding-left: 20px; }
ul.note li + li { margin-top: 6px; }
"""


def bar_chart(items, unit, width=460):
    """items: [(label, value, kind)]。log 軸の横棒。value は C 比"""
    row_h, top, left, right = 22, 8, 190, 52
    h = top + row_h * len(items) + 22
    lo = min(v for _, v, _ in items)
    hi = max(v for _, v, _ in items)
    lmin = math.floor(math.log10(min(lo, 1.0)) * 2) / 2
    lmax = math.ceil(math.log10(max(hi, 1.0)) * 2) / 2
    if lmax - lmin < 1:
        lmax = lmin + 1
    plot_w = width - left - right

    def x(v):
        return left + (math.log10(v) - lmin) / (lmax - lmin) * plot_w

    parts = ['<svg viewBox="0 0 %d %d" role="img">' % (width, h)]
    k = math.ceil(lmin)
    while k <= lmax:
        gx = x(10 ** k)
        parts.append('<line class="gridline" x1="%.1f" x2="%.1f" y1="%d" y2="%d"/>' % (gx, gx, top, h - 18))
        parts.append('<text class="muted num" x="%.1f" y="%d" text-anchor="middle">%s%s</text>' % (
            gx, h - 4, ("%g" % (10 ** k)), unit))
        k += 1
    for i, (label, v, kind) in enumerate(items):
        y = top + i * row_h
        x0 = x(10 ** lmin)
        parts.append('<text x="%d" y="%d" text-anchor="end">%s</text>' % (left - 8, y + 15, html.escape(label)))
        parts.append('<rect class="bar-%s" x="%.1f" y="%d" width="%.1f" height="%d" rx="2"/>' % (
            kind, x0, y + 4, max(1.0, x(v) - x0), row_h - 8))
        parts.append('<text class="num" x="%.1f" y="%d">%s×</text>' % (x(v) + 4, y + 15, fmt_x(v)))
    parts.append("</svg>")
    return "".join(parts)


def kind_of(im):
    if im["id"] == BASE:
        return "base"
    return "tcs" if im["group"] == "tcs" else "ref"


NOTES = [
    "tcs c の binarytrees は peak RSS が 1.8 GiB になる。tcs c の GC はフレームの境界 (host が entry から戻るとき) でだけ回収するので、Main 1 本で終わるこのプログラムでは最後まで回収しない。毎フレーム境界のあるゲームでは起きない。回収しないままくり返すので、1 回あたりの値 (k 回のプロセスは k 倍の memory を持つ) は定常の速さではなく、heap を広げ続ける時間になる",
    "C の sqrtf は errno の検査が残る (-fno-math-errno を付けていない) ので、nbody と spectralnorm では Rust の方が速い",
    "Java と JS には値型が無いので、particles は struct の配列ではなく object の参照の配列になる",
    "JS は float32 に揃えるため、float の演算をすべて Math.fround に通している。その分 float の多いプログラムで遅い",
    "C との差が 10% 未満の倍率は揺れの範囲で、同着とみなす (表の範囲の列が組ごとの値の幅)",
    "起動から終了までの倍率では、.NET、Java、Node.js に 30〜50 ms の起動と JIT の立ち上がりが入る。1 回あたりの倍率はそれを除いたもの",
]


def page(data, by, fragment=False):
    labels = {im["id"]: im for im in data["impls"]}
    m = data["machine"]
    sm = summary(data, by, PER)
    se = summary(data, by, E2E)
    rs = ratios(data, by, PER)
    head = ('<title>tcs 実行経路ベンチ</title><meta name="claude:viewport-fit-sides" content="cover">'
            '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500'
            '&family=IBM+Plex+Sans+JP:wght@400;500;700&display=swap"><style>%s</style>' % CSS)
    if fragment:
        out = [head, "<main>"]
    else:
        out = ['<!doctype html><html lang="ja"><head><meta charset="utf-8">',
               '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">',
               head, "</head><body><main>"]
    out += ["<h1>tcs 実行経路ベンチ</h1>",
           '<p class="meta">%s ・ tcs %s ・ %s (%s コア) ・ %s</p>' % (
               html.escape(data["date"]), html.escape(data["tcs_commit"]), html.escape(m["cpu"]), m["cores"],
               html.escape(m["os"])),
           "<p>同じ TinyC# のプログラムを tcs の各経路 (tcs VM、C、.NET) で動かし、同じアルゴリズムを"
           "各言語で素直に書いたものと比べた。値は C (gcc -O2) を 1 とした実行時間の倍率で、log 軸。小さいほど速い。"
           "どの実装も出力が一致することを確かめてから測っている。"
           "1 回あたりは起動と JIT の立ち上がりを除いた計算の速さ、起動から終了まではプロセス 1 つの体感の速さ。</p>",
           '<div class="legend"><span style="--c:var(--tcs)">tcs の経路</span>'
           '<span style="--c:var(--base)">C (基準)</span><span style="--c:var(--ref)">他の言語</span></div>']

    items = [(labels[i]["label"], v, kind_of(labels[i])) for i, v in sorted(sm.items(), key=lambda kv: kv[1])]
    if items:
        out.append("<h2>まとめ: 1 回あたりの C 比の幾何平均 (%d プログラム)</h2>" % len(rs))
        out.append('<div class="card">%s</div>' % bar_chart(items, "×", width=640))
    items = [(labels[i]["label"], v, kind_of(labels[i])) for i, v in sorted(se.items(), key=lambda kv: kv[1])]
    if items:
        out.append("<h2>まとめ: 起動から終了までの C 比の幾何平均</h2>")
        out.append('<div class="card">%s</div>' % bar_chart(items, "×", width=640))

    out.append("<h2>プログラムごと (1 回あたり)</h2><div class=\"grid\">")
    for p in data["programs"]:
        if p["name"] not in rs:
            continue
        r = rs[p["name"]]
        items = [(labels[i]["label"], v, kind_of(labels[i])) for i, v in sorted(r.items(), key=lambda kv: kv[1])]
        out.append('<div class="card"><h3>%s <span class="meta">N=%d</span></h3><p class="desc">%s</p>%s</div>' % (
            html.escape(p["name"]), p["n"], html.escape(p["desc"]), bar_chart(items, "×")))
    out.append("</div>")

    st = [(labels[r["impl"]]["label"], r["median_s"] * 1000, kind_of(labels[r["impl"]]))
          for r in data["rows"] if r["program"] == "startup"]
    if st:
        st.sort(key=lambda t: t[1])
        out.append("<h2>起動の時間 (何もしないプログラム、ms)</h2>")
        out.append('<div class="card">%s</div>' % bar_chart([(a, max(b, 0.1), c) for a, b, c in st], "ms", width=640))

    out.append("<h2>数値</h2><div class=\"tablewrap\"><table><tr><th class=\"l\">program</th><th class=\"l\">実装</th>"
               "<th>1 回あたり (秒)</th><th>範囲</th><th>C 比</th><th>k</th><th>起動から終了 (秒)</th><th>C 比</th>"
               "<th>peak RSS (MiB)</th></tr>")
    for p in data["programs"]:
        base = by.get((p["name"], BASE))
        rows = sorted((r for r in data["rows"] if r["program"] == p["name"]), key=lambda r: val(r, PER))
        for r in rows:
            has = "per_iter_s" in r
            rng = "%s〜%s" % (fmt_s(r["per_iter_min_s"]), fmt_s(r["per_iter_max_s"])) if has else "-"
            out.append("<tr><td class=\"l\">%s</td><td class=\"l\">%s%s</td><td>%s</td><td>%s</td><td>%s</td>"
                       "<td>%s</td><td>%s</td><td>%s</td><td>%.1f</td></tr>" % (
                           html.escape(p["name"]), html.escape(labels[r["impl"]]["label"]), " ※" if r["approx"] else "",
                           fmt_s(r["per_iter_s"]) if has else "-", rng,
                           fmt_x(r["per_iter_s"] / base["per_iter_s"]) if has and base and "per_iter_s" in base else "-",
                           r.get("k", "-"), fmt_s(r["median_s"]),
                           fmt_x(r["median_s"] / base["median_s"]) if base else "-", r["peak_rss_kib"] / 1024))
    out.append("</table></div>")
    out.append('<h2>測り方</h2><ul class="note">'
               "<li>各プログラムは本体を環境変数 BENCH_REPS の回数くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにしている</li>"
               "<li>1 回あたり = (T(k 回) − T(1 回)) ÷ (k − 1)。k は差が 1 秒ほどになるよう選び、T(1 回) と T(k 回) を交互に 5 組 (遅い実装は 3 組) 測った median から出す</li>"
               "<li>起動から終了まで = 本体を 1 回動かすプロセスの wall の時間の median</li>"
               "<li>数値は int32 と float32 に揃えた (tcs の数値は i32 と f32 だけ)。float32 を持たない言語 (※) は float64 で計算し、出力を相対 1e-3 で比べた</li>"
               "<li>1 スレッド、SIMD と unsafe と自前の pool は使わず、各言語の標準の書き方で書いた</li>"
               "<li>C と tcs の C の出力は gcc -O2 -ffp-contract=off -fwrapv</li>"
               "<li>詳細は tcs の perf/compare/METHODOLOGY.md</li></ul>")
    out.append('<h2>読むときの注意</h2><ul class="note">%s</ul>' % "".join("<li>%s</li>" % html.escape(n) for n in NOTES))
    out.append("<h2>版</h2><div class=\"tablewrap\"><table><tr><th class=\"l\">実装</th><th class=\"l\">版</th></tr>")
    for im in data["impls"]:
        out.append("<tr><td class=\"l\">%s</td><td class=\"l\">%s</td></tr>" % (html.escape(im["label"]), html.escape(im["version"])))
    out.append("</table></div></main>" if fragment else "</table></div></main></body></html>")
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("json")
    ap.add_argument("--md")
    ap.add_argument("--html")
    ap.add_argument("--fragment", help="Artifact で公開する用に doctype と head を付けない HTML も書く")
    args = ap.parse_args()
    data, by = load(args.json)
    src = Path(args.json)
    md = Path(args.md) if args.md else src.with_suffix(".md")
    ht = Path(args.html) if args.html else src.with_suffix(".html")
    md.write_text(markdown(data, by) + "\n")
    ht.write_text(page(data, by) + "\n")
    if args.fragment:
        Path(args.fragment).write_text(page(data, by, fragment=True) + "\n")
    print("wrote %s\nwrote %s" % (md, ht))


if __name__ == "__main__":
    main()

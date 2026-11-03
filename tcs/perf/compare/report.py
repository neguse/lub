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


def load(path):
    data = json.loads(Path(path).read_text())
    by = {}
    for r in data["rows"]:
        by[(r["program"], r["impl"])] = r
    return data, by


def ratios(data, by):
    """program → impl → C 比 (startup は除く)"""
    out = {}
    for p in data["programs"]:
        if p["name"] == "startup":
            continue
        base = by.get((p["name"], BASE))
        if not base:
            continue
        out[p["name"]] = {im["id"]: by[(p["name"], im["id"])]["median_s"] / base["median_s"]
                          for im in data["impls"] if (p["name"], im["id"]) in by}
    return out


def geomean(xs):
    return math.exp(sum(math.log(x) for x in xs) / len(xs)) if xs else None


def summary(data, by):
    """impl → (幾何平均の C 比, 対象のプログラムの数)。全プログラムが揃った実装だけ"""
    rs = ratios(data, by)
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
             "- 値は wall の時間の median (秒)。倍率は C (gcc -O2) を 1 としたもの。小さいほど速い",
             "- 測り方と公平さの決まりは `perf/compare/METHODOLOGY.md`", ""]
    sm = summary(data, by)
    lines += ["## まとめ (startup を除くプログラムの C 比の幾何平均)", "",
              "| 実装 | C 比 | 版 |", "|---|---:|---|"]
    for im in sorted(data["impls"], key=lambda im: sm.get(im["id"], 1e9)):
        if im["id"] in sm:
            lines.append("| %s | %s | %s |" % (im["label"], fmt_x(sm[im["id"]]), im["version"]))
    lines.append("")

    progs = [p for p in data["programs"]]
    impls = [im for im in data["impls"]]
    lines += ["## プログラムごとの C 比", "",
              "| 実装 | " + " | ".join(p["name"] for p in progs if p["name"] != "startup") + " |",
              "|---|" + "---:|" * len([p for p in progs if p["name"] != "startup"])]
    rs = ratios(data, by)
    for im in impls:
        cells = []
        for p in progs:
            if p["name"] == "startup":
                continue
            x = rs.get(p["name"], {}).get(im["id"])
            cells.append(fmt_x(x) if x is not None else "-")
        lines.append("| %s | %s |" % (labels[im["id"]], " | ".join(cells)))
    lines.append("")

    for p in progs:
        lines += ["## %s (N=%d)" % (p["name"], p["n"]), "", p["desc"], "",
                  "| 実装 | 秒 (median) | min | C 比 | peak RSS (MiB) | 回数 |", "|---|---:|---:|---:|---:|---:|"]
        base = by.get((p["name"], BASE))
        rows = sorted((by[(p["name"], im["id"])] for im in impls if (p["name"], im["id"]) in by),
                      key=lambda r: r["median_s"])
        for r in rows:
            x = r["median_s"] / base["median_s"] if base else None
            note = " ※" if r["approx"] else ""
            lines.append("| %s%s | %s | %s | %s | %.1f | %d |" % (
                labels[r["impl"]], note, fmt_s(r["median_s"]), fmt_s(r["min_s"]),
                fmt_x(x) if x is not None else "-", r["peak_rss_kib"] / 1024, r["runs"]))
        lines.append("")
    if any(r["approx"] for r in data["rows"]):
        lines += ["※ float32 を持たない言語は float64 で計算し、出力を相対 1e-3 の許容差で比べた", ""]
    return "\n".join(lines)


CSS = """
:root { --bg:#ffffff; --fg:#1d1d1f; --muted:#6b6b70; --grid:#e4e4e8; --tcs:#2f6fd6; --ref:#9a9aa3; --base:#d0802a; --card:#f6f6f8; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#141416; --fg:#ececf0; --muted:#9c9ca4; --grid:#2c2c31; --tcs:#6aa0ff; --ref:#6e6e78; --base:#e8a050; --card:#1d1d21; } }
:root[data-theme="dark"] { --bg:#141416; --fg:#ececf0; --muted:#9c9ca4; --grid:#2c2c31; --tcs:#6aa0ff; --ref:#6e6e78; --base:#e8a050; --card:#1d1d21; }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.6 system-ui, -apple-system, "Segoe UI", "Hiragino Sans", sans-serif; }
main { max-width: 960px; margin: 0 auto; padding: 32px 16px 64px; }
h1 { font-size: 26px; margin: 0 0 4px; }
h2 { font-size: 19px; margin: 40px 0 8px; }
p, li { color: var(--fg); }
.meta { color: var(--muted); font-size: 13px; }
.legend { display:flex; gap:16px; flex-wrap:wrap; font-size:13px; color:var(--muted); margin: 12px 0; }
.legend span::before { content:""; display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:6px; vertical-align:-1px; background:var(--c); }
.grid { display:grid; grid-template-columns: repeat(auto-fill, minmax(420px, 1fr)); gap: 16px; }
@media (max-width: 480px) { .grid { grid-template-columns: 1fr; } }
.card { background: var(--card); border-radius: 10px; padding: 14px 14px 8px; }
.card h3 { margin: 0; font-size: 15px; }
.card .desc { color: var(--muted); font-size: 12.5px; margin: 2px 0 8px; }
svg { width: 100%; height: auto; display: block; }
svg text { fill: var(--fg); font-size: 12px; }
svg .muted { fill: var(--muted); }
svg .gridline { stroke: var(--grid); }
.bar-tcs { fill: var(--tcs); } .bar-ref { fill: var(--ref); } .bar-base { fill: var(--base); }
table { border-collapse: collapse; font-size: 13px; width: 100%; }
.tablewrap { overflow-x: auto; }
th, td { padding: 4px 8px; border-bottom: 1px solid var(--grid); text-align: right; white-space: nowrap; }
th:first-child, td:first-child { text-align: left; }
.note { color: var(--muted); font-size: 13px; }
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
        parts.append('<text class="muted" x="%.1f" y="%d" text-anchor="middle">%s%s</text>' % (
            gx, h - 4, ("%g" % (10 ** k)), unit))
        k += 1
    for i, (label, v, kind) in enumerate(items):
        y = top + i * row_h
        x0 = x(10 ** lmin)
        parts.append('<text x="%d" y="%d" text-anchor="end">%s</text>' % (left - 8, y + 15, html.escape(label)))
        parts.append('<rect class="bar-%s" x="%.1f" y="%d" width="%.1f" height="%d" rx="2"/>' % (
            kind, x0, y + 4, max(1.0, x(v) - x0), row_h - 8))
        parts.append('<text x="%.1f" y="%d">%s×</text>' % (x(v) + 4, y + 15, fmt_x(v)))
    parts.append("</svg>")
    return "".join(parts)


def kind_of(im):
    if im["id"] == BASE:
        return "base"
    return "tcs" if im["group"] == "tcs" else "ref"


def page(data, by):
    labels = {im["id"]: im for im in data["impls"]}
    m = data["machine"]
    sm = summary(data, by)
    rs = ratios(data, by)
    out = ['<!doctype html><html lang="ja"><head><meta charset="utf-8">',
           '<meta name="viewport" content="width=device-width, initial-scale=1">',
           "<title>tcs 実行経路ベンチ</title><style>%s</style></head><body><main>" % CSS,
           "<h1>tcs 実行経路ベンチ</h1>",
           '<p class="meta">%s ・ tcs %s ・ %s (%s コア) ・ %s</p>' % (
               html.escape(data["date"]), html.escape(data["tcs_commit"]), html.escape(m["cpu"]), m["cores"],
               html.escape(m["os"])),
           "<p>同じ TinyC# のプログラムを tcs の各経路 (Lua、tcs VM、C、.NET) で動かし、同じアルゴリズムを"
           "各言語で素直に書いたものと比べた。値は C (gcc -O2) を 1 とした実行時間の倍率で、log 軸。小さいほど速い。"
           "どの実装も出力が一致することを確かめてから測っている。</p>",
           '<div class="legend"><span style="--c:var(--tcs)">tcs の経路</span>'
           '<span style="--c:var(--base)">C (基準)</span><span style="--c:var(--ref)">他の言語</span></div>']

    items = [(labels[i]["label"], v, kind_of(labels[i])) for i, v in sorted(sm.items(), key=lambda kv: kv[1])]
    if items:
        out.append("<h2>まとめ: C 比の幾何平均 (%d プログラム)</h2>" % len(rs))
        out.append('<div class="card">%s</div>' % bar_chart(items, "×", width=640))

    out.append("<h2>プログラムごと</h2><div class=\"grid\">")
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

    out.append("<h2>数値</h2><div class=\"tablewrap\"><table><tr><th>program</th><th>実装</th><th>秒 (median)</th>"
               "<th>C 比</th><th>peak RSS (MiB)</th><th>回数</th></tr>")
    for p in data["programs"]:
        base = by.get((p["name"], BASE))
        rows = sorted((r for r in data["rows"] if r["program"] == p["name"]), key=lambda r: r["median_s"])
        for r in rows:
            x = r["median_s"] / base["median_s"] if base else None
            out.append("<tr><td>%s</td><td>%s%s</td><td>%s</td><td>%s</td><td>%.1f</td><td>%d</td></tr>" % (
                html.escape(p["name"]), html.escape(labels[r["impl"]]["label"]), " ※" if r["approx"] else "",
                fmt_s(r["median_s"]), fmt_x(x) if x is not None else "-", r["peak_rss_kib"] / 1024, r["runs"]))
    out.append("</table></div>")
    out.append('<h2>測り方</h2><ul class="note">'
               "<li>1 回の warmup の後に 5 回 (1 回 10 秒を超える実装は 3 回) 走らせた wall の時間の median。プロセスの起動を含む</li>"
               "<li>数値は int32 と float32 に揃えた (tcs の数値は i32 と f32 だけ)。float32 を持たない言語 (※) は float64 で計算し、出力を相対 1e-3 で比べた</li>"
               "<li>1 スレッド、SIMD と unsafe と自前の pool は使わず、各言語の標準の書き方で書いた</li>"
               "<li>C と tcs の C の出力は gcc -O2 -ffp-contract=off -fwrapv</li>"
               "<li>詳細は tcs の perf/compare/METHODOLOGY.md</li></ul>")
    out.append("<h2>版</h2><div class=\"tablewrap\"><table><tr><th>実装</th><th>版</th></tr>")
    for im in data["impls"]:
        out.append("<tr><td>%s</td><td>%s</td></tr>" % (html.escape(im["label"]), html.escape(im["version"])))
    out.append("</table></div></main></body></html>")
    return "\n".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("json")
    ap.add_argument("--md")
    ap.add_argument("--html")
    args = ap.parse_args()
    data, by = load(args.json)
    src = Path(args.json)
    md = Path(args.md) if args.md else src.with_suffix(".md")
    ht = Path(args.html) if args.html else src.with_suffix(".html")
    md.write_text(markdown(data, by) + "\n")
    ht.write_text(page(data, by) + "\n")
    print("wrote %s\nwrote %s" % (md, ht))


if __name__ == "__main__":
    main()

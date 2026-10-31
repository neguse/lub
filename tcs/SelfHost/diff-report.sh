#!/bin/bash
# セルフホストの差分の検査 (TCS_SELFHOST_DIFF=1) の結果を集計する。
# usage: SelfHost/diff-report.sh report.tsv
# report.tsv は TCS_SELFHOST_REPORT に与えたファイル。同じ入力は 1 件に数える。
# 結果: equal (実行の結果が一致)、compiles / rejects (受理 / 拒否が一致)、deferred (Roslyn だけが判定する検査)、
# unsupported (TCS1900)、stricter (セルフホストだけがサブセットの違反を報告)、
# rejected / missed / mismatch / crash (食い違い。test の失敗)
set -euo pipefail
report="${1:?usage: diff-report.sh report.tsv}"
sort -u -t $'\t' -k1,2 "$report" | cut -f1 | sort | uniq -c | sort -rn
# 参照実装と揃った結果 (equal / compiles / rejects) 以外は 1 件ずつ見せる
awk -F '\t' '$1 != "equal" && $1 != "compiles" && $1 != "rejects" && !seen[$1 FS $2]++ { print $1 "\t" $2 "\t" $3 }' "$report"

#!/bin/bash
# セルフホストの差分の検査 (TCS_SELFHOST_DIFF=1) の結果を集計する。
# usage: SelfHost/diff-report.sh report.tsv
# report.tsv は TCS_SELFHOST_REPORT に与えたファイル。同じ入力は 1 件に数える。
set -euo pipefail
report="${1:?usage: diff-report.sh report.tsv}"
sort -u -t $'\t' -k1,2 "$report" | cut -f1 | sort | uniq -c | sort -rn
# 一致以外は 1 件ずつ見せる
awk -F '\t' '$1 != "equal" && !seen[$1 FS $2]++ { print $1 "\t" $2 "\t" $3 }' "$report"

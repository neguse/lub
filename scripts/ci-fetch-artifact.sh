#!/usr/bin/env bash
# CI の同じ run の別の job が上げる artifact を待って、取ってくる。
#   scripts/ci-fetch-artifact.sh <artifact の名前> <置く dir> <上げる job の名前の正規表現>
# needs で待つと、この job は相手が終わるまで始まらない。準備を先に済ませ、要る物ができた所から続けるために、ここで待つ。
# 上げる job が成功以外で終わったら失敗する。job には permissions の actions: read が要る。env: GH_TOKEN
set -euo pipefail
name="$1"
dir="$2"
producer="$3"
api="repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID"
# 上げる job が止まったまま終わらないときに備えて、10 分で諦める
for _ in $(seq 1 200); do
  if gh api "$api/artifacts?per_page=100" --jq '.artifacts[].name' | grep -qxF "$name"; then
    # 一覧に出た直後は、まだ取れないことがある
    for _ in 1 2 3 4 5; do
      part="$(mktemp -d)"
      if gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" --name "$name" --dir "$part"; then
        mkdir -p "$dir"
        cp -R "$part/." "$dir/"
        rm -rf "$part"
        exit 0
      fi
      rm -rf "$part"
      sleep 3
    done
    echo "$name could not be downloaded" >&2
    exit 1
  fi
  bad="$(gh api "$api/jobs?per_page=100" --jq '.jobs[] | select(.conclusion != null and .conclusion != "success") | .name' | grep -E "$producer" || true)"
  if [ -n "$bad" ]; then
    echo "$bad did not succeed" >&2
    exit 1
  fi
  sleep 3
done
echo "$name did not appear" >&2
exit 1

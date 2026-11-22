#!/usr/bin/env bash
# Waits for an artifact uploaded by another job of the same CI run, and fetches it.
#   scripts/ci-fetch-artifact.sh <artifact name> <destination dir> <regex for the name of the uploading job>
# Waiting with needs means this job does not start until the other one finishes. The wait is done here so the preparation is finished first and the job continues as soon as what it needs is ready.
# Fails if the uploading job ends with anything other than success. The job needs actions: read in permissions. env: GH_TOKEN
set -euo pipefail
name="$1"
dir="$2"
producer="$3"
api="repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID"
# Give up after 10 minutes in case the uploading job hangs and never finishes
for _ in $(seq 1 200); do
  if gh api "$api/artifacts?per_page=100" --jq '.artifacts[].name' | grep -qxF "$name"; then
    # Right after it appears in the list, it sometimes cannot be fetched yet
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

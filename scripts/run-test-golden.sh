#!/usr/bin/env bash
# Compatibility wrapper. Visual tests live in scripts/run-golden.sh now.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$repo_root/scripts/run-golden.sh" --tests-only "$@"

#!/usr/bin/env bash
# --serve の形式検査を回す (tests/formal/README.md)。
# 依存: python3 と z3-solver。PYTHON で差し替えられる (例: PYTHON=.venv/bin/python)
set -euo pipefail
cd "$(dirname "$0")"

python=${PYTHON:-python3}
if ! "$python" -c 'import z3' 2>/dev/null; then
  echo "z3 が無い: $python -m pip install z3-solver (または PYTHON=<z3 のある python>)" >&2
  exit 1
fi

exec "$python" serve_bounds.py

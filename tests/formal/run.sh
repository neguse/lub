#!/usr/bin/env bash
# hot reload と --serve の形式検査を一式回す (tests/formal/README.md)。
# それぞれ期待する判定 (OK / INVARIANT / TEMPORAL) を持ち、一致しなければ失敗する。
# 依存:
#   - java。JAVA で起動コマンドを差し替えられる (例: JAVA="mise exec java@25 -- java")
#   - python3 と z3-solver。PYTHON で差し替えられる (例: PYTHON=.venv/bin/python)
#   - TLC の jar (tla2tools.jar) は版と checksum を固定して .cache/ に取ってくる
set -euo pipefail
cd "$(dirname "$0")"

tlc_url="https://github.com/tlaplus/tlaplus/releases/download/v1.7.4/tla2tools.jar"
tlc_sha256="936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88"
jar=".cache/tla2tools.jar"
sha() { if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }
if [[ ! -f $jar ]] || [[ "$(sha "$jar")" != "$tlc_sha256" ]]; then
  mkdir -p .cache
  curl -fsSL -o "$jar" "$tlc_url"
fi
if [[ "$(sha "$jar")" != "$tlc_sha256" ]]; then
  echo "tla2tools.jar checksum mismatch" >&2
  exit 1
fi
export TLC_JAR="$PWD/$jar"
python=${PYTHON:-python3}
if ! "$python" -c 'import z3' 2>/dev/null; then
  echo "z3 が無い: $python -m pip install z3-solver (または PYTHON=<z3 のある python>)" >&2
  exit 1
fi

fail=0
t() { ./tlc.sh "$@" || fail=1; }
N=TcsNative; S=TcsServe
# ---- native (src/main.c + src/app.c) ----
t $N native_base            OK        'MaxBuild = 4' 'BadBuilds = {}'  'PlanMayFail = FALSE' 'ChunkMayFail = FALSE' 'AtomicDeadMtime = FALSE' -- 'INVARIANTS TypeOK GuardOK' 'PROPERTIES Monotone Sync'
t $N native_bad_toctou      TEMPORAL  'MaxBuild = 4' 'BadBuilds = {2}' 'PlanMayFail = FALSE' 'ChunkMayFail = FALSE' 'AtomicDeadMtime = FALSE' -- 'INVARIANTS TypeOK GuardOK' 'PROPERTIES Monotone Sync'
t $N native_bad_atomic      OK        'MaxBuild = 4' 'BadBuilds = {2}' 'PlanMayFail = FALSE' 'ChunkMayFail = FALSE' 'AtomicDeadMtime = TRUE'  -- 'INVARIANTS TypeOK GuardOK' 'PROPERTIES Monotone Sync'
t $N native_planfail        INVARIANT 'MaxBuild = 4' 'BadBuilds = {}'  'PlanMayFail = TRUE'  'ChunkMayFail = FALSE' 'AtomicDeadMtime = TRUE'  -- 'INVARIANTS TypeOK GuardOK'
t $N native_chunkfail_guard INVARIANT 'MaxBuild = 4' 'BadBuilds = {}'  'PlanMayFail = FALSE' 'ChunkMayFail = TRUE'  'AtomicDeadMtime = TRUE'  -- 'INVARIANTS TypeOK GuardOK'
t $N native_chunkfail_sync  TEMPORAL  'MaxBuild = 3' 'BadBuilds = {}'  'PlanMayFail = FALSE' 'ChunkMayFail = TRUE'  'AtomicDeadMtime = TRUE'  -- 'INVARIANTS TypeOK' 'PROPERTIES Sync'
# ---- web (src/serve.c + src/embedded_serve_page.h) ----
t $S serve_base             OK        'MaxBuild = 4' 'BadBuilds = {}'  'PlanMayFail = FALSE' 'ChunkMayFail = FALSE' 'MaxDisrupt = 2' -- 'INVARIANTS TypeOK GuardOK' 'PROPERTIES Monotone Sync'
t $S serve_bad              TEMPORAL  'MaxBuild = 4' 'BadBuilds = {1}' 'PlanMayFail = FALSE' 'ChunkMayFail = FALSE' 'MaxDisrupt = 1' -- 'INVARIANTS TypeOK GuardOK' 'PROPERTIES Sync'
t $S serve_planfail         INVARIANT 'MaxBuild = 4' 'BadBuilds = {}'  'PlanMayFail = TRUE'  'ChunkMayFail = FALSE' 'MaxDisrupt = 1' -- 'INVARIANTS TypeOK GuardOK'
t $S serve_chunkfail        TEMPORAL  'MaxBuild = 3' 'BadBuilds = {}'  'PlanMayFail = FALSE' 'ChunkMayFail = TRUE'  'MaxDisrupt = 0' -- 'INVARIANTS TypeOK' 'PROPERTIES Sync'
# ---- 全列挙と z3 ----
"$python" serve_conns.py || fail=1
"$python" serve_bounds.py || fail=1
if [ $fail = 0 ]; then echo "formal: ALL PASS"; else echo "formal: SOME FAILED" >&2; fi
exit $fail

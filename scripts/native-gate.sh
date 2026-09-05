#!/usr/bin/env bash
# Native regression gate: docs lint, Release build, C smoke tests,
# physics Lua tests, visual goldens (lavapipe), and the C# sample gate.
# Single source of truth shared by linux CI (.github/workflows/linux.yml)
# and the manual full gate (scripts/pre-push.sh).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

require_cs=0
skip_golden=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      cat <<'EOF'
Usage: scripts/native-gate.sh [--require-cs] [--skip-golden]

Runs the native regression gate:
  docs lint, Release build, C smoke tests, physics Lua tests,
  visual goldens (lavapipe), and the C# sample gate.

--require-cs makes a missing C# toolchain (dotnet / third_party/tcs) an
error instead of a skip; CI passes it so the gate cannot silently narrow.
--skip-golden skips the visual golden byte-compares. lavapipe output is
mesa-version-dependent (filtering LSB differences), so the compare only
holds where goldens were generated; linux CI passes this until its mesa
is pinned. C# sample captures still run (crash coverage), only cmp is
skipped.
EOF
      exit 0
      ;;
    --require-cs)
      require_cs=1
      ;;
    --skip-golden)
      skip_golden=1
      ;;
    *)
      echo "unknown arg: $1" >&2
      exit 2
      ;;
  esac
  shift
done

timeout_cmd=()
if command -v timeout >/dev/null 2>&1; then
  timeout_cmd=(timeout 2h)
fi

run() {
  echo
  echo "==> $*"
  "$@"
}

run_timed() {
  echo
  echo "==> $*"
  "${timeout_cmd[@]}" "$@"
}

cleanup_files=()
cleanup() {
  if [[ ${#cleanup_files[@]} -gt 0 ]]; then
    rm -f "${cleanup_files[@]}"
  fi
}
trap cleanup EXIT INT TERM

run scripts/docs-lint.sh

run_timed bash scripts/build-release.sh
native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
run_timed scripts/run-headless.sh "$native_binary" tests/lua/test_fixed_dt.lua \
  --fixed-dt 0.0125
run_timed bash scripts/build-release.sh --target lub_haxe_build_smoke --no-configure
run_timed ./build-release-linux/lub_haxe_build_smoke
run_timed bash scripts/build-release.sh --target lub_physics_box2d_smoke --no-configure
run_timed ./build-release-linux/lub_physics_box2d_smoke
run_timed bash scripts/build-release.sh --target lub_surfacenets_smoke --no-configure
run_timed ./build-release-linux/lub_surfacenets_smoke
run_timed bash scripts/build-release.sh --target lub_sdf_smoke --no-configure
run_timed ./build-release-linux/lub_sdf_smoke

physics_lua_tests=(
  tests/lua/test_physics_box2d.lua
  tests/lua/test_physics_box2d_phase2.lua
  tests/lua/test_physics_box2d_phase3.lua
  tests/lua/test_physics_box2d_debug.lua
  tests/lua/test_physics_box2d_joints.lua
  tests/lua/test_physics_box2d_callbacks.lua
  tests/lua/test_physics_box2d_lifetime.lua
  tests/lua/test_resource_revision.lua
  tests/lua/test_audio.lua
  tests/lua/test_font.lua
  tests/lua/test_api_surface.lua
)
echo
echo "==> physics Lua tests (${#physics_lua_tests[@]} in parallel)"
physics_pids=()
physics_logs=()
for i in "${!physics_lua_tests[@]}"; do
  physics_log="$(mktemp)"
  cleanup_files+=("$physics_log")
  physics_logs+=("$physics_log")
  LUB_XVFB_SERVERNUM=$((300 + i)) "${timeout_cmd[@]}" scripts/run-headless.sh \
    "$native_binary" "${physics_lua_tests[$i]}" >"$physics_log" 2>&1 &
  physics_pids+=("$!")
done
physics_failed=0
for i in "${!physics_lua_tests[@]}"; do
  if wait "${physics_pids[$i]}"; then
    echo "PASS ${physics_lua_tests[$i]}"
  else
    echo "FAIL ${physics_lua_tests[$i]}"
    sed 's/^/    /' "${physics_logs[$i]}"
    physics_failed=1
  fi
done
if [[ $physics_failed -ne 0 ]]; then
  exit 1
fi

if [[ $skip_golden -eq 0 ]]; then
  run_timed env BINARY="$native_binary" scripts/run-golden.sh
else
  echo
  echo "==> visual goldens SKIPPED (--skip-golden: mesa-version-dependent)"
fi

# C# (tcs) サンプル: dotnet と third_party/tcs submodule があるときだけ、
# .cs entry を持つ全サンプルを check + build + golden 比較する。
# 無い環境では skip (C# 対応は optional toolchain)。CI は --require-cs で
# skip を fail に変える。
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
  # API 面の記述 (cs-lib/lub_stub.cs) の検査と、生成物 (tests/lua/test_api_surface.lua)
  # が記述と一致していることの確認。差分が出たら `dotnet run --project tools/lub-gen
  # -- surface-test -o tests/lua/test_api_surface.lua` で再生成する。
  run dotnet run --project tools/lub-gen -- check
  surface_gen="$(mktemp)"
  cleanup_files+=("$surface_gen")
  run dotnet run --project tools/lub-gen --no-build -- surface-test -o "$surface_gen"
  run cmp "$surface_gen" tests/lua/test_api_surface.lua
  shopt -s nullglob
  for cs_dir in samples/*/; do
    cs_dir="${cs_dir%/}"
    cs_files=("$cs_dir"/*.cs)
    ((${#cs_files[@]} == 0)) && continue
    cs_name="$(basename "$cs_dir")"
    # entry class は csproj basename、無ければ唯一の .cs (run-cs-sample と同じ)
    cs_projs=("$cs_dir"/*.csproj)
    if ((${#cs_projs[@]} >= 1)); then
      cs_class="$(basename "${cs_projs[0]}" .csproj)"
    else
      cs_class="$(basename "${cs_files[0]}" .cs)"
    fi
    run scripts/run-cs-sample.sh "$cs_name" --check
    run scripts/run-cs-sample.sh "$cs_name" --build
    for cs_proj in "${cs_projs[@]}"; do
      run dotnet build "$cs_proj" -nologo
    done
    cs_png="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_cs.png"
    rm -f "$cs_png"
    run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "$native_binary" \
      "$cs_dir/.lub/$cs_class.lua" --capture "$cs_png" --capture-frame 240 \
      --fixed-dt 0.0166666666666667
    # golden 比較は Haxe 側と同じ curation (frame 240 が決定的なサンプルのみ)。
    # golden が無いサンプルも capture 実行までは検証される (クラッシュ検出)。
    if [[ $skip_golden -eq 1 ]]; then
      echo "==> golden cmp skipped (--skip-golden): ${cs_name}"
    elif [[ -f "tests/golden/${cs_name}_cs_sdlgpu.png" ]]; then
      run cmp "$cs_png" "tests/golden/${cs_name}_cs_sdlgpu.png"
    else
      echo "==> golden skip (nondeterministic): ${cs_name}"
    fi
  done
  shopt -u nullglob
elif [[ $require_cs -eq 1 ]]; then
  echo "C# sample gate required (--require-cs) but dotnet or third_party/tcs is missing" >&2
  exit 1
else
  echo
  echo "==> C# sample gate skipped (dotnet or third_party/tcs missing)"
fi

echo
echo "native gate OK"

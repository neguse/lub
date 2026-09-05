#!/usr/bin/env bash
# Native regression gate: docs lint, Release build, C smoke tests,
# physics Lua tests, visual goldens (lavapipe), and the C# sample gate
# (tcs→Lua と .NET 実行の両方、digest 比較つき).
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
  # API 面の記述 (cs-lib/lub_stub.cs) の検査と、生成物 (header / Lua binding /
  # surface test) が記述と一致していることの確認。差分が出たら
  # scripts/gen-api.sh で再生成する。
  run scripts/gen-api.sh --check
  # raw Lua 向けの lubx (cs-lib から tcs が生成)。差分が出たら
  # scripts/gen-lubx-lua.sh で再生成する。
  run scripts/gen-lubx-lua.sh --check
  # .NET 実行の共有 library と facade
  run_timed bash scripts/build-release.sh --target lub_shared --no-configure
  run_timed dotnet build dotnet/Lub -nologo -v q
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
    cs_digest="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_cs.digest"
    rm -f "$cs_png" "$cs_digest"
    run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "$native_binary" \
      "$cs_dir/.lub/$cs_class.lua" --capture "$cs_png" --capture-frame 240 \
      --fixed-dt 0.0166666666666667 --digest | tee "${cs_digest}.log"
    grep '^digest ' "${cs_digest}.log" > "$cs_digest" || true
    # golden 比較は Haxe 側と同じ curation (frame 240 が決定的なサンプルのみ)。
    # golden が無いサンプルも capture 実行までは検証される (クラッシュ検出)。
    if [[ $skip_golden -eq 1 ]]; then
      echo "==> golden cmp skipped (--skip-golden): ${cs_name}"
    elif [[ -f "tests/golden/${cs_name}_cs_sdlgpu.png" ]]; then
      run cmp "$cs_png" "tests/golden/${cs_name}_cs_sdlgpu.png"
    else
      echo "==> golden skip (nondeterministic): ${cs_name}"
    fi

    # .NET 実行 (dotnet/SampleRunner): 同じソースを facade + 共有 library で
    # 動かし、frame ごとの digest (C API 呼び出しの構造、--digest) が tcs→Lua と
    # 一致することと、実行形ごとの golden (<name>_dotnet_sdlgpu.png) を確かめる。
    run_timed dotnet build dotnet/SampleRunner -p:Sample="$cs_name" -nologo -v q
    dn_png="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_dotnet.png"
    dn_digest="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_dotnet.digest"
    rm -f "$dn_png" "$dn_digest"
    run_timed env LUB_BACKEND=sdlgpu LUB_NATIVE_LIB="$PWD/build-release-linux/liblub.so" \
      scripts/run-headless.sh "./dotnet/SampleRunner/bin/${cs_name}/lub-sample-${cs_name}" \
      --capture "$dn_png" --capture-frame 240 --fixed-dt 0.0166666666666667 --digest \
      | tee "${dn_digest}.log"
    grep '^digest ' "${dn_digest}.log" > "$dn_digest" || true
    if grep -q "lub: error" "${dn_digest}.log"; then
      echo ".NET run of ${cs_name} logged errors" >&2
      exit 1
    fi
    # 比較は最初の 30 frame。ゲーム内の float (Lua) と double (.NET) の差で
    # game state が割れる前の範囲で、facade の詰め替えの違いはここに出る。
    run cmp <(head -30 "$cs_digest") <(head -30 "$dn_digest")
    if [[ $skip_golden -eq 1 ]]; then
      echo "==> .NET golden cmp skipped (--skip-golden): ${cs_name}"
    elif [[ -f "tests/golden/${cs_name}_dotnet_sdlgpu.png" ]]; then
      run cmp "$dn_png" "tests/golden/${cs_name}_dotnet_sdlgpu.png"
    else
      echo "==> .NET golden skip (nondeterministic): ${cs_name}"
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

# samples/ngs の scenario golden (scene と入力 script を固定した capture)
if [[ $skip_golden -eq 1 ]]; then
  echo "==> ngs scenario golden skipped (--skip-golden)"
elif command -v dotnet >/dev/null 2>&1 && [[ -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
  run_timed env BINARY="$native_binary" scripts/ngs-golden.sh
fi

# raw Lua サンプル (samples/<name>/<name>.lua): lub table と samples/lubx.lua
# だけで動く。capture して golden (<name>_lua_sdlgpu.png) と比べる。
shopt -s nullglob
for lua_entry in samples/*/; do
  lua_entry="${lua_entry%/}"
  lua_name="$(basename "$lua_entry")"
  [[ -f "$lua_entry/$lua_name.lua" ]] || continue
  lua_png="${TMPDIR:-/tmp}/lub-native-gate-${lua_name}_lua.png"
  rm -f "$lua_png"
  run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "$native_binary" \
    "$lua_entry/$lua_name.lua" --capture "$lua_png" --capture-frame 240 \
    --fixed-dt 0.0166666666666667
  if [[ $skip_golden -eq 1 ]]; then
    echo "==> golden cmp skipped (--skip-golden): ${lua_name}"
  elif [[ -f "tests/golden/${lua_name}_lua_sdlgpu.png" ]]; then
    run cmp "$lua_png" "tests/golden/${lua_name}_lua_sdlgpu.png"
  else
    echo "==> golden skip (nondeterministic): ${lua_name}"
  fi
done
shopt -u nullglob

echo
echo "native gate OK"

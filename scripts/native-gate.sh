#!/usr/bin/env bash
# Native regression gate: docs lint, Release build, C smoke tests,
# physics Lua tests, visual goldens (lavapipe), the C# sample gate
# (tcs→Lua と .NET 実行を同じ条件で走らせ、digest と capture が一致すること)、
# ngs scenario golden、raw Lua サンプル。
# Single source of truth shared by the CI linux job (.github/workflows/ci.yml)
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

# ---- C# (tcs) サンプル gate --------------------------------------------
# dotnet と third_party/tcs submodule があるときだけ、.cs entry を持つ全
# サンプルを check + build + golden 比較する。無い環境では skip (C# 対応は
# optional toolchain)。CI は --require-cs で skip を fail に変える。
cs_available=0
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
  cs_available=1
elif [[ $require_cs -eq 1 ]]; then
  echo "C# sample gate required (--require-cs) but dotnet or third_party/tcs is missing" >&2
  exit 1
fi

# entry class は csproj basename、無ければ唯一の .cs (run-cs-sample と同じ)
cs_entry_class() {
  local cs_dir="$1" cs_files cs_projs
  shopt -s nullglob
  cs_files=("$cs_dir"/*.cs)
  cs_projs=("$cs_dir"/*.csproj)
  shopt -u nullglob
  if ((${#cs_projs[@]} >= 1)); then
    basename "${cs_projs[0]}" .csproj
  else
    basename "${cs_files[0]}" .cs
  fi
}

cs_transpile() {
  local cs_dir="$1" cs_name
  cs_name="$(basename "$cs_dir")"
  run scripts/run-cs-sample.sh "$cs_name" --check
  run scripts/run-cs-sample.sh "$cs_name" --build
}

cs_capture() {
  local cs_dir="$1" slot="$2" cs_name cs_class cs_png cs_digest cs_frame
  local dn_png dn_digest
  cs_name="$(basename "$cs_dir")"
  cs_class="$(cs_entry_class "$cs_dir")"
  cs_png="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_cs.png"
  cs_digest="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_cs.digest"
  rm -f "$cs_png" "$cs_digest"
  # 視覚 golden は scripts/run-golden.sh (curation と frame はそちら)。ここは
  # .NET 実行と突き合わせる材料 (digest と capture)。CI (--skip-golden) は
  # digest 比較の範囲 (30 frame) で足りる。
  cs_frame=240
  [[ $skip_golden -eq 1 ]] && cs_frame=30
  run_timed env LUB_BACKEND=sdlgpu LUB_XVFB_SERVERNUM=$((400 + slot)) \
    scripts/run-headless.sh "$native_binary" \
    "$cs_dir/.lub/$cs_class.lua" --capture "$cs_png" --capture-frame "$cs_frame" \
    --fixed-dt 0.0166666666666667 --digest | tee "${cs_digest}.log"
  grep '^digest ' "${cs_digest}.log" > "$cs_digest" || true

  # .NET 実行 (dotnet/SampleRunner): 同じソースを facade + 共有 library で
  # 動かし、frame ごとの digest (C API 呼び出しの構造、--digest) が tcs→Lua と
  # 一致することを確かめる。数値は両方 f32 なので絵も揃うが、libm の実装差
  # (sinf と (float)sin の丸め等) で LSB が違いうるので、capture の一致は
  # golden と同じく手元の gate でだけ見る。
  # SampleRunner の出力は Sample ごとに別 dir なので build は capture の直前。
  run_timed dotnet build dotnet/SampleRunner -p:Sample="$cs_name" -nologo -v q
  dn_png="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_dotnet.png"
  dn_digest="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_dotnet.digest"
  rm -f "$dn_png" "$dn_digest"
  run_timed env LUB_BACKEND=sdlgpu LUB_XVFB_SERVERNUM=$((400 + slot)) \
    LUB_NATIVE_LIB="$repo_root/build-release-linux/liblub.so" \
    scripts/run-headless.sh "./dotnet/SampleRunner/bin/${cs_name}/lub-sample-${cs_name}" \
    --capture "$dn_png" --capture-frame "$cs_frame" --fixed-dt 0.0166666666666667 \
    --digest | tee "${dn_digest}.log"
  grep '^digest ' "${dn_digest}.log" > "$dn_digest" || true
  if grep -q "lub: error" "${dn_digest}.log"; then
    echo ".NET run of ${cs_name} logged errors" >&2
    return 1
  fi
  # digest は最初の 30 frame (CI の capture 長)。facade の詰め替えの違いは
  # ここに出る。
  run cmp <(head -30 "$cs_digest") <(head -30 "$dn_digest")
  if [[ $skip_golden -eq 1 ]]; then
    echo "==> .NET capture cmp skipped (--skip-golden): ${cs_name}"
  else
    run cmp "$cs_png" "$dn_png"
  fi
}

# サンプルごとに独立 (dir が disjoint) な処理を pool で並列化する。
# status はファイル渡し: wait -n が reap した job は後から wait <pid>
# できないため (run-golden.sh と同じ)。相ごとの所要時間を出力する。
# 第 3 引数で pool 幅を上書きできる (既定 cs_jobs_max)。
cs_pool() {
  local label="$1" fn="$2"
  local jobs_max="${3:-$cs_jobs_max}"
  local t0=$SECONDS running=0 i failed=0 tmp
  tmp="$(mktemp -d)"
  for i in "${!cs_dirs[@]}"; do
    (
      if "$fn" "${cs_dirs[$i]}" "$i" >"$tmp/$i.log" 2>&1; then
        echo pass >"$tmp/$i.status"
      else
        echo fail >"$tmp/$i.status"
      fi
    ) &
    running=$((running + 1))
    if ((running >= jobs_max)); then
      wait -n || true
      running=$((running - 1))
    fi
  done
  wait
  for i in "${!cs_dirs[@]}"; do
    if [[ "$(cat "$tmp/$i.status" 2>/dev/null)" != pass ]]; then
      echo "FAIL ${label} ${cs_dirs[$i]}"
      sed 's/^/    /' "$tmp/$i.log" 2>/dev/null || true
      failed=1
    fi
  done
  rm -rf "$tmp"
  echo "==> C# ${label}: ${#cs_dirs[@]} samples in $((SECONDS - t0))s"
  [[ $failed -eq 0 ]]
}

cs_dirs=()
if [[ $cs_available -eq 1 ]]; then
  shopt -s nullglob
  for cs_dir in samples/*/; do
    cs_dir="${cs_dir%/}"
    cs_files=("$cs_dir"/*.cs)
    ((${#cs_files[@]} == 0)) && continue
    cs_dirs+=("$cs_dir")
  done
  shopt -u nullglob
fi
cs_jobs_max="${LUB_CS_JOBS:-$(nproc)}"

# dotnet 相 (Transpiler prebuild / transpile / csproj build) は native binary
# に依存しないので、C build と並行に回して capture の前で join する。
# dotnet run は呼び出しごとに MSBuild 評価が走り 1 回 ~5 秒になるので、
# Transpiler を一度だけ build し、以後は run-cs-sample.sh が LUB_TCS_DLL の
# prebuilt DLL を直接実行する。csproj の dotnet build は直列 (並列だと
# MSBuild/NuGet の取り合いで warm server 直列の ~2s/proj より遅い)。
cs_dotnet_pid=""
if [[ $cs_available -eq 1 ]]; then
  cs_dotnet_log="$(mktemp)"
  cleanup_files+=("$cs_dotnet_log")
  (
    run dotnet build third_party/tcs/Transpiler -c Release -nologo
    tcs_dll="$(find third_party/tcs/Transpiler/bin/Release -name Transpiler.dll -print -quit)"
    if [[ -z "$tcs_dll" ]]; then
      echo "Transpiler.dll not found under third_party/tcs/Transpiler/bin/Release" >&2
      exit 1
    fi
    export LUB_TCS_DLL="$repo_root/$tcs_dll"
    # API 面の記述 (cs-lib/lub_stub.cs) の検査と、生成物 (header / Lua binding /
    # surface test / API docs / facade) が記述と一致していることの確認。差分が
    # 出たら scripts/gen-api.sh で再生成する。
    run scripts/gen-api.sh --check
    # raw Lua 向けの lubx (cs-lib から tcs が生成)。差分が出たら
    # scripts/gen-lubx-lua.sh で再生成する。
    run scripts/gen-lubx-lua.sh --check
    # .NET 実行の facade + host
    run dotnet build dotnet/Lub -nologo -v q
    cs_pool "transpile (check+build)" cs_transpile
    cs_t0=$SECONDS
    for cs_dir in "${cs_dirs[@]}"; do
      shopt -s nullglob
      cs_projs=("$cs_dir"/*.csproj)
      shopt -u nullglob
      for cs_proj in "${cs_projs[@]}"; do
        run dotnet build "$cs_proj" -nologo
      done
    done
    echo "==> C# csproj builds in $((SECONDS - cs_t0))s"
  ) >"$cs_dotnet_log" 2>&1 &
  cs_dotnet_pid=$!
fi
# ------------------------------------------------------------------------

run_timed bash scripts/build-release.sh
native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
run_timed scripts/run-headless.sh "$native_binary" tests/lua/test_fixed_dt.lua \
  --fixed-dt 0.0125
# .NET 実行の共有 library (facade が P/Invoke する)
run_timed bash scripts/build-release.sh --target lub_shared --no-configure
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

# C# gate の残り: 並行していた dotnet 相を join し、capture を回す。
# capture は lavapipe の実レンダリングで CPU 飽和 (llvmpipe が 1 プロセスで
# コア数分の render thread を立てる) ため、多重化しても縮まない。直列で回す。
if [[ $cs_available -eq 1 ]]; then
  echo
  echo "==> C# sample gate (${#cs_dirs[@]} samples, dotnet phases ran alongside the C build)"
  cs_dotnet_ok=0
  wait "$cs_dotnet_pid" && cs_dotnet_ok=1
  sed 's/^/  /' "$cs_dotnet_log"
  if [[ $cs_dotnet_ok -ne 1 ]]; then
    exit 1
  fi
  cs_pool "capture (+.NET digest)" cs_capture 1
else
  echo
  echo "==> C# sample gate skipped (dotnet or third_party/tcs missing)"
fi

# samples/ngs の scenario golden (scene と入力 script を固定した capture)
if [[ $skip_golden -eq 1 ]]; then
  echo "==> ngs scenario golden skipped (--skip-golden)"
elif [[ $cs_available -eq 1 ]]; then
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

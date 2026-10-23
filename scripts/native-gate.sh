#!/usr/bin/env bash
# Native regression gate: docs lint, Release build, C smoke tests,
# C# runtime tests (tests/cs), visual goldens (lavapipe), the C# sample gate
# (tcs→Lua と .NET 実行を同じ条件で走らせ、digest と capture が一致すること)、
# ngs scenario golden。
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
  docs lint, Release build, C smoke tests, C# runtime tests (tests/cs),
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

# goldens compare lavapipe captures; without it run-headless.sh would fall
# back to the machine's GPU driver and every compare would fail.
if [[ $skip_golden -eq 0 ]] &&
  ! compgen -G '/usr/share/vulkan/icd.d/lvp_icd*.json' >/dev/null; then
  echo "goldens need Mesa lavapipe (no /usr/share/vulkan/icd.d/lvp_icd*.json)." >&2
  echo "Install vulkan-swrast (Arch) / mesa-vulkan-drivers (Debian), or pass" >&2
  echo "--skip-golden to run the rest of the gate on this machine's driver." >&2
  exit 1
fi

timeout_cmd=()
if command -v timeout >/dev/null 2>&1; then
  timeout_cmd=(timeout -k 30s 2h)
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
# サンプルを check + build し、.NET 実行と digest / capture を比較する。無い環境では skip (C# 対応は
# optional toolchain)。CI は --require-cs で skip を fail に変える。
cs_available=0
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
  cs_available=1
elif [[ $require_cs -eq 1 ]]; then
  echo "C# sample gate required (--require-cs) but dotnet or third_party/tcs is missing" >&2
  exit 1
fi

if [[ $cs_available -eq 1 ]]; then
  run bash scripts/verify-tcs-binding.sh
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
  # --build は tcs check と同じく警告で失敗する (check と変換を 1 回の compile で)
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
    --fixed-dt 0.0166666666666667 --digest 2>&1 | tee "${cs_digest}.log"
  grep '^digest ' "${cs_digest}.log" > "$cs_digest" || true
  # error の log は stderr に出る (xvfb-run が stdout に混ぜるとは限らない) ので、両方の経路で 2>&1 して log に入れる。
  if grep -q "lua error" "${cs_digest}.log"; then
    echo "tcs→Lua run of ${cs_name} logged errors" >&2
    return 1
  fi

  # .NET 実行 (dotnet/SampleRunner): 同じソースを facade + 共有 library で
  # 動かし、frame ごとの digest (C API 呼び出しの構造、--digest) が tcs→Lua と
  # 一致することを確かめる。数値は両方 f32 で、Math も同じ実装なので絵も揃う。
  # capture の一致は golden と同じく手元の gate でだけ見る。
  # SampleRunner の出力は Sample ごとに別 dir なので build は capture の直前。
  run_timed dotnet build dotnet/SampleRunner -p:Sample="$cs_name" -nologo -v q
  dn_png="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_dotnet.png"
  dn_digest="${TMPDIR:-/tmp}/lub-native-gate-${cs_name}_dotnet.digest"
  rm -f "$dn_png" "$dn_digest"
  run_timed env LUB_BACKEND=sdlgpu LUB_XVFB_SERVERNUM=$((400 + slot)) \
    LUB_NATIVE_LIB="$repo_root/build-release-linux/liblub.so" \
    scripts/run-headless.sh "./dotnet/SampleRunner/bin/${cs_name}/lub-sample-${cs_name}" \
    --capture "$dn_png" --capture-frame "$cs_frame" --fixed-dt 0.0166666666666667 \
    --digest 2>&1 | tee "${dn_digest}.log"
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
    # "$fn" を if や || の条件に置くと、関数の中で set -e が効かず途中の失敗が消える。
    # 条件の外で呼び、途中で落ちたら status を書かずに終わる (status の無い job は失敗)。
    (
      "$fn" "${cs_dirs[$i]}" "$i" >"$tmp/$i.log" 2>&1
      echo pass >"$tmp/$i.status"
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

# dotnet 相 (Transpiler prebuild / transpile / 雛形の build) は native binary
# に依存しないので、C build と並行に回して runtime テストの前で join する。
# dotnet run は呼び出しごとに MSBuild 評価が走り 1 回 ~5 秒になるので、
# Transpiler を一度だけ build し、以後は run-cs-sample.sh と cs_tests_transpile が
# LUB_TCS_DLL の prebuilt DLL を直接実行する。
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
    # API docs / facade) が記述と一致していることの確認。差分が
    # 出たら scripts/gen-api.sh で再生成する。
    run scripts/gen-api.sh --check
    # .NET 実行の facade + host
    run dotnet build dotnet/Lub -nologo -v q
    run dotnet run --project tests/dotnet/Bindings -c Release
    # tests/cs の runtime テストを tests/cs/.lub/<Entry>.lua に変換する
    # shellcheck source=scripts/cs-tests.sh
    source scripts/cs-tests.sh
    run cs_tests_transpile "${cs_all_tests[@]}"
    cs_pool "transpile" cs_transpile
    # サンプルの csproj は build しない: 同じソースを .NET 実行 (SampleRunner) が
    # capture の前に実 .NET で compile し、TinyC# の範囲は tcs の check が見る。
    # 雛形 (cp -r して使う) は他で compile しないので、同じ API で build できること
    run dotnet build templates/game/Game.csproj -nologo
  ) >"$cs_dotnet_log" 2>&1 &
  cs_dotnet_pid=$!
fi
# ------------------------------------------------------------------------

run_timed bash scripts/build-release.sh
native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
# .NET 実行の共有 library (facade が P/Invoke する)
run_timed bash scripts/build-release.sh --target lub_shared --no-configure
run_timed bash scripts/build-release.sh --target lub_tcs_sources_smoke --no-configure
run_timed ./build-release-linux/lub_tcs_sources_smoke
run_timed bash scripts/build-release.sh --target lub_tcs_resolve_smoke --no-configure
run_timed ./build-release-linux/lub_tcs_resolve_smoke
run_timed bash scripts/build-release.sh --target lub_physics_box2d_smoke --no-configure
run_timed ./build-release-linux/lub_physics_box2d_smoke
run_timed bash scripts/build-release.sh --target lub_surfacenets_smoke --no-configure
run_timed ./build-release-linux/lub_surfacenets_smoke
run_timed bash scripts/build-release.sh --target lub_sdf_smoke --no-configure
run_timed ./build-release-linux/lub_sdf_smoke
run_timed bash scripts/build-release.sh --target lub_resources_smoke --no-configure
run_timed ./build-release-linux/lub_resources_smoke
run_timed bash scripts/build-release.sh --target lub_shader_layout_smoke --no-configure
run_timed ./build-release-linux/lub_shader_layout_smoke
run_timed bash scripts/build-release.sh --target lub_xr_math_smoke --no-configure
run_timed ./build-release-linux/lub_xr_math_smoke
# dotnet 相 (サンプルと tests/cs の変換) を join する。ここから先の runtime
# テスト、golden、C# サンプル gate はその出力を使う。
if [[ $cs_available -eq 1 ]]; then
  echo
  echo "==> C# dotnet phases (ran alongside the C build)"
  cs_dotnet_ok=0
  wait "$cs_dotnet_pid" && cs_dotnet_ok=1
  sed 's/^/  /' "$cs_dotnet_log"
  if [[ $cs_dotnet_ok -ne 1 ]]; then
    exit 1
  fi
fi

if [[ $cs_available -eq 1 ]]; then
  # shellcheck source=scripts/cs-tests.sh
  source scripts/cs-tests.sh
  run_timed scripts/run-headless.sh "$native_binary" tests/cs/.lub/TestFixedDt.lua \
    --fixed-dt 0.0125
  # capture の PNG は test が OnQuit で確かめる (同じ path を env でも渡す)
  readback_capture_png="$(mktemp --suffix=.png)"
  cleanup_files+=("$readback_capture_png")
  for no_load in 0 1; do
    run_timed env LUB_BACKEND=sdlgpu \
      LUB_READBACK_CAPTURE_TEST_OUT="$readback_capture_png" \
      LUB_READBACK_CAPTURE_TEST_NO_LOAD="$no_load" \
      scripts/run-headless.sh "$native_binary" tests/cs/.lub/TestReadbackCapture.lua "${cs_test_args[@]}" \
      --capture "$readback_capture_png" --capture-frame 5 \
      --fixed-dt 0.0166666666666667
  done
  # runtime テストは既定で sdlgpu を通る。descriptor を draw ごとに取る Vulkan
  # backend でも、1 フレームの draw が多いときに各 draw が自分の binding で描くこと。
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/TestManyDraws.lua "${cs_test_args[@]}"
  # vulkan backend は texture の束縛を別に組むので、texture と sampler を別に
  # 宣言する shader はこちらでも描く。
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/TestSeparateSampler.lua "${cs_test_args[@]}"
  # compute が読み書きする storage は、vulkan backend でも前の dispatch の内容を保つ。
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/TestStoragePreserve.lua "${cs_test_args[@]}"
  # readback の届く frame は backend によらず、保証しないモードでは GPU を待たない。
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/TestReadbackFixedLatency.lua
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/TestReadbackNoWait.lua
  # lub export は GPU も display も使わず、OnExport で使った shader を export
  # 先の player の target で compile して snapshot の cache/shader/ に入れ、読んだ
  # file と Lua を同じ相対 path に写す。linux の snapshot は、その中で player を
  # 起動するとリポジトリから動かしたときと同じ絵を描く (web は
  # scripts/web-export-gate.sh)。
  export_dir="$(mktemp -d)"
  native_abs="$(realpath "$native_binary")"
  for t in TestVertexPull TestDepthSample; do
    run_timed env -u DISPLAY -u WAYLAND_DISPLAY "$native_binary" export linux \
      "tests/cs/.lub/$t.lua" -o "$export_dir/linux-$t"
    run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "$native_binary" \
      "tests/cs/.lub/$t.lua" --capture "$export_dir/$t-repo.png" \
      --capture-frame 5 --fixed-dt 0.0166666666666667
    (cd "$export_dir/linux-$t" && run_timed env LUB_BACKEND=sdlgpu \
      "$OLDPWD/scripts/run-headless.sh" "$native_abs" game.lua \
      --capture "$export_dir/$t-snapshot.png" --capture-frame 5 \
      --fixed-dt 0.0166666666666667)
    cmp "$export_dir/$t-repo.png" "$export_dir/$t-snapshot.png"
  done
  rm -r "$export_dir"

  echo
  echo "==> C# runtime tests (${#cs_runtime_tests[@]} in parallel)"
  runtime_pids=()
  runtime_logs=()
  for i in "${!cs_runtime_tests[@]}"; do
    runtime_log="$(mktemp)"
    cleanup_files+=("$runtime_log")
    runtime_logs+=("$runtime_log")
    LUB_XVFB_SERVERNUM=$((300 + i)) "${timeout_cmd[@]}" scripts/run-headless.sh \
      "$native_binary" "tests/cs/.lub/${cs_runtime_tests[$i]}.lua" "${cs_test_args[@]}" \
      >"$runtime_log" 2>&1 &
    runtime_pids+=("$!")
  done
  runtime_failed=0
  for i in "${!cs_runtime_tests[@]}"; do
    if wait "${runtime_pids[$i]}"; then
      echo "PASS ${cs_runtime_tests[$i]}"
    else
      echo "FAIL ${cs_runtime_tests[$i]}"
      sed 's/^/    /' "${runtime_logs[$i]}"
      runtime_failed=1
    fi
  done
  if [[ $runtime_failed -ne 0 ]]; then
    exit 1
  fi
  # .NET 実行の Math (TinySystem.props で TinySystem.Math になる) が、tcs→Lua と
  # 同じ期待値 (tests/cs/TestMathDeterminism.cs の表) を返すこと
  run_timed dotnet build dotnet/SampleRunner -p:Test=TestMathDeterminism -nologo -v q
  run_timed env LUB_BACKEND=sdlgpu LUB_NATIVE_LIB="$repo_root/build-release-linux/liblub.so" \
    scripts/run-headless.sh ./dotnet/SampleRunner/bin/TestMathDeterminism/lub-sample-TestMathDeterminism
else
  echo
  echo "==> C# runtime tests skipped (dotnet or third_party/tcs missing)"
fi

if [[ $skip_golden -eq 0 ]]; then
  run_timed env BINARY="$native_binary" scripts/run-golden.sh
else
  echo
  echo "==> visual goldens SKIPPED (--skip-golden: mesa-version-dependent)"
fi

# C# gate の残り: capture を回す。
# capture は lavapipe の実レンダリングで CPU 飽和 (llvmpipe が 1 プロセスで
# コア数分の render thread を立てる) ため、多重化しても縮まない。直列で回す。
if [[ $cs_available -eq 1 ]]; then
  echo
  echo "==> C# sample gate (${#cs_dirs[@]} samples)"
  cs_pool "capture (+.NET digest)" cs_capture 1
  run_timed scripts/test-cs-hot-reload.sh "$native_binary"
else
  echo
  echo "==> C# sample gate skipped (dotnet or third_party/tcs missing)"
fi
# tcs の標準出力の frame (restart_required と知らない種類)。fake の tcs を
# 使うので dotnet は要らない
run_timed scripts/test-tcs-restart.sh "$native_binary"

# samples/ngs の scenario golden (scene と入力 script を固定した capture)
if [[ $skip_golden -eq 1 ]]; then
  echo "==> ngs scenario golden skipped (--skip-golden)"
elif [[ $cs_available -eq 1 ]]; then
  run_timed env BINARY="$native_binary" scripts/ngs-golden.sh
fi

echo
echo "native gate OK"

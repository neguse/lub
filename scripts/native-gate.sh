#!/usr/bin/env bash
# Native regression gate: Release build, C smoke tests, C# runtime tests
# (tests/cs), visual goldens of the tests (lavapipe), the samples' tcs check,
# and the dev tools (tcs の子 process、lub check、vm の project、lider)。
# Single source of truth shared by the CI linux jobs (.github/workflows/ci.yml)
# and the manual full gate (scripts/pre-push.sh).
# CI は --part ごとの job に分けて並べ、手元の gate は全部を順に走らせる。
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

require_cs=0
skip_golden=0
part=all
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      cat <<'EOF'
Usage: scripts/native-gate.sh [--require-cs] [--skip-golden] [--part <name>]

Runs the native regression gate:
  Release build, C smoke tests, C# runtime tests (tests/cs), visual
  goldens of the tests (lavapipe), the samples' tcs check, and the dev
  tools.

--part runs one part (all by default):
  bindings  the tcs and VM bindings, lub-gen (C vs .NET)
  runtime   the C smoke tests, the C# runtime tests on sdlgpu and vulkan,
            the .NET runs, export, the goldens
  tools     the generated files, tests/cs and the samples' tcs check, the
            template's .NET build, the tcs child processes, lub check, the
            bindings digest, the vm projects, the tcs pipeline smoke
  lider     the lider gate (LIDER_SHARD picks a shard of its tests)

--require-cs makes a missing C# toolchain (dotnet / tcs) an
error instead of a skip; CI passes it so the gate cannot silently narrow.
--skip-golden skips the visual golden byte-compares. lavapipe output is
mesa-version-dependent (filtering LSB differences), so the compare only
holds where goldens were generated; linux CI passes this until its mesa
is pinned.
EOF
      exit 0
      ;;
    --require-cs)
      require_cs=1
      ;;
    --skip-golden)
      skip_golden=1
      ;;
    --part)
      part="${2:?--part needs a name}"
      shift
      ;;
    *)
      echo "unknown arg: $1" >&2
      exit 2
      ;;
  esac
  shift
done

case "$part" in
  all | bindings | runtime | tools | lider) ;;
  *)
    echo "unknown part: $part" >&2
    exit 2
    ;;
esac
# want <part>: この起動が <part> を走らせるか
want() { [[ $part == all || $part == "$1" ]]; }
if ! want runtime; then
  # golden は runtime の部分だけが撮る
  skip_golden=1
fi

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

# ---- C# (tcs) ----------------------------------------------------------- dotnet と tcs/ があるときだけ、binding の検査、tests/cs の変換と runtime テスト、全サンプルの tcs check と変換をする。
# 無い環境では skip (C# 対応は optional toolchain)。
# CI は --require-cs で skip を fail に変える。
cs_available=0
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f tcs/Transpiler/Transpiler.csproj ]]; then
  cs_available=1
elif [[ $require_cs -eq 1 ]]; then
  echo "C# toolchain required (--require-cs) but dotnet or tcs is missing" >&2
  exit 1
fi

if [[ $cs_available -eq 1 ]] && want bindings; then
  run bash scripts/verify-tcs-binding.sh
  run bash scripts/verify-vm-binding.sh
  # lub-gen (TinyC#) を C にした実行ファイルが、.NET の lub-gen と同じ出力を出すこと (release の一式が配る lub-gen と同じ作り方)
  lub_gen_build="$(mktemp -d)"
  run bash scripts/build-lub-gen.sh --verify "$lub_gen_build"
  rm -rf "$lub_gen_build"
fi

cs_transpile() {
  local cs_dir="$1" cs_name
  cs_name="$(basename "$cs_dir")"
  # tcs check は警告でも失敗する。tcs vm は警告で止まらないので、check を別に回す
  run scripts/run-cs-sample.sh "$cs_name" --check
  run scripts/run-cs-sample.sh "$cs_name" --vm
}

# サンプルごとに独立 (dir が disjoint) な処理を pool で並列化する。
# status はファイル渡し: wait -n が reap した job は後から wait <pid> できないため (run-golden.sh と同じ)。
# 相ごとの所要時間を出力する。
cs_pool() {
  local label="$1" fn="$2"
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
    if ((running >= cs_jobs_max)); then
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

# dotnet 相 (Transpiler prebuild / transpile / .NET 実行と雛形の build) は native binary に依存しないので、C build と並行に回して runtime テストの前で join する。
# tests/cs の runtime テストを、$3 の backend (空白区切り) ごとに全部、並べて走らせ (一度に core の数の 4 倍まで)、失敗があれば log を出して終わる。
# $2 は test の名前から起動の引数 (実行ファイルと入力) を 1 行ずつ出す関数で、test の名前は $4 以降
run_runtime_tests() {
  local label=$1 argv_fn=$2 backend entry i log failed=0 t0=$SECONDS
  local backends=()
  read -r -a backends <<<"$3"
  shift 3
  local tests=("$@") runs=() pids=() logs=() argv=()
  echo
  echo "==> $label (${#tests[@]} x ${backends[*]}, in parallel)"
  for backend in "${backends[@]}"; do
    for entry in "${tests[@]}"; do
      log="$(mktemp)"
      cleanup_files+=("$log")
      logs+=("$log")
      mapfile -t argv < <("$argv_fn" "$entry")
      # 一度に動かす数を限る。全部を一度に起こすと、lavapipe と Xvfb の memory で機械ごと止まることがある
      while (($(jobs -rp | wc -l) >= cs_jobs_max * 4)); do
        sleep 0.05
      done
      LUB_BACKEND=$backend LUB_XVFB_SERVERNUM=$((300 + ${#runs[@]})) "${timeout_cmd[@]}" \
        scripts/run-headless.sh "${argv[@]}" "${cs_test_args[@]}" >"$log" 2>&1 &
      pids+=("$!")
      runs+=("$entry ($backend)")
    done
  done
  for i in "${!runs[@]}"; do
    if wait "${pids[$i]}"; then
      echo "PASS ${runs[$i]}"
    else
      echo "FAIL ${runs[$i]}"
      sed 's/^/    /' "${logs[$i]}"
      failed=1
    fi
  done
  echo "==> $label: ${#runs[@]} runs in $((SECONDS - t0))s"
  if [[ $failed -ne 0 ]]; then
    exit 1
  fi
}
vm_test_argv() { printf '%s\n' "${vm_tests[@]}" --entry "$1"; }
dotnet_test_argv() { printf '%s\n' "${dotnet_tests[@]}" --entry "$1"; }

# dotnet run は呼び出しごとに MSBuild 評価が走り 1 回 ~5 秒になるので、Transpiler を一度だけ build し、以後は run-cs-sample.sh と scripts/cs-tests.sh が LUB_TCS_DLL の prebuilt DLL を直接実行する。
cs_dotnet_pid=""
if [[ $cs_available -eq 1 ]] && [[ $part != bindings ]]; then
  cs_dotnet_log="$(mktemp)"
  cleanup_files+=("$cs_dotnet_log")
  (
    run dotnet build tcs/Transpiler -c Release -nologo
    # 直前の build の出力 (bin/Release/<framework>/)。RID 付きの古い出力 (bin/Release/<framework>/<rid>/) は拾わない
    tcs_dll="$(find tcs/Transpiler/bin/Release -maxdepth 2 -name Transpiler.dll -print -quit)"
    if [[ -z "$tcs_dll" ]]; then
      echo "Transpiler.dll not found under tcs/Transpiler/bin/Release" >&2
      exit 1
    fi
    export LUB_TCS_DLL="$repo_root/$tcs_dll"
    if want runtime || want tools; then
      # tests/cs 全体の .NET 実行 (dotnet/SampleRunner/bin/tests/lub-sample-tests。test は --entry で選ぶ)
      run dotnet build dotnet/SampleRunner -p:Tests=true -nologo -v q
    fi
    # tests/cs を tcs vm で 1 つの bytecode (tests/cs/.lub/all.tcsb) にする。test は --entry で選ぶ
    # shellcheck source=scripts/cs-tests.sh
    source scripts/cs-tests.sh
    run cs_tests_vm_build
    if want tools; then
      # API 面の記述 (cs-lib/lub_stub.cs) の検査と、生成物 (header / tcs VM の host の関数 / API docs / facade) が記述と一致していることの確認。
      # 差分が出たら scripts/gen-api.sh で再生成する。
      run scripts/gen-api.sh --check
      # .NET 実行の facade + host
      run dotnet build dotnet/Lub -nologo -v q
      run dotnet run --project tests/dotnet/Bindings -c Release
      # tcs vm は警告で止まらないので、tests/cs の警告は tcs check で見る
      run cs_tests_check
      cs_pool "transpile" cs_transpile
      # サンプルの csproj は build しない: サンプルは実行例で、検査は tcs の check と変換まで。
      # 雛形 (cp -r して使う) は実 .NET で build できること
      run dotnet build templates/game/Game.csproj -nologo
    fi
  ) >"$cs_dotnet_log" 2>&1 &
  cs_dotnet_pid=$!
fi
# ------------------------------------------------------------------------

native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
# tests/cs の起動の頭。
# tcs VM の player は tests/cs 全体の bytecode を、.NET 実行は tests/cs 全体を compile した実行ファイルを動かし、どちらも test を --entry <Entry> で選ぶ。
vm_tests=("$native_binary" tests/cs/.lub/all.tcsb)
dotnet_tests=(./dotnet/SampleRunner/bin/tests/lub-sample-tests)
if [[ $part != bindings ]]; then
  run_timed bash scripts/build-release.sh
fi
if want runtime || want tools; then
  # .NET 実行の共有 library (facade が P/Invoke する)
  run_timed bash scripts/build-release.sh --target lub_shared --no-configure
  export LUB_NATIVE_LIB="$repo_root/build-release-linux/liblub.so"
fi
if want runtime; then
  run_timed bash scripts/build-release.sh --target lub_tcs_sources_smoke --no-configure
  run_timed ./build-release-linux/lub_tcs_sources_smoke
  run_timed bash scripts/build-release.sh --target lub_tcs_resolve_smoke --no-configure
  run_timed ./build-release-linux/lub_tcs_resolve_smoke
  run_timed bash scripts/build-release.sh --target lub_vm_project_smoke --no-configure
  run_timed ./build-release-linux/lub_vm_project_smoke
  # 外の project が find_package(lub) で自前の player を作れること (tests/cmake/player)
  run_timed cmake -S tests/cmake/player -B build-release-linux/export-player -G Ninja -Dlub_DIR="$PWD/build-release-linux"
  run_timed cmake --build build-release-linux/export-player
  run_timed bash scripts/build-release.sh --target lub_physics_box2d_smoke --no-configure
  run_timed ./build-release-linux/lub_physics_box2d_smoke
  run_timed bash scripts/build-release.sh --target lub_surfacenets_smoke --no-configure
  run_timed ./build-release-linux/lub_surfacenets_smoke
  run_timed bash scripts/build-release.sh --target lub_sdf_smoke --no-configure
  run_timed ./build-release-linux/lub_sdf_smoke
  run_timed bash scripts/build-release.sh --target lub_resources_smoke --no-configure
  run_timed ./build-release-linux/lub_resources_smoke
  run_timed bash scripts/build-release.sh --target lub_gamepad_smoke --no-configure
  run_timed ./build-release-linux/lub_gamepad_smoke
  run_timed bash scripts/build-release.sh --target lub_window_input_smoke --no-configure
  run_timed ./build-release-linux/lub_window_input_smoke
  run_timed bash scripts/build-release.sh --target lub_shader_layout_smoke --no-configure
  run_timed ./build-release-linux/lub_shader_layout_smoke
  run_timed bash scripts/build-release.sh --target lub_xr_math_smoke --no-configure
  run_timed ./build-release-linux/lub_xr_math_smoke
fi
# dotnet 相 (サンプルと tests/cs の変換) を join する。
# ここから先の runtime テストと golden はその出力を使う。
if [[ -n $cs_dotnet_pid ]]; then
  echo
  echo "==> C# dotnet phases (ran alongside the C build)"
  cs_dotnet_ok=0
  wait "$cs_dotnet_pid" && cs_dotnet_ok=1
  sed 's/^/  /' "$cs_dotnet_log"
  if [[ $cs_dotnet_ok -ne 1 ]]; then
    exit 1
  fi
fi

if [[ $cs_available -eq 1 ]] && want runtime; then
  # shellcheck source=scripts/cs-tests.sh
  source scripts/cs-tests.sh
  # capture の PNG は test が OnQuit で確かめる (同じ path を env でも渡す)
  readback_capture_png="$(mktemp --suffix=.png)"
  cleanup_files+=("$readback_capture_png")
  # 起動の引数や env を付けて走らせる test。
  # $@ は起動の頭 (vm_tests か dotnet_tests)
  cs_individual_tests() {
    local save_home saved no_load
    run_timed scripts/run-headless.sh "$@" --entry TestFixedDt --fixed-dt 0.0125
    run_timed scripts/run-headless.sh "$@" --entry TestFixedDt --fixed-dt 0.0166666666666667
    # save/<名前>/ は $XDG_DATA_HOME/<名前>/ に入る
    save_home="$(mktemp -d)"
    run_timed env XDG_DATA_HOME="$save_home" scripts/run-headless.sh "$@" --entry TestSaveDir "${cs_test_args[@]}"
    saved="$(cat "$save_home/lub-test/scores.txt")"
    rm -rf "$save_home"
    if [[ $saved != saved ]]; then
      echo "TestSaveDir: expected 'saved' in \$XDG_DATA_HOME/lub-test/scores.txt, got '$saved'" >&2
      exit 1
    fi
    for no_load in 0 1; do
      run_timed env LUB_BACKEND=sdlgpu \
        LUB_READBACK_CAPTURE_TEST_OUT="$readback_capture_png" \
        LUB_READBACK_CAPTURE_TEST_NO_LOAD="$no_load" \
        scripts/run-headless.sh "$@" --entry TestReadbackCapture "${cs_test_args[@]}" \
        --capture "$readback_capture_png" --capture-frame 5 \
        --fixed-dt 0.0166666666666667
    done
  }
  cs_individual_tests "${vm_tests[@]}"
  cs_individual_tests "${dotnet_tests[@]}"
  # TestDrawStateReuse は runtime テストの一覧に無い。
  # descriptor set と pipeline の bind を使い回す vulkan backend でも、各 draw が自分の状態で描くこと。
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/all.tcsb --entry TestDrawStateReuse "${cs_test_args[@]}"
  # --readback-fixed-latency を付けない起動でも、readback の届く frame は backend によらず、保証しないモードでは GPU を待たない (runtime テストは付けて走らせる)。
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/all.tcsb --entry TestReadbackFixedLatency
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/all.tcsb --entry TestReadbackNoWait
  # lub export は GPU も display も使わず、OnExport で使った shader を export 先の player の target で compile して snapshot の cache/shader/ に入れ、読んだ file と bytecode (<Entry>.tcsb) を同じ相対 path に写す。
  # linux の snapshot は、その中で player を起動するとリポジトリから動かしたときと同じ絵を描く (web は scripts/web-export-gate.sh)。
  export_dir="$(mktemp -d)"
  native_abs="$(realpath "$native_binary")"
  for t in TestVertexPull TestDepthSample; do
    run_timed env -u DISPLAY -u WAYLAND_DISPLAY "$native_binary" export linux \
      tests/cs/.lub/all.tcsb --entry "$t" -o "$export_dir/linux-$t"
    run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "$native_binary" \
      tests/cs/.lub/all.tcsb --entry "$t" --capture "$export_dir/$t-repo.png" \
      --capture-frame 5 --fixed-dt 0.0166666666666667
    (cd "$export_dir/linux-$t" && run_timed env LUB_BACKEND=sdlgpu \
      "$OLDPWD/scripts/run-headless.sh" "$native_abs" "$t.tcsb" \
      --capture "$export_dir/$t-snapshot.png" --capture-frame 5 \
      --fixed-dt 0.0166666666666667)
    cmp "$export_dir/$t-repo.png" "$export_dir/$t-snapshot.png"
  done
  rm -r "$export_dir"

  run_runtime_tests "C# runtime tests" vm_test_argv "sdlgpu vulkan" "${cs_runtime_tests[@]}"
  # 同じ runtime テストを .NET 実行でも走らせる (走らせないものと理由は scripts/cs-tests.sh の cs_dotnet_excluded)。
  # .NET 実行の Math (TinySystem.props で TinySystem.Math になる) が tcs VM と同じ期待値を返すことは TestMathDeterminism が見る
  mapfile -t dotnet_runtime_tests < <(cs_dotnet_tests)
  # backend は C の runtime の中にあって .NET 実行も同じ所を通るので、.NET 実行は sdlgpu だけで走らせる
  run_runtime_tests "C# runtime tests (.NET)" dotnet_test_argv sdlgpu "${dotnet_runtime_tests[@]}"
  # Blend は値の struct の record。
  # tcs VM の player と .NET 実行の両方で TestBlendValue が通り、frame ごとの digest (draw の Blend を含む) が一致すること
  blend_vm_log="$(mktemp)"
  blend_dn_log="$(mktemp)"
  cleanup_files+=("$blend_vm_log" "$blend_dn_log")
  run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "${vm_tests[@]}" --entry TestBlendValue \
    "${cs_test_args[@]}" --fixed-dt 0.0166666666666667 --digest 2>&1 | tee "$blend_vm_log"
  run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "${dotnet_tests[@]}" --entry TestBlendValue \
    "${cs_test_args[@]}" --fixed-dt 0.0166666666666667 --digest 2>&1 | tee "$blend_dn_log"
  grep -q '^PASS' "$blend_vm_log"
  grep -q '^PASS' "$blend_dn_log"
  grep '^digest ' "$blend_vm_log" >"$blend_vm_log.digest"
  grep '^digest ' "$blend_dn_log" >"$blend_dn_log.digest"
  cleanup_files+=("$blend_vm_log.digest" "$blend_dn_log.digest")
  [[ -s "$blend_vm_log.digest" ]]
  run cmp "$blend_vm_log.digest" "$blend_dn_log.digest"
elif want runtime; then
  echo
  echo "==> C# runtime tests skipped (dotnet or tcs missing)"
fi

if [[ $skip_golden -eq 0 ]]; then
  run_timed env BINARY="$native_binary" scripts/run-golden.sh
elif want runtime; then
  echo
  echo "==> visual goldens SKIPPED (--skip-golden: mesa-version-dependent)"
fi

# 開発の道具: tcs の子 process の後始末、lub check、binding の digest、.csproj から動かす経路
if [[ $cs_available -eq 1 ]] && want tools; then
  run_timed scripts/test-tcs-process-cleanup.sh "$native_binary"
  run_timed scripts/test-lub-check.sh "$native_binary"
  run_timed scripts/test-bindings-digest.sh "$native_binary" "${dotnet_tests[@]}"
  # tcs VM の player が .csproj から動かす経路 (tcs vm --watch、hot reload、OnBuildFailed、App.Use で開いた app の --trace と検査の口)。
  # tcs は dotnet 相が build した Transpiler.dll を直接動かす (dotnet run は起動のたびに MSBuild の評価が走る。tcs を探す順は lub_tcs_resolve_smoke が見る)
  tcs_dll="$(find tcs/Transpiler/bin/Release -maxdepth 2 -name Transpiler.dll -print -quit)"
  run_timed env LUB_VM="$native_binary" LUB_TCS="dotnet $repo_root/$tcs_dll" scripts/vm-project-test.sh
elif want tools; then
  echo
  echo "==> dev tool checks skipped (dotnet or tcs missing)"
fi
# lider (lider/README.md): 自前の player の build と、lider の runtime テスト
if [[ $cs_available -eq 1 ]] && want lider; then
  if command -v cargo >/dev/null; then
    run_timed lider/scripts/gate.sh
  elif [[ $require_cs -eq 1 ]]; then
    echo "lider gate needs cargo" >&2
    exit 1
  else
    echo "==> lider gate skipped (cargo missing)"
  fi
fi
if want tools; then
  # tcs の子プロセスの標準出力から frame を切り出す (知らない種類を読み飛ばす)。
  # fake の tcs を使うので dotnet は要らない
  run_timed bash scripts/build-release.sh --target lub_tcs_pipeline_smoke --no-configure
  run_timed scripts/test-tcs-pipeline-smoke.sh ./build-release-linux/lub_tcs_pipeline_smoke
fi

echo
echo "native gate OK (part: $part)"

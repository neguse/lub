#!/usr/bin/env bash
# Native regression gate: Release build, C smoke tests, C# runtime tests
# (tests/cs), visual goldens of the tests (lavapipe), the samples' tcs check,
# and the dev tools (child processes of tcs, lub check, vm projects, lider).
# Single source of truth shared by the CI linux jobs (.github/workflows/ci.yml)
# and the manual full gate (scripts/pre-push.sh).
# CI splits it into one job per --part and runs them in parallel; the local gate runs everything in order.
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
# want <part>: whether this invocation runs <part>
want() { [[ $part == all || $part == "$1" ]]; }
if ! want runtime; then
  # Only the runtime part takes goldens
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

# ---- C# (tcs) ----------------------------------------------------------- Only when dotnet and src/tcs are present: check the bindings, convert tests/cs and run the runtime tests, and run tcs check and the conversion for all samples.
# Skipped in environments without them (C# support is an optional toolchain).
# CI turns the skip into a fail with --require-cs.
cs_available=0
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f src/tcs/tcs.csproj ]]; then
  cs_available=1
elif [[ $require_cs -eq 1 ]]; then
  echo "C# toolchain required (--require-cs) but dotnet or tcs is missing" >&2
  exit 1
fi

if [[ $cs_available -eq 1 ]] && want bindings; then
  run bash scripts/verify-tcs-binding.sh
  run bash scripts/verify-vm-binding.sh
  # The executable built from lub-gen (TinyC#) converted to C must produce the same output as the .NET lub-gen (built the same way as the lub-gen shipped in the release kit)
  lub_gen_build="$(mktemp -d)"
  run bash scripts/build-lub-gen.sh --verify "$lub_gen_build"
  rm -rf "$lub_gen_build"
fi

cs_transpile() {
  local cs_dir="$1" cs_name
  cs_name="$(basename "$cs_dir")"
  # tcs check fails even on warnings. tcs vm does not stop on warnings, so check is run separately
  run scripts/run-cs-sample.sh "$cs_name" --check
  run scripts/run-cs-sample.sh "$cs_name" --vm
}

# Parallelize the independent per-sample work (disjoint dirs) with a pool.
# Status is passed through files: a job reaped by wait -n cannot be waited on later with wait <pid> (same as run-golden.sh).
# Print the time taken by each phase.
cs_pool() {
  local label="$1" fn="$2"
  local t0=$SECONDS running=0 i failed=0 tmp
  tmp="$(mktemp -d)"
  for i in "${!cs_dirs[@]}"; do
    # Putting "$fn" in the condition of an if or || disables set -e inside the function, and failures in the middle get lost.
    # Call it outside a condition; if it fails midway, it ends without writing a status (a job without a status is a failure).
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

# The dotnet phase (tcs prebuild / transpile / .NET execution and the template build) does not depend on the native binary, so it runs in parallel with the C build and joins before the runtime tests.
# Run all runtime tests of tests/cs in parallel for each backend in $3 (space-separated) (up to 4 times the number of cores at once), and if any fail, print the logs and exit.
# $2 is a function that prints the launch arguments (executable and input) for a test name, one per line, and the test names are $4 onward
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
      # Limit how many run at once. Starting all of them at once can stop the whole machine because of the memory of lavapipe and Xvfb
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

# dotnet run evaluates MSBuild on every call and takes ~5 seconds each time, so the .NET tcs is built only once, and from then on run-cs-sample.sh and scripts/cs-tests.sh run the prebuilt tcs.dll through LUB_TCS.
cs_dotnet_pid=""
if [[ $cs_available -eq 1 ]] && [[ $part != bindings ]]; then
  cs_dotnet_log="$(mktemp)"
  cleanup_files+=("$cs_dotnet_log")
  (
    LUB_TCS="$(scripts/build-tcs-dotnet.sh)"
    export LUB_TCS
    if want runtime || want tools; then
      # .NET execution of all of tests/cs (dotnet/SampleRunner/bin/tests/lub-sample-tests. A test is selected with --entry)
      run dotnet build dotnet/SampleRunner -p:Tests=true -nologo -v q
    fi
    # Compile tests/cs into one bytecode (tests/cs/.lub/all.tcsb) with tcs vm. A test is selected with --entry
    # shellcheck source=scripts/cs-tests.sh
    source scripts/cs-tests.sh
    run cs_tests_vm_build
    if want tools; then
      # Check the description of the API surface (cs-lib/lub_stub.cs), and confirm that the generated files (header / host functions of the tcs VM / API docs / facade) match the description.
      # If there is a diff, regenerate them with scripts/gen-api.sh.
      run scripts/gen-api.sh --check
      # Facade + host for .NET execution
      run dotnet build dotnet/Lub -nologo -v q
      run dotnet run --project tests/dotnet/Bindings -c Release
      # tcs vm does not stop on warnings, so warnings in tests/cs are checked with tcs check
      run cs_tests_check
      cs_pool "transpile" cs_transpile
      # The csproj files of the samples are not built: samples are examples, and the check goes only as far as tcs check and conversion.
      # The template (used via cp -r) must build with real .NET
      run dotnet build templates/game/Game.csproj -nologo
    fi
  ) >"$cs_dotnet_log" 2>&1 &
  cs_dotnet_pid=$!
fi
# ------------------------------------------------------------------------

native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
# Head of the tests/cs launch command.
# The tcs VM player runs the bytecode of all of tests/cs, and .NET execution runs the executable compiled from all of tests/cs; both select the test with --entry <Entry>.
vm_tests=("$native_binary" tests/cs/.lub/all.tcsb)
dotnet_tests=(./dotnet/SampleRunner/bin/tests/lub-sample-tests)
if [[ $part != bindings ]]; then
  run_timed bash scripts/build-release.sh
fi
if want runtime || want tools; then
  # Shared library for .NET execution (the facade P/Invokes it)
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
  # An external project can build its own player with find_package(lub) (tests/cmake/player)
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
# Join the dotnet phase (conversion of the samples and tests/cs).
# The runtime tests and goldens from here on use its output.
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
  # The test checks the capture PNG in OnQuit (the same path is also passed via env)
  readback_capture_png="$(mktemp --suffix=.png)"
  cleanup_files+=("$readback_capture_png")
  # Tests run with launch arguments or env.
  # $@ is the head of the launch command (vm_tests or dotnet_tests)
  cs_individual_tests() {
    local save_home saved no_load
    run_timed scripts/run-headless.sh "$@" --entry TestFixedDt --fixed-dt 0.0125
    run_timed scripts/run-headless.sh "$@" --entry TestFixedDt --fixed-dt 0.0166666666666667
    # save/<name>/ goes into $XDG_DATA_HOME/<name>/
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
  # TestDrawStateReuse is not in the runtime test list.
  # Each draw must render with its own state, even on the vulkan backend, which reuses descriptor set and pipeline binds.
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/all.tcsb --entry TestDrawStateReuse "${cs_test_args[@]}"
  # Even when launched without --readback-fixed-latency, the frame in which a readback arrives is the same regardless of backend, and in the mode without that guarantee it does not wait for the GPU (the runtime tests are run with it).
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/all.tcsb --entry TestReadbackFixedLatency
  run_timed env LUB_BACKEND=vulkan scripts/run-headless.sh "$native_binary" \
    tests/cs/.lub/all.tcsb --entry TestReadbackNoWait
  # lub export uses neither the GPU nor a display; it compiles the shaders used in OnExport for the target of the export destination's player, puts them in cache/shader/ of the snapshot, and copies the files read and the bytecode (<Entry>.tcsb) to the same relative paths.
  # A linux snapshot draws the same picture when the player is started inside it as when run from the repository (for web, see scripts/web-export-gate.sh).
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
  # Run the same runtime tests on .NET execution as well (the ones not run, and why, are in cs_dotnet_excluded of scripts/cs-tests.sh).
  # TestMathDeterminism checks that the Math of .NET execution (which becomes TinySystem.Math through tcs-system.props) returns the same expected values as the tcs VM
  mapfile -t dotnet_runtime_tests < <(cs_dotnet_tests)
  # The backend is inside the C runtime and .NET execution goes through the same code, so .NET execution runs only on sdlgpu
  run_runtime_tests "C# runtime tests (.NET)" dotnet_test_argv sdlgpu "${dotnet_runtime_tests[@]}"
  # Blend is a value struct record.
  # TestBlendValue must pass on both the tcs VM player and .NET execution, and the per-frame digests (including the Blend of draws) must match
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

# Dev tools: cleanup of tcs child processes, lub check, the bindings digest, the path that runs from a .csproj
if [[ $cs_available -eq 1 ]] && want tools; then
  run_timed scripts/test-tcs-process-cleanup.sh "$native_binary"
  run_timed scripts/test-lub-check.sh "$native_binary"
  run_timed scripts/test-bindings-digest.sh "$native_binary" "${dotnet_tests[@]}"
  # The path where the tcs VM player runs from a .csproj (tcs vm --watch, hot reload, OnBuildFailed, --trace and the inspection entry points of apps opened with App.Use).
  # tcs runs the tcs.dll built by the dotnet phase directly (dotnet run evaluates MSBuild on every launch. lub_tcs_resolve_smoke checks the order in which tcs is searched for)
  run_timed env LUB_VM="$native_binary" LUB_TCS="$(scripts/build-tcs-dotnet.sh)" scripts/vm-project-test.sh
elif want tools; then
  echo
  echo "==> dev tool checks skipped (dotnet or tcs missing)"
fi
# lider (lider/README.md): building its own player, and the runtime tests of lider
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
  # Extract frames from the stdout of the tcs child process (skipping unknown kinds).
  # Uses a fake tcs, so dotnet is not needed
  run_timed bash scripts/build-release.sh --target lub_tcs_pipeline_smoke --no-configure
  run_timed scripts/test-tcs-pipeline-smoke.sh ./build-release-linux/lub_tcs_pipeline_smoke
fi

echo
echo "native gate OK (part: $part)"

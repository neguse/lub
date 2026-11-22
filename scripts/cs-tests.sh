# List and conversion of the runtime tests in tests/cs.
# Sourced by scripts/native-gate.sh, apple-gate.sh, run-golden.sh and CI.
# Each test is the entry of tests/cs/<Entry>.csproj, and the inputs are all *.cs in tests/cs and the cs-lib implementation sources (same convention as the lub CLI).
# cs_tests_vm_build turns all of tests/cs into tests/cs/.lub/all.tcsb, and lub (the tcs VM player) runs it with --entry <Entry> selecting the test.
# shellcheck shell=bash

# Tests whose pass/fail is decided by the exit code.
# The native gate runs all of them on both Linux sdlgpu and vulkan, the apple gate on metal, and the CI windows job on d3d12.
# Run with --readback-fixed-latency to fix the frame in which a readback arrives (cs_test_args)
cs_runtime_tests=(
  TestPhysicsBox2d
  TestPhysicsBox2dPhase2
  TestPhysicsBox2dPhase3
  TestPhysicsBox2dDebug
  TestPhysicsBox2dJoints
  TestPhysicsBox2dCallbacks
  TestPhysicsBox2dLifetime
  TestPhysicsNestedQuery
  TestBitMask
  TestResourceRevision
  TestResourceHandles
  TestResourceSweptMessage
  TestResourceSweepDefault
  TestDeferredData
  TestIoText
  TestIoMissing
  TestPick
  TestPickFrame
  TestAtlas
  TestSpriteBatchAtlasUpdate
  TestSpriteBatchOrder
  TestLubxFixedstepEdges
  TestLubxMeshReassert
  TestLubxGameHelpers
  TestLubxKeys
  TestAudio
  TestAudioVoiceKey
  TestVorbis
  TestFont
  TestViewLifetime
  TestRotationConvention
  TestRenderer3dOrientation
  TestRenderer3dViewport
  TestMathValues
  TestDispatchAfterReadback
  TestReadbackOnQuit
  TestReadbackFixedLatency
  TestReadbackNoWait
  TestVertexTexture
  TestSeparateSampler
  TestArenaNestedCall
  TestGfxInOnInitBuffer
  TestGfxInOnInitPass
  TestGfxInOnInitTexture
  TestInstanceCountZero
  TestSweptRefError
  TestShaderKeyConflict
  TestShaderLimits
  TestBufferKeyConflict
  TestSpriteBatchLimit
  TestDupBinding
  TestTextureRecordingOrder
  TestManyDraws
  TestBufferRecordingOrder
  TestBufferArray
  TestBufferInts
  TestBufferCapacity
  TestFrameBuffer
  TestFrameBufferErrors
  TestFrameBufferLimit
  TestBlend
  TestUnboundBindings
  TestUnboundAfterBound
  TestComputeUniformBlock
  TestStoragePreserve
  TestStorageFormat
  TestReadStorageTexture
  TestXrInactive
  TestMathDeterminism
  TestTcsRuntime
  TestLinq
  TestWindowSize
  TestOutParams
  TestBindingsImmediate
  TestBindingsShaderSwap
  TestBindingsRecreate
  TestBindingsLifetime
  TestBindingsDispatch
  TestBindingsMainTex
  TestUniformMatch
  TestGpuPassTime
  TestOpenApp
  TestAppInputs
  TestAppState
  TestAppInput
  TestAppXr
  TestAppObserve
  TestInspectSet
  TestAppTimeline
  TestAppPick
)
# Launch arguments for the runtime tests
cs_test_args=(--readback-fixed-latency)
# Tests whose rendering is checked against goldens (scripts/run-golden.sh)
cs_visual_tests=(TestIndexedDraw TestLoadOp TestDepthSample TestVertexPull TestRenderer3dViewportDraw)
# Tests run individually with launch arguments or env (native-gate.sh / apple-gate.sh).
# TestGamepad is run only by the web verify (A9), which can provide a pad.
# TestKeyMods is run only by the web verify (A9), which can send keys with CapsLock mapped to Ctrl.
# TestSaveDir is run by the native gate with the save location set to a temporary directory (so it does not write to the location of the user's data).
# For TestBindingsDigest, scripts/test-bindings-digest.sh compares the digest.
# TestBlendValue is run by the native gate on the tcs VM player and on .NET execution, and the digests are compared.
# TestXrCapture needs an XR session, so it is run only by the web verify (A9), which can inject a fake session.
# TestUnboundStorage writes to a storage texture from a fragment shader, so it is run only by the web verify (A9).
# sdlgpu can only read graphics storage textures and cannot write them from a fragment shader.
# sdlgpu does not fill the slots not passed in dispatch, and stops on an SDL assert.
# The vulkan backend does not put writable graphics storage textures into the pipeline layout, and in SPIR-V that binding overlaps a StructuredBuffer, so lavapipe crashes on draw.
# Not verified on metal and d3d12.
# TestDrawStateReuse checks how the backend reuses binds, so the native gate runs it on vulkan, the apple gate on metal, the CI windows job on d3d12, and the web verify (A9) on webgpu.
cs_all_tests=("${cs_runtime_tests[@]}" "${cs_visual_tests[@]}" TestFixedDt TestReadbackCapture TestUiRender TestGamepad TestKeyMods TestSaveDir TestXrCapture TestDrawStateReuse TestBindingsDigest TestBlendValue TestUnboundStorage)
# The web verify (A9 of web/scripts/verify-player.mjs) runs all of cs_all_tests on the web player.
# Only the following tests do not apply to web, so they are not run.
# TestReadbackOnQuit checks that a readback queued inside OnQuit arrives right there. The web backend cannot wait for GPU completion on the main thread of the browser, so readbacks do not arrive before the next frame.
cs_web_skip_tests=(TestReadbackOnQuit)
# On .NET execution as well (lub-sample-tests built from dotnet/SampleRunner with -p:Tests=true; a test is selected with --entry <Entry>), the native gate runs the runtime tests and TestFixedDt, TestSaveDir, TestReadbackCapture, TestBindingsDigest, TestBlendValue with the same arguments as on the tcs VM player.
# Of the runtime tests, those in cs_dotnet_excluded are not run on .NET execution.
# App.Use (TestOpenApp, TestAppState, TestAppInput, TestAppXr, TestAppObserve, TestInspectSet, TestAppTimeline, TestAppPick) is available only on the tcs VM player and is an error on .NET execution (App.Use of the stub).
# App.Inputs (TestAppInputs) is collected only by the player that can read .csproj (lub), and returns null on .NET execution.
# TestPick checks the Site of Gfx.Pick. Only the tcs VM player creates Site; it is empty on .NET execution.
# Tests that the native gate reruns on vulkan to check the backend (TestDrawStateReuse etc.) run only on the tcs VM player, because the backend is inside the C runtime and .NET execution goes through the same code.
# The pictures of cs_visual_tests are checked against goldens (tcs VM player).
cs_dotnet_excluded=(TestOpenApp TestAppInputs TestAppState TestAppInput TestAppXr TestAppObserve TestInspectSet TestAppTimeline TestAppPick TestPick)

# Print the runtime tests run on .NET execution (cs_runtime_tests minus cs_dotnet_excluded), one per line.
cs_dotnet_tests() {
  local t
  for t in "${cs_runtime_tests[@]}"; do
    [[ " ${cs_dotnet_excluded[*]} " == *" $t "* ]] || echo "$t"
  done
}

# Inputs of tests/cs: all *.cs in tests/cs and the cs-lib implementation sources.
cs_tests_sources() {
  find tests/cs \( -name bin -o -name obj -o -name '.?*' \) -prune -o -name '*.cs' -print
  find cs-lib -name '*.cs' ! -name 'lub_stub.cs'
}

# If LUB_TCS_DLL is set, run Transpiler.dll directly (dotnet run spends ~5 seconds on MSBuild evaluation every time).
cs_tests_tcs() {
  if [[ -n "${LUB_TCS_DLL:-}" ]]; then
    dotnet "$LUB_TCS_DLL" "$@"
  else
    dotnet run --project tcs/Transpiler -- "$@"
  fi
}

# Compile all of tests/cs into one bytecode (tests/cs/.lub/all.tcsb) with tcs vm.
# The entry of each test is selected with --entry of the tcs VM player (lub_vm tests/cs/.lub/all.tcsb --entry <Entry>).
cs_tests_vm_build() {
  local sources exports=() csproj
  mapfile -t sources < <(cs_tests_sources)
  for csproj in tests/cs/*.csproj; do
    exports+=(--export "$(basename "$csproj" .csproj).*")
  done
  mkdir -p tests/cs/.lub || return 1
  cs_tests_tcs vm "${sources[@]}" --ref cs-lib/lub_stub.cs "${exports[@]}" -o tests/cs/.lub/all.tcsb
}

# Run tcs check over all of tests/cs.
# tcs vm does not stop on warnings, so the check that also fails on warnings is done here (tcs check fails even on warnings).
cs_tests_check() {
  local sources
  mapfile -t sources < <(cs_tests_sources)
  cs_tests_tcs check "${sources[@]}" --ref cs-lib/lub_stub.cs
}

# Run tests in parallel, up to $1 at a time, and print PASS or FAIL in test order. Also print the log of failed tests.
# Each one runs as "$2" <test> <log file> <test number>, and the function $2 writes the log. A non-zero return means failure.
# The log stays as <test>.log in $cs_test_logs (a temporary directory is created if it is not set).
# Usage: cs_run_tests 4 run_one "${cs_runtime_tests[@]}"
cs_run_tests() {
  local jobs=$1 fn=$2 i t running=0 failed=0
  shift 2
  local tests=("$@")
  cs_test_logs="${cs_test_logs:-$(mktemp -d)}"
  for i in "${!tests[@]}"; do
    t="${tests[$i]}"
    rm -f "$cs_test_logs/$t.status"
    # The function is placed to the left of &&, so set -e has no effect inside it. Failures are returned through the exit code
    ("$fn" "$t" "$cs_test_logs/$t.log" "$i" && echo pass >"$cs_test_logs/$t.status") &
    running=$((running + 1))
    if ((running >= jobs)); then
      wait -n || true
      running=$((running - 1))
    fi
  done
  wait
  for t in "${tests[@]}"; do
    if [[ "$(cat "$cs_test_logs/$t.status" 2>/dev/null)" == pass ]]; then
      echo "PASS $t"
    else
      echo "FAIL $t"
      sed 's/^/    /' "$cs_test_logs/$t.log" 2>/dev/null || true
      failed=1
    fi
  done
  return "$failed"
}

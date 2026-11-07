# tests/cs の runtime テストの一覧と変換。
# scripts/native-gate.sh、apple-gate.sh、run-golden.sh と CI が source する。
# 各 test は tests/cs/<Entry>.csproj の entry で、入力は tests/cs の *.cs 全部と cs-lib の実装ソース (lub CLI と同じ規約)。
# cs_tests_vm_build が tests/cs 全体を tests/cs/.lub/all.tcsb にし、lub (tcs VM の player) で --entry <Entry> を選んで動かす。
# shellcheck shell=bash

# 終了コードで合否が決まるもの。
# readback の届く frame を固定するため --readback-fixed-latency を付けて走らせる (cs_test_args)
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
  TestBufferKeyConflict
  TestSpriteBatchLimit
  TestDupBinding
  TestTextureRecordingOrder
  TestManyDraws
  TestBufferRecordingOrder
  TestBufferArray
  TestBufferCapacity
  TestFrameBuffer
  TestFrameBufferErrors
  TestFrameBufferLimit
  TestBlend
  TestUnboundBindings
  TestComputeUniformBlock
  TestStoragePreserve
  TestStorageFormat
  TestXrInactive
  TestMathDeterminism
  TestTcsRuntime
  TestLinq
  TestWindowSize
  TestBindingsImmediate
  TestBindingsShaderSwap
  TestBindingsRecreate
  TestBindingsLifetime
  TestBindingsDispatch
  TestBindingsMainTex
  TestUniformMatch
  TestGpuPassTime
)
# runtime テストに付ける起動引数
cs_test_args=(--readback-fixed-latency)
# 描画を golden で見るもの (scripts/run-golden.sh)
cs_visual_tests=(TestIndexedDraw TestLoadOp TestDepthSample TestVertexPull TestRenderer3dViewportDraw)
# 起動引数や env を付けて個別に走らせるもの (native-gate.sh / apple-gate.sh)。
# TestGamepad は pad を用意できる web の verify (A9) だけが走らせる。
# TestKeyMods は CapsLock を Ctrl にした key を流せる web の verify (A9) だけが走らせる。
# TestSaveDir は保存先を一時ディレクトリにして native gate が走らせる (ユーザーのデータの場所に書かないため)。
# TestBindingsDigest は digest を scripts/test-bindings-digest.sh が比べる。
# TestXrCapture は XR セッションが要るので、偽の session を入れられる web の verify (A9) だけが走らせる。
# TestUnboundStorage は fragment shader から storage texture に書き、native の backend では確かめていないので、web の verify (A9) だけが走らせる。
# TestDrawStateReuse は backend の bind の使い回しを見るので、native gate が vulkan で、apple gate が metal で、CI の windows job が d3d12 で、web の verify (A9) が webgpu で走らせる。
# TestUnboundAfterBound は apple gate が metal で、web の verify (A9) が webgpu で走らせる。
cs_all_tests=("${cs_runtime_tests[@]}" "${cs_visual_tests[@]}" TestFixedDt TestReadbackCapture TestUiRender TestGamepad TestKeyMods TestSaveDir TestXrCapture TestDrawStateReuse TestBindingsDigest TestUnboundAfterBound TestUnboundStorage)

# tests/cs の入力: tests/cs の *.cs 全部と cs-lib の実装ソース。
cs_tests_sources() {
  find tests/cs \( -name bin -o -name obj -o -name '.?*' \) -prune -o -name '*.cs' -print
  find cs-lib -name '*.cs' ! -name 'lub_stub.cs'
}

# LUB_TCS_DLL があれば Transpiler.dll を直接動かす (dotnet run は 1 回 ~5 秒の MSBuild 評価が走る)。
cs_tests_tcs() {
  if [[ -n "${LUB_TCS_DLL:-}" ]]; then
    dotnet "$LUB_TCS_DLL" "$@"
  else
    dotnet run --project third_party/tcs/Transpiler -- "$@"
  fi
}

# tests/cs 全体を tcs vm で 1 つの bytecode (tests/cs/.lub/all.tcsb) にする。
# 各 test の entry は tcs VM の player の --entry で選ぶ (lub_vm tests/cs/.lub/all.tcsb --entry <Entry>)。
cs_tests_vm_build() {
  local sources exports=() csproj
  mapfile -t sources < <(cs_tests_sources)
  for csproj in tests/cs/*.csproj; do
    exports+=(--export "$(basename "$csproj" .csproj).*")
  done
  mkdir -p tests/cs/.lub || return 1
  cs_tests_tcs vm "${sources[@]}" --ref cs-lib/lub_stub.cs "${exports[@]}" -o tests/cs/.lub/all.tcsb
}

# tests/cs 全体を tcs check にかける。
# tcs vm は警告で止まらないので、警告も失敗にする検査はこちらで見る (tcs check は警告でも失敗する)。
cs_tests_check() {
  local sources
  mapfile -t sources < <(cs_tests_sources)
  cs_tests_tcs check "${sources[@]}" --ref cs-lib/lub_stub.cs
}

# tests/cs の runtime テストの一覧と変換。
# scripts/native-gate.sh、apple-gate.sh、run-golden.sh と CI が source する。
# 各 test は tests/cs/<Entry>.csproj の entry で、入力は tests/cs の *.cs 全部と cs-lib の実装ソース (lub CLI と同じ規約)。
# cs_tests_transpile が tests/cs/.lub/<Entry>.lua に変換し、それを lub で動かす。
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
  TestResourceSweepDefault
  TestDeferredData
  TestIoText
  TestIoMissing
  TestAtlas
  TestLubxFixedstepEdges
  TestLubxMeshReassert
  TestLubxGameHelpers
  TestAudio
  TestVorbis
  TestFont
  TestViewLifetime
  TestRotationConvention
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
  TestDupBinding
  TestTextureRecordingOrder
  TestManyDraws
  TestBufferRecordingOrder
  TestUnboundBindings
  TestComputeUniformBlock
  TestStoragePreserve
  TestXrInactive
  TestMathDeterminism
  TestTcsRuntime
  TestWindowSize
)
# runtime テストに付ける起動引数
cs_test_args=(--readback-fixed-latency)
# 描画を golden で見るもの (scripts/run-golden.sh)
cs_visual_tests=(TestIndexedDraw TestLoadOp TestDepthSample TestVertexPull)
# 起動引数や env を付けて個別に走らせるもの (native-gate.sh / apple-gate.sh)。
# TestGamepad は pad を用意できる web の verify (A9) だけが走らせる。
# TestSaveDir は保存先を一時ディレクトリにして native gate が走らせる (ユーザーのデータの場所に書かないため)。
cs_all_tests=("${cs_runtime_tests[@]}" "${cs_visual_tests[@]}" TestFixedDt TestReadbackCapture TestUiRender TestGamepad TestSaveDir)

# tests/cs 全体を 1 回だけ変換し (tcs check と同じく警告があれば失敗)、引数の entry ごとに `return <Entry>` を足して tests/cs/.lub/<Entry>.lua にする (tcs の --entry が足すのと同じ 1 行。entry ごとに tcs を回すと、どれも同じ program を丸ごと compile し直す)。
# LUB_TCS_DLL があれば Transpiler.dll を直接動かす (dotnet run は 1 回 ~5 秒の MSBuild 評価が走る)。
cs_tests_transpile() {
  local tcs=(dotnet run --project third_party/tcs/Transpiler --)
  [[ -n "${LUB_TCS_DLL:-}" ]] && tcs=(dotnet "$LUB_TCS_DLL")
  local sources entry all=tests/cs/.lub/all.lua
  mapfile -t sources < <(
    find tests/cs \( -name bin -o -name obj -o -name '.?*' \) -prune -o -name '*.cs' -print
    find cs-lib -name '*.cs' ! -name 'lub_stub.cs'
  )
  mkdir -p tests/cs/.lub || return 1
  "${tcs[@]}" "${sources[@]}" --ref cs-lib/lub_stub.cs --warnaserror -o "$all" || return 1
  for entry in "$@"; do
    { cat "$all"; printf 'return %s\n' "$entry"; } >"tests/cs/.lub/$entry.lua" || return 1
  done
}

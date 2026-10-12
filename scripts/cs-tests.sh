# tests/cs の runtime テストの一覧と変換。scripts/native-gate.sh、apple-gate.sh、
# run-golden.sh と CI が source する。
# 各 test は tests/cs/<Entry>.csproj の entry で、入力は tests/cs の *.cs 全部と
# cs-lib の実装ソース (lub CLI と同じ規約)。cs_tests_transpile が
# tests/cs/.lub/<Entry>.lua に変換し、それを lub で動かす。
# shellcheck shell=bash

# 終了コードで合否が決まり、引数なしで走るもの
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
  TestDeferredData
  TestIoText
  TestAtlas
  TestLubxFixedstepEdges
  TestLubxMeshReassert
  TestLubxGameHelpers
  TestAudio
  TestFont
  TestViewLifetime
  TestRotationConvention
  TestDispatchAfterReadback
  TestReadbackOnQuit
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
)
# 描画を golden で見るもの (scripts/run-golden.sh)
cs_visual_tests=(TestIndexedDraw TestLoadOp TestDepthSample TestVertexPull)
# 起動引数や env を付けて個別に走らせるもの (native-gate.sh / apple-gate.sh)
cs_all_tests=("${cs_runtime_tests[@]}" "${cs_visual_tests[@]}" TestFixedDt TestReadbackCapture)

# tcs の診断 (TinyC# の範囲外など) を確かめてから、引数の entry を並列に変換する。LUB_TCS_DLL があれば Transpiler.dll を直接
# 動かす (dotnet run は 1 回 ~5 秒の MSBuild 評価が走る)。
cs_tests_transpile() {
  local tcs=(dotnet run --project third_party/tcs/Transpiler --)
  [[ -n "${LUB_TCS_DLL:-}" ]] && tcs=(dotnet "$LUB_TCS_DLL")
  local sources
  mapfile -t sources < <(
    find tests/cs \( -name bin -o -name obj -o -name '.?*' \) -prune -o -name '*.cs' -print
    find cs-lib -name '*.cs' ! -name 'lub_stub.cs'
  )
  "${tcs[@]}" check "${sources[@]}" --ref cs-lib/lub_stub.cs || return 1
  mkdir -p tests/cs/.lub
  printf '%s\n' "$@" | xargs -P "$(nproc 2>/dev/null || echo 4)" -I{} \
    "${tcs[@]}" "${sources[@]}" --ref cs-lib/lub_stub.cs \
    -o "tests/cs/.lub/{}.lua" --entry "{}"
}

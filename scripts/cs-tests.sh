# tests/cs の runtime テスト (描画 golden を持たず、終了コードで合否が決まる
# もの)。scripts/native-gate.sh と scripts/apple-gate.sh が source する。
# 各 test は tests/cs/<Entry>.csproj の entry で、入力は tests/cs の *.cs 全部と
# cs-lib の実装ソース (lub CLI と同じ規約)。cs_tests_transpile が
# tests/cs/.lub/<Entry>.lua に変換し、それを lub で動かす。
# shellcheck shell=bash
cs_runtime_tests=(
  TestBitMask
  TestGfxInOnInitBuffer
  TestGfxInOnInitPass
  TestGfxInOnInitTexture
)

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

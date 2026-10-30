#!/usr/bin/env bash
# lub check <entry> が、窓を開かずゲームも走らせずに C# を検査し、終了コードで答えることを見る。
# 使い方: scripts/test-lub-check.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

tcs_dll="${LUB_TCS_DLL:-$(find third_party/tcs/Transpiler/bin/Release -name Transpiler.dll -print -quit)}"
if [[ -z "$tcs_dll" ]]; then
  echo "Transpiler.dll not found (dotnet build third_party/tcs/Transpiler -c Release)" >&2
  exit 1
fi
export LUB_TCS="dotnet $tcs_dll"
# 画面が無くても動くこと
unset DISPLAY WAYLAND_DISPLAY

fail() {
  cat "$work/log"
  echo "FAIL: $1" >&2
  exit 1
}

# サブディレクトリの .cs、cs-lib の実装 (Lubx)、stub (Lub) を入力に含むので通る
mkdir -p "$work/good/sub"
touch "$work/good/Good.csproj"
cat >"$work/good/Good.cs" <<'CS'
using Lub;
using Lubx;

public static class Good
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = Helper.Size, Height = Helper.Size });
    }

    public static void OnFrame(float dt)
    {
        Color c = Color.Rgb(1.0f, 1.0f, 1.0f);
    }
}
CS
cat >"$work/good/sub/Helper.cs" <<'CS'
public static class Helper
{
    public const int Size = 64;
}
CS
"$binary" check "$work/good/Good.csproj" >"$work/log" 2>&1 || fail "a valid entry was rejected"
grep -q "backend selected" "$work/log" && fail "the check started the game"
[[ -e "$work/good/.lub" ]] && fail "the check wrote a build output"

mkdir "$work/bad"
touch "$work/bad/Bad.csproj"
cat >"$work/bad/Bad.cs" <<'CS'
public static class Bad
{
    public static void OnFrame(float dt)
    {
        NoSuchType.Call();
    }
}
CS
code=0
"$binary" check "$work/bad/Bad.csproj" >"$work/log" 2>&1 || code=$?
[[ $code -eq 1 ]] || fail "a broken entry should exit 1, got $code"
grep -q "Bad.cs.*NoSuchType" "$work/log" || fail "the diagnostic does not name the problem"

code=0
"$binary" check >"$work/log" 2>&1 || code=$?
[[ $code -eq 1 ]] || fail "no entry should exit 1, got $code"
grep -q "usage: lub check" "$work/log" || fail "no entry should print the usage"

code=0
"$binary" check "$work/missing/Missing.csproj" >"$work/log" 2>&1 || code=$?
[[ $code -eq 1 ]] || fail "a missing entry should exit 1, got $code"

echo "lub check: a valid entry passes without starting the game, a broken one exits 1 with its diagnostic"

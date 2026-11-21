#!/usr/bin/env bash
# Checks that lub check <entry> checks the C# without opening a window or running the game, and answers with the exit code.
# Usage: scripts/test-lub-check.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

tcs_dll="${LUB_TCS_DLL:-$(find tcs/Transpiler/bin/Release -name Transpiler.dll -print -quit)}"
if [[ -z "$tcs_dll" ]]; then
  echo "Transpiler.dll not found (dotnet build tcs/Transpiler -c Release)" >&2
  exit 1
fi
export LUB_TCS="dotnet $tcs_dll"
# Must work without a display
unset DISPLAY WAYLAND_DISPLAY

fail() {
  cat "$work/log"
  echo "FAIL: $1" >&2
  exit 1
}

# Passes because the inputs include the .cs in subdirectories, the cs-lib implementation (Lubx) and the stub (Lub)
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

# Sees the same inputs as lub <csproj>: the .cs of a Lub="Source" reference are inputs too, and *.ref.cs are declaration-only references (--ref)
mkdir -p "$work/shared" "$work/uses"
touch "$work/shared/Shared.csproj"
cat >"$work/shared/Shared.cs" <<'CS'
public static class Shared
{
    public static int Twice(int v) { return v * 2; }
}
CS
cat >"$work/uses/Uses.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../shared/Shared.csproj" Lub="Source" />
  </ItemGroup>
</Project>
XML
cat >"$work/uses/Native.ref.cs" <<'CS'
public static class Native
{
    public static int Get() { return 0; }
}
CS
cat >"$work/uses/Uses.cs" <<'CS'
public static class Uses
{
    public static void OnFrame(float dt)
    {
        int v = Shared.Twice(Native.Get());
    }
}
CS
"$binary" check "$work/uses/Uses.csproj" >"$work/log" 2>&1 || fail "an entry with a Lub=\"Source\" reference and a *.ref.cs was rejected"
[[ -e "$work/uses/.lub" ]] && fail "the check wrote a build output"

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

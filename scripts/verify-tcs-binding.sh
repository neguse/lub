#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
cflags=(-std=gnu11 -O2 -fwrapv -ffp-contract=off -fexcess-precision=standard -Iinclude -Ithird_party/SDL/include -I"$work")
dotnet build third_party/tcs/tcs2c/tcs2c.csproj -c Release
dotnet build tools/lub-gen/lub-gen.csproj -c Release
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs tests/c/tcs_binding.cs -o "$work/game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_binding.cs -o "$work/binding.c"
cp tests/c/tcs_binding.c "$work/test.c"
printf '\nint main(void) { tcs_lib_init(); tcs_entry_BindingTest_Main(); return 0; }\n' >> "$work/test.c"
"${CC:-cc}" "${cflags[@]}" "$work/test.c" -lm -o "$work/test"
[[ "$("$work/test" | tr -d '\r')" == $'0\n7\n123\n1\ntrue\ntrue\n9\n4\ntrue\n48000\n6\nb\ntrue\n2\n8\ntrue\ntrue\n10\n3\n2.5\ntrue\ntrue\n11\ntrue\n2\n1\n0000000000000abc' ]]
echo 'tcs2c binding: main target, handles, dictionaries, array and list uniforms, inherited options, nullable XR views, byte views, record results, record lists, list results, maybe results, view methods, nullable scalar results, nullable record outs, optional record fields, bit masks and callbacks passed'

# bit mask の文字列は空と 17 桁以上が error (Lua と .NET の binding と同じ規則)。
for mask_case in 'EmptyMask:empty hex string' 'LongMask:more than 16 hex digits'; do
  entry=${mask_case%%:*}
  cp tests/c/tcs_binding.c "$work/$entry.c"
  printf '\nint main(void) { tcs_lib_init(); tcs_entry_BindingTest_%s(); return 0; }\n' "$entry" >> "$work/$entry.c"
  "${CC:-cc}" "${cflags[@]}" "$work/$entry.c" -lm -o "$work/$entry"
  if "$work/$entry" 2> "$work/$entry.err"; then
    echo "tcs2c binding: $entry did not fault" >&2
    exit 1
  fi
  grep -qF "bit mask: ${mask_case#*:}" "$work/$entry.err"
done
echo 'tcs2c binding: empty and over-long bit masks fault'

# tcs2c の生成 C の Math (生成 C に埋め込まれた tcs の runtime/math) が、Lua の経路と同じ結果になること。
# 期待値は tests/cs/TestMathDeterminism.cs の表 (OS ごとに分けない 1 つ)。
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll tests/c/tcs_math.cs -o "$work/math_game.c"
"${CC:-cc}" "${cflags[@]}" -fno-fast-math "$work/math_game.c" -lm -o "$work/math"
"$work/math" | tr -d '\r' > "$work/math.out"
sed -nE 's/^ *"([a-z0-9]+ = -?[0-9]+)",$/\1/p' tests/cs/TestMathDeterminism.cs > "$work/math.expected"
[[ -s "$work/math.out" ]]
if grep -vxFf "$work/math.expected" "$work/math.out"; then
  echo 'tcs2c math: the digests above differ from tests/cs/TestMathDeterminism.cs' >&2
  exit 1
fi
echo "tcs2c math: $(wc -l < "$work/math.out") functions match tests/cs/TestMathDeterminism.cs"

# cs-lib (lub.Math + lubx) 全体がゲームと同じ経路 (tcs2c → lub-gen tcs → C) を通ること。
# 使う API は多いので C の実装は link せず、compile まで確かめる。
lubx=(cs-lib/lub/Math.cs cs-lib/lubx/*.cs)
lubx_sources=()
for source in "${lubx[@]}"; do lubx_sources+=(--source "$source"); done
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs "${lubx[@]}" -o "$work/lubx_game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  "${lubx_sources[@]}" -o "$work/lubx_binding.c"
printf '#include "lubx_game.c"\n#include "lubx_binding.c"\n' > "$work/lubx.c"
"${CC:-cc}" "${cflags[@]}" -c "$work/lubx.c" -o "$work/lubx.o"
echo "tcs2c binding: cs-lib (${#lubx[@]} files) generated and compiled"

# src/tcs_host.c が entry class 名に依らず、OnQuit の無いゲームでも組み上がること。
# 13_sprites は class が Sprites13 で OnQuit を持たない。
# host の object に未解決の tcs_entry_* が残らなければ、runtime と link できる。
host_sources=(samples/13_sprites/Sprites13.cs "${lubx[@]}")
host_args=()
for source in "${host_sources[@]}"; do host_args+=(--source "$source"); done
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs "${host_sources[@]}" -o "$work/host_game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  "${host_args[@]}" -o "$work/host_binding.c"
grep -qF 'static void tcs_game_on_quit(void) {}' "$work/host_binding.c"
"${CC:-cc}" "${cflags[@]}" -Isrc -DLUB_TCS_GAME="\"$work/host_game.c\"" \
  -DLUB_TCS_BINDING="\"$work/host_binding.c\"" -c src/tcs_host.c -o "$work/host.o"
if nm -u "$work/host.o" | grep tcs_entry_; then
  echo 'tcs host: unresolved tcs_entry_* symbols (an entry hook is not defined by the game)' >&2
  exit 1
fi
echo "tcs host: entry class Sprites13 without OnQuit compiled and linked cleanly"

# src/tcs_host.c が poll した event を entry の OnEvent に渡すこと。
# 偽の lub_host_* (tests/c/tcs_event_host.c) が決めた event 列を流す。
# Quit を受けたら残りの event は渡さず、その frame は回さない。
# ゲームは受け取った EventData を List に持ち越すので、毎 frame の境界で旧世代まで GC する TCS_GC_STRESS でも同じ出力になること。
# frame を始められない周回 (偽の frame_begin が frame 1 で一度 false を返す) では、次の周回の前に 16ms 待つこと。
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs tests/c/tcs_event.cs -o "$work/event_game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_event.cs -o "$work/event_binding.c"
cat > "$work/event.expected" <<'OUT'
init
event 2 key=4 button=0 x=0 y=0 dy=0
event 4 key=0 button=1 x=10.5 y=20 dy=0
frame 1 2:4:0 4:0:10.5
event 7 key=0 button=0 x=0 y=0 dy=-1
delay 16
frame 2 2:4:0 4:0:10.5 7:0:0
event 3 key=4 button=0 x=0 y=0 dy=0
quit frames=2 seen=4
OUT
for stress in '' -DTCS_GC_STRESS=1; do
  "${CC:-cc}" "${cflags[@]}" -Isrc $stress -DLUB_TCS_GAME="\"$work/event_game.c\"" \
    -DLUB_TCS_BINDING="\"$work/event_binding.c\"" src/tcs_host.c tests/c/tcs_event_host.c \
    -lm -o "$work/event"
  "$work/event" | tr -d '\r' | diff "$work/event.expected" -
done
echo "tcs host: OnEvent receives events until Quit, and kept EventData survive GC stress"

# OnEvent は static void OnEvent(Lub.EventData) でなければ entry にしない。
for bad in 'void OnEvent(int key) { }' 'int OnEvent(EventData e) { return 0; }' 'void OnEvent() { }'; do
  printf 'using Lub;\nstatic class Game\n{\n    public static %s\n    public static void OnFrame(float dt) { }\n}\n' "$bad" > "$work/bad_event.cs"
  if dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
    --source "$work/bad_event.cs" -o "$work/bad_event.c" 2> "$work/bad_event.err"; then
    echo "tcs host: lub-gen accepted $bad" >&2
    exit 1
  fi
  grep -qF 'OnEvent must be static void (Lub.EventData)' "$work/bad_event.err"
done
echo "tcs host: lub-gen rejects OnEvent without a single EventData parameter or with a result"

# --entry の解決: namespace 付きの class は C# の名前 (MyGame.Game) でも指定でき、static OnFrame の無い class は entry にできない。
cat > "$work/entry.cs" <<'CS'
namespace MyGame
{
    public static class Game
    {
        public static void OnFrame(float dt) { }
    }
    public static class Helper
    {
        public static void Run() { }
    }
}
CS
lub_gen=(dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs --source "$work/entry.cs")
"${lub_gen[@]}" -o "$work/entry_default.c"
"${lub_gen[@]}" --entry MyGame.Game -o "$work/entry_display.c"
"${lub_gen[@]}" --entry MyGame_Game -o "$work/entry_il.c"
for generated in default display il; do
  grep -qF 'tcs_entry_MyGame_Game_OnFrame(dt)' "$work/entry_$generated.c"
done
if "${lub_gen[@]}" --entry MyGame.Helper -o "$work/entry_helper.c" 2> "$work/entry_helper.err"; then
  echo 'tcs host: --entry accepted a class without static OnFrame' >&2
  exit 1
fi
grep -q 'no static OnFrame' "$work/entry_helper.err"
echo "tcs host: --entry accepts the C# name and rejects a class without OnFrame"

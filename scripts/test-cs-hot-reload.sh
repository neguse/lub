#!/usr/bin/env bash
# C# entry の hot reload で、初期化済みの状態が更新後の method から読めることを見る。
# lub <Entry>.csproj (tcs --watch) を起動し、lub を止めている間 (デバッガ停止と
# 同じ) に 2 回書き換える: 1 回目で instance field を足し、2 回目でそれを使う。
# 再開後に 2 つの reload chunk が順に当たれば、足した field も初期化されている。
# 状態や field を見失うと OnFrame が Lua error になり PASS が出ない。
# 使い方: scripts/test-cs-hot-reload.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
work="$(mktemp -d)"
pid=
paused=
cleanup() {
  [[ -n "$paused" ]] && kill -CONT $paused 2>/dev/null || true
  [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
  rm -rf "$work"
}
trap cleanup EXIT

touch "$work/HotReload.csproj"
cat > "$work/HotReload.cs" <<'EOF'
using System;
using Lub;

public class State
{
    public int V;
}

public static class HotReload
{
    static State? state;
    static int last;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
        state = new State { V = 42 };
    }

    public static void OnFrame(float dt)
    {
        var value = state!.V + 1;
        if (value != last)
        {
            last = value;
            Console.WriteLine("HOT_RELOAD value=" + value);
        }
        if (value == 470)
        {
            Console.WriteLine("HOT_RELOAD_PASS");
            App.Quit();
        }
    }
}
EOF

scripts/run-headless.sh "$binary" "$work/HotReload.csproj" > "$work/log" 2>&1 &
pid=$!

wait_for() {
  local pattern="$1" tries="$2"
  for _ in $(seq 1 "$tries"); do
    grep -q "$pattern" "$work/log" && return 0
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
  grep -q "$pattern" "$work/log"
}

# 初回 transpile (dotnet の cold start 込み) と最初の frame を待ってから書き換える
if ! wait_for "HOT_RELOAD value=43" 1800; then
  cat "$work/log"
  echo "FAIL: the first frame did not run" >&2
  exit 1
fi
# lub (と xvfb-run などの wrapper) だけを止める。子の tcs (引数は .cs のパス)
# は止めず、止めている間に rebuild させる
paused=$(pgrep -f 'HotReload\.csproj')
kill -STOP $paused
sed -i -e 's/public int V;/public int V;\n    public int Bonus = 5;/' \
  -e 's/state!.V + 1;/state!.V + state.Bonus;/' "$work/HotReload.cs"
sleep 3
sed -i 's/state!.V + state.Bonus;/(state!.V + state.Bonus) * 10;/' "$work/HotReload.cs"
sleep 3
kill -CONT $paused
paused=
if ! wait_for "HOT_RELOAD_PASS" 600; then
  cat "$work/log"
  echo "FAIL: the edits made while paused did not apply in order" >&2
  exit 1
fi
wait "$pid" || true
pid=
echo "cs hot reload: edits made while paused apply in order and keep the state"

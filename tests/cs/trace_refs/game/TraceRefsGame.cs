// overlay の参照先からだけ辿れる参照 (TraceRefsLib) は --trace で変換しない。
// ゲーム、overlay (TraceRefsTool)、overlay が Lub="Source" で参照する TraceRefsLib を別のディレクトリに置く。
// overlay は毎 frame TraceRefsLib を呼び、Sys.TakeTrace にゲームの行が入り、TraceRefsLib と overlay の行が入らないことを見る。
// lub tests/cs/trace_refs/game/TraceRefsGame.csproj で動かす。
using System;
using Lub;

public static class TraceRefsGame
{
    public static int Frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Frames++;
    }
}

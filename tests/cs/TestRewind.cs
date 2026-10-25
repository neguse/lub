// Rewind: overlay (TestRewindTool) がゲームの時間を止めて戻す。
// ゲームは毎 frame、static の数を 1 増やし、List に積む。
// 止めている間は、毎 frame 止めた位置の状態に戻してから OnFrame を呼び直すので、数と List の長さは毎回同じ値になる。
// 再開すると、止めた位置から続く。
// lub tests/cs/TestRewind.csproj で動かす (overlay は csproj の entry でだけ付く)。
using System;
using System.Collections.Generic;
using Lub;

public static class TestRewind
{
    public static int Count;
    static List<int> history = new List<int>();

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Count++;
        history.Add(Count);
        // 戻されない overlay の側に、この frame で見た値を渡す
        TestRewindTool.GameCount = Count;
        TestRewindTool.GameHistory = history.Count;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

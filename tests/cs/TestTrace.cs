// Sys.TakeTrace: overlay があるとき、ゲームのソースを tcs の --trace で変換し、実行した行を返す。
// lub tests/cs/TestTrace.csproj で動かす (overlay は TestTraceTool)。
// TestTraceTool が、通った行 (HitLine) が返り、通らなかった行 (MissLine) が返らないことを確かめる。
// 行番号はこの file の中の位置なので、行を足したら HitLine と MissLine を合わせる。
using System;
using Lub;

public static class TestTrace
{
    public const int HitLine = 21;
    public const int MissLine = 23;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames < 0)
            frames = 0;
    }
}

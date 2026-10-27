// Sys.TakeTrace と Sys.TakeValues: overlay があるとき、ゲームのソースを tcs の --trace で変換し、実行した行と代入した値を返す。
// lub tests/cs/TestTrace.csproj で動かす (overlay は TestTraceTool)。
// TestTraceTool が、通った行 (HitLine) が返り、通らなかった行 (MissLine) が返らないことと、代入した値が文字になって返ることを確かめる。
// 行番号はこの file の中の位置なので、行を足したら HitLine と MissLine と、TestTraceTool の site を合わせる。
using System;
using System.Collections.Generic;
using Lub;

public class Spot
{
    public int X;
    public int Y;
}

public static class TestTrace
{
    public const int HitLine = 31;
    public const int MissLine = 33;
    public static int frames;
    static string label = "";
    static Spot? at;
    static List<int>? seen;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames < 0)
            frames = 0;
        label = "frame " + frames;
        at = new Spot { X = frames, Y = 2 };
        seen = new List<int> { 1, 2 };
    }
}

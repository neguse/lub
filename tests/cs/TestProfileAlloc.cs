// LUB_PROFILE の Lua の確保量と GC の数え方を確かめる材料。
// フレームごとに LUB_PROFILE_ALLOC_TEST_N 個の object を作って捨てる。
// 数値の判定は scripts/test-profile-alloc.sh が log を見て行う。
using System;
using Lub;

public class ProfileAllocGarbage
{
    public int V;
}

public static class TestProfileAlloc
{
    const int frames = 120;
    static readonly int count = int.Parse(Environment.GetEnvironmentVariable("LUB_PROFILE_ALLOC_TEST_N") ?? "0");
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        for (int i = 0; i < count; i++)
        {
            var garbage = new ProfileAllocGarbage();
            garbage.V = i;
        }
        frame++;
        if (frame == frames)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}

// 行ごとの負荷のテスト (profile_test.c が呼ぶ)。時間は Probe.Clock.Advance が知らせる分だけ経つ。
public static class Game
{
    static int[]? keep;

    public static void Step()
    {
        Probe.Clock.Advance(100);
        Inner();
        Lib.Work();
        keep = new int[100];
    }

    static void Inner()
    {
        Probe.Clock.Advance(20);
    }

    public static void Churn()
    {
        for (int i = 0; i < 3000; i++)
            keep = new int[1000];
    }
}

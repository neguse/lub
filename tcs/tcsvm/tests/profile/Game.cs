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

    // 持ち越し: static (kept、items、chain) か前からある object の field (held.item) に置いた物だけを数える
    static Item? kept;
    static Holder held = new Holder();
    static System.Collections.Generic.List<Item> items = new System.Collections.Generic.List<Item>();
    static Holder? chain;

    public static void Keep()
    {
        var temp = new Item();
        temp.v = 1;
        kept = new Item();
        held.item = new Item();
        items.Add(new Item());
        var h = new Holder();
        h.item = new Item();
        chain = h;
    }

    // 別の thread が時間を知らせ終えるまで回る
    public static void Spin()
    {
        while (!Probe.Clock.Done())
        {
        }
    }
}

public class Item
{
    public int v;
}

public class Holder
{
    public Item? item;
}

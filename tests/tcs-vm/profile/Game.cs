// Per-line cost test (called by profile_test.c). Time advances only by the amount Probe.Clock.Advance reports.
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

    // Kept: counts only objects placed in statics (kept, items, chain) or in a field of a pre-existing object (held.item)
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

    // Spins until another thread finishes reporting time
    public static void Spin()
    {
        while (!Probe.Clock.Done())
        {
        }
    }

    // Time and allocations after returning from a called --trace function (Mark) are attributed to the calling line
    public static void Nest()
    {
        Lib.Spend(Mark());
    }

    // The host calls Mark nested and reports time after it returns
    public static void Around()
    {
        Probe.Clock.Around();
    }

    public static int Mark()
    {
        return 100;
    }

    // Looks up, from inside a host function, the line that called it
    public static void Where()
    {
        Probe.Clock.Where();
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

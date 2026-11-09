using System;

// out 引数の呼び出し: 別の class の static method を class 名で呼ぶ形 (文と式)、同じ class、instance method
public static class Foo
{
    public static bool Two(int a, out int x, out int y)
    {
        x = a + 1;
        y = a + 2;
        return true;
    }

    public static void One(int a, out int x)
    {
        x = a * 10;
    }
}

public class Counter
{
    public int N = 4;

    public bool Next(out int v)
    {
        v = N;
        N++;
        return v < 6;
    }
}

public static class OutCalls
{
    static void Local(out string s)
    {
        s = "local";
    }

    public static void Main()
    {
        Foo.Two(1, out var x, out var y);
        Foo.One(3, out var z);
        bool ok = Foo.Two(5, out var p, out var q);
        Console.WriteLine(x + " " + y + " " + z + " " + ok + " " + p + " " + q);
        Local(out var s);
        // 捨てる out
        Console.WriteLine((float.TryParse("1.5", out _) ? "num" : "no") + " " + (int.TryParse("x", out _) ? "num" : "no"));
        Foo.Two(9, out _, out var only);
        Console.WriteLine(only);
        var c = new Counter();
        while (c.Next(out var v))
            Console.WriteLine(s + " " + v);
    }
}
